using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class OcrLifecycleDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [Fact]
    public Task Stored_execution_forward_hold_transfer_is_bound_and_replayable() => RunScriptAsync("ocr-stored-forward-hold-contract.ps1");

    [Fact]
    public Task Unproved_initial_drain_does_not_repeat_during_protected_journal_failure() => RunScriptAsync("incremental-initial-drain-failure.ps1");

    [Fact]
    public Task Physical_verifier_and_release_failure_boundaries_fail_closed_and_replay_safely() => RunScriptAsync("ocr-maintenance-contract.ps1");

    [Fact]
    public Task Actual_updater_preflight_dispatch_and_completed_replay_are_bound_and_fail_closed() => RunScriptAsync("ocr-updater-contract.ps1");

    [Fact]
    public Task Preparation_refuses_binding_drift_before_creating_or_publishing_a_candidate() => RunScriptAsync("ocr-preflight-order-contract.ps1");

    [Fact]
    public Task Scoped_updater_resolves_real_helpers_and_persists_failure_containment() => RunScriptAsync("ocr-scoped-updater-contract.ps1");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_schema_maintenance_composes_protected_start_and_hold_release_and_emits_a_supported_recovery_packet(bool storedExecution)
    {
        var area=Path.Combine(Path.GetTempPath(),"FluxOcrPacket_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            await RunScriptAsync("ocr-cleanup-admission-contract.ps1",packetDirectory:area,storedExecution:storedExecution);
            var index=Path.Combine(area,"Index");Directory.CreateDirectory(index);
            var audit=new RecoveryIndexReferenceAudit(index).Capture(Path.Combine(area,"Recovery"));
            Assert.Equal(1,audit.PacketCount);Assert.Empty(audit.Pins);
        }
        finally {Directory.Delete(area,true);}
    }

    [NativeSqlServerFact]
    public async Task Pinned_migration_is_additive_atomic_replayable_and_rejects_schema_drift()
    {
        await using var db = SqlTestData.CreateFactory(fixture).CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        const string baseline = "20261021010911_AddIndexRecoveryReferenceAudit";
        const string target = "20261024061418_AddDocumentOcrExecutionLifecycle";
        var root = FluxKnowledge.Testing.RepositoryFiles.Root();
        var expected = migrator.GenerateScript(baseline, target).Replace("\r\n", "\n");
        var actual = await File.ReadAllTextAsync(Path.Combine(root, "scripts/deploy/ocr-lifecycle-schema-up.sql"));
        Assert.Equal(expected, actual);
        Assert.Equal("3A66996FC45853B16E7329B5D51C1E10DDD0F05F9AC842C702591E516115DC20",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
        await migrator.MigrateAsync(baseline);
        try { await RunScriptAsync("ocr-lifecycle-migration-rehearsal.ps1", fixture.ConnectionString); }
        finally { await db.Database.MigrateAsync(); }
    }

    internal static async Task RunScriptAsync(string name, string? connection = null,string? packetDirectory=null,bool storedExecution=false)
    {
        var root = FluxKnowledge.Testing.RepositoryFiles.Root();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root, "tests/native", name), "-SourceRoot", root }) start.ArgumentList.Add(argument);
        start.Environment["FLUXKNOWLEDGE_OCR_CONTRACT_ASSEMBLY"] = typeof(FluxKnowledge.Application.Documents.DocumentOcrExecutionLease).Assembly.Location;
        if (connection is not null) start.Environment["FLUXKNOWLEDGE_OCR_DISPOSABLE_SQL"] = connection;
        if (packetDirectory is not null) start.Environment["FLUXKNOWLEDGE_OCR_PACKET_TEST_ROOT"] = packetDirectory;
        if (storedExecution) start.Environment["FLUXKNOWLEDGE_OCR_STORED_EXECUTION"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(budget.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}
