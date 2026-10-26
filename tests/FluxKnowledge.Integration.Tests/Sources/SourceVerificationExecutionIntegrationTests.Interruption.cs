using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_or_cancellation_between_chunks_retains_actual_bytes_and_releases_native_ownership(bool cancel)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "one.txt"), new string('a', 256 * 1024));
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "two.txt"), new string('b', 256 * 1024));
        await scenario.BaselineAsync();
        var inner = scenario.Verification;
        await inner.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("interrupt", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var intercepted = new CheckpointHook(inner, (_, _) => Task.CompletedTask, beforeReadCheck: async () =>
        {
            if (++checks != 3) return;
            if (cancel) cancellation.Cancel();
            else
            {
                await using var db = scenario.Factory.CreateDbContext();
                await db.SourceRootScanStates.ExecuteUpdateAsync(set => set.SetProperty(x => x.VerificationPaused, true));
            }
        });
        var worker = new SourceVerificationWorker(intercepted, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, cancellation.Token).AsTask());
        else await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, cancellation.Token);
        var status = (await inner.ReadAsync(scenario.RootId, default))!;
        Assert.Equal(128 * 1024, status.Run!.SourceBytesRead);
        Assert.Equal(0, status.Run.RetainedBytesRead);
        Assert.Equal(0, status.Run.Verified);
        Assert.Equal(cancel ? "cancelled" : "foreground-or-policy", status.Run.Reason);
        using var free = new WindowsSourceVerificationReaderSlot(scenario.Runtime).TryAcquire();
        Assert.NotNull(free);
        using var writer = File.Open(Path.Combine(scenario.Sources, "one.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [NativeSqlServerFact]
    public async Task Higher_claim_after_worker_failure_resumes_completed_items_and_reuses_only_actual_cycle_blob_proof()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "two.txt"), new string('a', 64));
        await scenario.BaselineAsync();
        var clock = new OffsetClock();
        var inner = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental);
        var run = await inner.StartAsync(scenario.RootId, true, default);
        var first = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("first", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        var intercepted = new CheckpointHook(inner, (_, _) => Task.CompletedTask, afterCheckpoint: accepted =>
        { if (accepted) throw new InvalidOperationException("fixture-worker-interrupted-after-checkpoint"); });
        var firstWorker = new SourceVerificationWorker(intercepted, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstWorker.ScanAsync(first.SourceRoot, first.ScanRequest, default).AsTask());
        await scenario.Control.CompleteAsync(first, new(first.SourceRoot.Id, first.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, "fixture-worker-interrupted", default);
        Guid completedId; string receipt;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var completed = await db.SourceVerificationItems.SingleAsync(x => x.RunId == run && x.State == "Verified");
            completedId = completed.Id; receipt = completed.ReceiptJson!;
        }
        clock.Offset = TimeSpan.FromMinutes(2);
        var second = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("higher", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        Assert.Equal(first.ScanRequest.Id, second.ScanRequest.Id);
        Assert.True(second.ScanRequest.Lease!.Generation > first.ScanRequest.Lease!.Generation);
        var secondWorker = new SourceVerificationWorker(inner, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, clock);
        await scenario.Control.CompleteAsync(second, await secondWorker.ScanAsync(second.SourceRoot, second.ScanRequest, default), null, default);
        var status = (await inner.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("Verified", status.Run!.State);
        Assert.Equal(2, status.Run.Verified);
        Assert.Equal(128, status.Run.SourceBytesRead);
        Assert.Equal(64, status.Run.RetainedBytesRead);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(receipt, await after.SourceVerificationItems.Where(x => x.Id == completedId).Select(x => x.ReceiptJson).SingleAsync());
        var times = (await after.SourceVerificationItems.Where(x => x.RunId == run).Select(x => x.ReceiptJson!).ToArrayAsync())
            .Select(x => JsonSerializer.Deserialize<SourceVerificationReceipt>(x)!.RetainedHashedAtUtc).Distinct().ToArray();
        Assert.Single(times);
    }

    private sealed class OffsetClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }
}
