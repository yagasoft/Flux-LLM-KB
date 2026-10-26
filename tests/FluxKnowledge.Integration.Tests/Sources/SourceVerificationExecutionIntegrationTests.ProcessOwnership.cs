using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Another_process_cannot_take_the_slot_after_SQL_session_and_lease_loss_until_native_cleanup_finishes()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        // With pooling disabled, closing this owned connection disconnects its
        // real SQL session rather than returning it to a pool.
        await using var session = new SqlConnection(new SqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
        await session.OpenAsync();
        Assert.Equal(fixture.DatabaseName, session.Database);
        var oldStore = new SqlSourceVerificationStore(new SharedVerificationSessionFactory(session), TimeProvider.System,
            scenario.Options, scenario.Incremental);
        await oldStore.StartAsync(scenario.RootId, true, default);
        var first = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("old-process-owner", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10), default));
        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        var reader = new WindowsSourceVerificationContentReader(TimeProvider.System)
        {
            ConfigureNativeLease = lease => lease.NativeCompletionObserved = () =>
            {
                cleanupReached.TrySetResult();
                if (!releaseCleanup.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Owned native cleanup probe was not released");
            }
        };
        var worker = new SourceVerificationWorker(oldStore, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            reader, scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        var work = Task.Run(() => worker.ScanAsync(first.SourceRoot, first.ScanRequest, default).AsTask());
        try
        {
            await cleanupReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(work.IsCompleted);
            await session.CloseAsync();
            Assert.Equal(System.Data.ConnectionState.Closed, session.State);
            var recoveredClock = new OffsetClock { Offset = TimeSpan.FromMinutes(11) };
            var higher = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("replacement-process-owner",
                recoveredClock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
            Assert.Equal(first.ScanRequest.Id, higher.ScanRequest.Id);
            Assert.True(higher.LeaseGeneration > first.LeaseGeneration);
            var recoveredStore = new SqlSourceVerificationStore(scenario.Factory, recoveredClock, scenario.Options, scenario.Incremental);
            var replacement = new SourceVerificationWorker(recoveredStore, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
                new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, recoveredClock);
            await scenario.Control.CompleteAsync(higher,
                await replacement.ScanAsync(higher.SourceRoot, higher.ScanRequest, default), null, default);
            var status = (await recoveredStore.ReadAsync(scenario.RootId, default))!;
            Assert.Equal("reader-busy", status.Run!.Reason);
            Assert.Equal(0, status.Run.Verified);
            Assert.Equal(0, status.Run.SourceBytesRead);
            Assert.Null(status.LastVerifiedAtUtc);
            Assert.Equal("busy", await ProbeSlotInSeparateProcessAsync(scenario.Runtime));
            Assert.False(work.IsCompleted);
            releaseCleanup.Set();
            var oldFailure = await Record.ExceptionAsync(async () => await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.NotNull(oldFailure);
            Assert.True(oldFailure is DbException or SourceSnapshotChangedException or InvalidOperationException, oldFailure.ToString());
            Assert.Equal("acquired", await ProbeSlotInSeparateProcessAsync(scenario.Runtime));
            await using var db = scenario.Factory.CreateDbContext();
            Assert.False(await db.SourceVerificationItems.AnyAsync(x => x.ReceiptJson != null));
        }
        finally
        {
            releaseCleanup.Set();
            try { await work.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch when (work.IsCompleted) { }
        }
    }

    private sealed class SharedVerificationSessionFactory(SqlConnection connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).Options);
    }

    private static async Task<string> ProbeSlotInSeparateProcessAsync(string runtime)
    {
        var script = Path.Combine(runtime, "slot-process-probe.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$AssemblyDirectory,[string]$RuntimeDirectory)
            $ErrorActionPreference = 'Stop'
            foreach ($name in @('FluxKnowledge.Domain.dll','FluxKnowledge.Application.dll','FluxKnowledge.Integrations.dll')) {
                $null = [Reflection.Assembly]::LoadFrom((Join-Path $AssemblyDirectory $name))
            }
            $slot = [FluxKnowledge.Integrations.Files.WindowsSourceVerificationReaderSlot]::new($RuntimeDirectory).TryAcquire()
            if ($null -eq $slot) { [Console]::Out.WriteLine('busy') }
            else { try { [Console]::Out.WriteLine('acquired') } finally { $slot.Dispose() } }
            """);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
            "-AssemblyDirectory", Path.GetDirectoryName(typeof(WindowsSourceVerificationReaderSlot).Assembly.Location)!, "-RuntimeDirectory", runtime })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }
}
