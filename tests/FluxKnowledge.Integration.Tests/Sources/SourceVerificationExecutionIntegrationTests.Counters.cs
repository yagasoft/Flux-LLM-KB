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
    [NativeSqlServerFact]
    public async Task Successful_receipt_persists_actual_counters_when_finalisation_is_lost()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "two.txt"), new string('a', 64));
        await scenario.BaselineAsync();
        var clock = new OffsetClock();
        var store = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental);
        var run = await store.StartAsync(scenario.RootId, true, default);
        var first = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("lost-finaliser", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        var interrupted = new CheckpointHook(store, (_, _) => Task.CompletedTask, afterCheckpoint: accepted =>
        { if (accepted) throw new InvalidOperationException("fixture-lost-finaliser"); }, suppressFinish: true);
        var firstWorker = new SourceVerificationWorker(interrupted, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstWorker.ScanAsync(first.SourceRoot, first.ScanRequest, default).AsTask());
        var before = (await store.ReadAsync(scenario.RootId, default))!.Run!;
        Assert.Equal(1, before.Verified);
        Assert.Equal(64, before.SourceBytesRead);
        Assert.Equal(64, before.RetainedBytesRead);
        Guid itemId; string receipt;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var item = await db.SourceVerificationItems.SingleAsync(x => x.RunId == run && x.State == "Verified");
            itemId = item.Id; receipt = item.ReceiptJson!;
        }
        // Let the owned ten-minute test lease expire; keep the real fifteen-minute namespace backstop current.
        clock.Offset = TimeSpan.FromMinutes(11);
        var second = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("recovered-finaliser", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        Assert.Equal(first.ScanRequest.Id, second.ScanRequest.Id);
        Assert.True(second.ScanRequest.Lease!.Generation > first.ScanRequest.Lease!.Generation);
        var recovered = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, clock);
        await scenario.Control.CompleteAsync(second, await recovered.ScanAsync(second.SourceRoot, second.ScanRequest, default), null, default);
        var status = (await store.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("Verified", status.Run!.State);
        Assert.Equal(128, status.Run.SourceBytesRead);
        Assert.Equal(64, status.Run.RetainedBytesRead); // Historical retained proof, not a second read.
        Assert.True(status.Run.ReadCountsIncomplete);
        Assert.NotNull(status.LastVerifiedAtUtc);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(receipt, await after.SourceVerificationItems.Where(x => x.Id == itemId).Select(x => x.ReceiptJson).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Completed_counter_attempt_only_accepts_identical_finalisation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("counter-replay", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var batch = Assert.IsType<SourceVerificationBatch>(await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        // Controlled progress tests the persistence contract, not native byte measurement.
        await store.FinishBatchAsync(batch, 7, 11, "fixture", default);
        await store.FinishBatchAsync(batch, 7, 11, "fixture", default);
        var status = (await store.ReadAsync(scenario.RootId, default))!.Run!;
        Assert.Equal(7, status.SourceBytesRead);
        Assert.Equal(11, status.RetainedBytesRead);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.FinishBatchAsync(batch, 8, 11, "fixture", default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.FinishBatchAsync(batch, 7, 10, "fixture", default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.FinishBatchAsync(batch with { AttemptId = Guid.NewGuid() }, 7, 11, "fixture", default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.FinishBatchAsync(batch, -1, 11, "fixture", default).AsTask());
    }

    [NativeSqlServerTheory]
    [InlineData("start")]
    [InlineData("pump")]
    [InlineData("prepare")]
    public async Task Abandoned_counter_attempt_remains_incomplete_when_authority_invalidates_the_run(string path)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        var runId = await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("abandoned-counter", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        Assert.NotNull(await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        await using (var db = scenario.Factory.CreateDbContext())
            await db.SourceRootScanStates.ExecuteUpdateAsync(set => set.SetProperty(x => x.RecoveryGeneration, x => x.RecoveryGeneration + 1));
        if (path == "start") Assert.NotEqual(runId, await store.StartAsync(scenario.RootId, true, default));
        else if (path == "pump") Assert.Equal(0, await store.PumpAsync(default));
        else Assert.Null(await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        await using var after = scenario.Factory.CreateDbContext();
        var old = await after.SourceVerificationRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal("Invalidated", old.State);
        Assert.False(old.Active);
        Assert.True(old.ReadCountsIncomplete);
    }

    [NativeSqlServerFact]
    public async Task Receipt_checkpoint_fences_attempt_and_monotonic_counters_even_for_a_replayed_item()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("checkpoint-counter-replay", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var batch = Assert.IsType<SourceVerificationBatch>(await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        var item = batch.Items[0];
        using var budget = new SourceVerificationReadBudget(2 * 1024 * 1024);
        var digest = await new WindowsSourceVerificationContentReader(TimeProvider.System).ReadAsync(batch.Root, item.Binding.RelativePath, budget, default);
        await using var blob = await scenario.Artifacts.OpenVerificationObservationAsync(item.Binding.StoreRelativePath!, item.Binding.Sha256!, item.Binding.ByteLength, budget, default);
        var hashedAt = blob.ChecksumVerifiedAtUtc!.Value;
        var sourceBytes = budget.SourceBytesRead; var retainedBytes = budget.RetainedBytesRead;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CheckpointAsync(batch with { AttemptId = Guid.NewGuid() }, item, digest,
            blob, hashedAt, sourceBytes, retainedBytes, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CheckpointAsync(batch, item, digest, blob, hashedAt, -1, retainedBytes, default).AsTask());
        Assert.True(await store.CheckpointAsync(batch, item, digest, blob, hashedAt, sourceBytes, retainedBytes, default));
        await using var before = scenario.Factory.CreateDbContext();
        var receipt = await before.SourceVerificationItems.Where(x => x.Id == item.Id).Select(x => x.ReceiptJson).SingleAsync();
        Assert.True(await store.CheckpointAsync(batch, item, digest, blob, hashedAt, sourceBytes, retainedBytes, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CheckpointAsync(batch, item, digest, blob, hashedAt, sourceBytes - 1, retainedBytes, default).AsTask());
        await store.FinishBatchAsync(batch, sourceBytes, retainedBytes, "fixture", default);
        Assert.True(await store.CheckpointAsync(batch, item, digest, blob, hashedAt, sourceBytes, retainedBytes, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CheckpointAsync(batch, item, digest, blob, hashedAt, sourceBytes + 1, retainedBytes, default).AsTask());
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(receipt, await after.SourceVerificationItems.Where(x => x.Id == item.Id).Select(x => x.ReceiptJson).SingleAsync());
        var status = (await store.ReadAsync(scenario.RootId, default))!.Run!;
        Assert.Equal(sourceBytes, status.SourceBytesRead);
        Assert.Equal(retainedBytes, status.RetainedBytesRead);
        Assert.False(status.ReadCountsIncomplete);
    }
}
