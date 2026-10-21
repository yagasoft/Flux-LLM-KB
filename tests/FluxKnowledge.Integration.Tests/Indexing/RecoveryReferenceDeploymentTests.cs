using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class RecoveryReferenceDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task The_exact_schema_bootstrap_is_atomic_repeatable_and_refuses_unreviewed_state()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261014134909_AddResumableSourceDeletionCleanup");
        try { await RunDeploymentScriptAsync("segmented-index-schema.ps1"); }
        finally { await migrator.MigrateAsync(); }
    }

    [NativeSqlServerFact]
    public async Task Updater_continuations_and_Outlook_receipts_use_the_actual_guarded_writer_lifetime()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        await RunDeploymentScriptAsync("recovery-reference-deployment.ps1");
    }

    private async Task RunDeploymentScriptAsync(string script)
    {
        var source = SourceRoot();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(source, "tests", "native", script),
            "-SourceRoot", source, "-ExpectedDatabase", fixture.DatabaseName }) start.ArgumentList.Add(value);
        start.Environment["FLUXKNOWLEDGE_RECOVERY_REFERENCE_TEST_SQL"] = fixture.ConnectionString;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } throw; }
        Assert.True(child.ExitCode == 0, (await output) + (await error));
        Assert.Contains("PASS:", await output);
    }

    [NativeSqlServerTheory]
    [InlineData("complete")]
    [InlineData("lost-session")]
    [InlineData("external-incomplete")]
    public async Task The_actual_PowerShell_writer_excludes_audits_and_retains_unfinished_process_ownership(string mode)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var scratch = Path.Combine(Path.GetTempPath(), "FluxKnowledgeRecoveryWriter_" + Guid.NewGuid().ToString("N"));
        var recovery = Path.Combine(scratch, "Recovery"); var index = Path.Combine(scratch, "Index");
        Directory.CreateDirectory(recovery); Directory.CreateDirectory(index);
        var store = new SqlIndexRecoveryReferences(SqlTestData.CreateFactory(fixture), TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        await store.AuditAsync(recovery, index, CancellationToken.None);
        var source = SourceRoot();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(source, "tests", "native", "recovery-reference-writer.ps1"),
            "-SourceRoot", source, "-ScratchRoot", scratch, "-ExpectedDatabase", fixture.DatabaseName, "-Mode", mode })
            start.ArgumentList.Add(value);
        start.Environment["FLUXKNOWLEDGE_RECOVERY_REFERENCE_TEST_SQL"] = fixture.ConnectionString;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        Process? descendant = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(scratch, "ready")))
            {
                Assert.False(child.HasExited, (await OutputIfExitedAsync(child, output, error)));
                await Task.Delay(20, deadline.Token);
            }
            await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
            Assert.False((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
            var owner = Assert.Single(await db.IndexRecoveryWriters.AsNoTracking().ToArrayAsync());
            Assert.Equal(mode == "lost-session", owner.OwnerExitSufficient);
            Assert.Equal(child.Id, owner.OwnerProcessId);
            Assert.Equal(new DateTimeOffset(child.StartTime.ToUniversalTime()), owner.OwnerStartedAtUtc);
            var probe = new WindowsInteractiveGpuOwnerProbe();
            Assert.Equal(probe.Current.MachineFingerprint, owner.OwnerMachineFingerprint);
            Assert.Equal(GpuInteractiveOwnerObservation.Alive, probe.Observe(new(owner.OwnerProcessId, owner.OwnerStartedAtUtc, owner.OwnerMachineFingerprint)));
            if (mode == "complete")
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AuditAsync(recovery, index, cancellation.Token));
            }
            else
                Assert.Equal("index-recovery-writer-exit-not-proven", (await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await store.AuditAsync(recovery, index, CancellationToken.None))).Message);
            await File.WriteAllTextAsync(Path.Combine(scratch, "release"), "continue");
            await child.WaitForExitAsync(deadline.Token);
            if (mode == "external-incomplete")
            {
                var identity = (await File.ReadAllTextAsync(Path.Combine(scratch, "descendant-owner"))).Split('|');
                descendant = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture), descendant.StartTime.ToUniversalTime().Ticks);
                Assert.False(descendant.HasExited); // The deployment parent really exited first.
                await RefuseUnreconciledAsync();
                await File.WriteAllTextAsync(Path.Combine(scratch, "descendant-release"), "continue");
                await descendant.WaitForExitAsync(deadline.Token);
                await RefuseUnreconciledAsync(); // Even a later observed exit cannot stand in for explicit reconciliation.
                Assert.Single(await db.IndexRecoveryWriters.AsNoTracking().ToArrayAsync());
                Assert.False((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);
                Assert.True(child.ExitCode == 0, (await output) + (await error));
                Assert.Contains("PASS:", await output);
                return;
            }
            Assert.True(child.ExitCode == 0, (await output) + (await error));
            Assert.Contains("PASS:", await output);
            await store.AuditAsync(recovery, index, CancellationToken.None);
            Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
            Assert.True((await db.IndexRecoveryAudit.AsNoTracking().SingleAsync()).IsValid);

            async Task RefuseUnreconciledAsync() => Assert.Equal("index-recovery-writer-reconciliation-required",
                (await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AuditAsync(recovery, index, CancellationToken.None))).Message);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (descendant is not null)
            {
                if (!descendant.HasExited) { descendant.Kill(entireProcessTree: true); await descendant.WaitForExitAsync(); }
                descendant.Dispose();
            }
            await output; await error;
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<string> OutputIfExitedAsync(Process process, Task<string> output, Task<string> error) =>
        process.HasExited ? (await output) + (await error) : "The isolated PowerShell writer exited before admission.";
    private static string SourceRoot([CallerFilePath] string source = "") => Directory.GetParent(Path.GetDirectoryName(source)!)!.Parent!.Parent!.FullName;
}
