using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Resolution_rotates_past_a_full_page_of_unavailable_items_to_a_ready_successor()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        for (var index = 0; index < 101; index++)
            await File.WriteAllTextAsync(Path.Combine(scenario.Sources, $"extra-{index:D3}.txt"), new string('a', 64));
        await scenario.BaselineAsync();
        var clock = new OffsetClock();
        var store = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental);
        var run = await store.StartAsync(scenario.RootId, true, default);
        Guid lastId; string lastPath;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var last = await db.SourceVerificationItems.Where(x => x.RunId == run).OrderByDescending(x => x.Id).FirstAsync();
            lastId = last.Id; lastPath = JsonSerializer.Deserialize<SourceVerificationBinding>(last.BindingJson)!.RelativePath;
        }
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, lastPath), new string('c', 64));
        await RunOrdinaryChangesAsync(scenario);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var rows = await db.SourceVerificationItems.Where(x => x.RunId == run).OrderBy(x => x.Id).ToArrayAsync();
            var state = await db.SourceRootScanStates.SingleAsync();
            foreach (var row in rows) row.State = "AwaitingCapture";
            foreach (var row in rows.Take(100))
            {
                var binding = JsonSerializer.Deserialize<SourceVerificationBinding>(row.BindingJson)!;
                var generation = ++state.Generation;
                db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.RootId.Value,
                    RelativePath = binding.RelativePath, PathFingerprint = SqlSourceChangeJournalStore.PathKey(binding.RelativePath),
                    ReasonFlags = 1, Generation = generation, RetryGeneration = generation, LastContentUsn = 0, LastUnjournaledGeneration = generation,
                    FirstObservedAtUtc = clock.GetUtcNow(), NotBeforeUtc = clock.GetUtcNow().AddHours(1) });
            }
            await db.SaveChangesAsync();
        }
        var first = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("blocked-page", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        var batch = Assert.IsType<SourceVerificationBatch>(await store.PrepareAsync(first.SourceRoot, first.ScanRequest, default));
        Assert.Empty(batch.Items);
        await store.FinishBatchAsync(batch, 0, 0, "awaiting-capture", default);
        await scenario.Control.CompleteAsync(first, new(first.SourceRoot.Id, first.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, null, default);
        clock.Offset = TimeSpan.FromSeconds(6);
        Assert.Equal(1, await store.PumpAsync(default));
        var second = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("ready-page", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, clock);
        await scenario.Control.CompleteAsync(second, await worker.ScanAsync(second.SourceRoot, second.ScanRequest, default), null, default);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal("CapturedSuccessor", await after.SourceVerificationItems.Where(x => x.Id == lastId).Select(x => x.State).SingleAsync());
        Assert.Equal(100, await after.SourceVerificationItems.CountAsync(x => x.RunId == run && x.State == "AwaitingCapture"));
    }

    [NativeSqlServerFact]
    public async Task Slow_credit_wait_observes_pause_without_waiting_for_the_next_full_chunk()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "one.txt"), new string('a', 256 * 1024));
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "two.txt"), new string('b', 256 * 1024));
        await scenario.BaselineAsync();
        await using (var db = scenario.Factory.CreateDbContext())
            await db.SourceRootScanStates.ExecuteUpdateAsync(set => set.SetProperty(x => x.VerificationPolicyJson,
                JsonSerializer.Serialize(SourceVerificationPolicy.Default("UTC") with { MaximumBytesPerSecond = 1 })));
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("slow-budget", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new WindowsSourceVerificationContentReader(TimeProvider.System) { SourceBytesRead = _ => read.TrySetResult() };
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime), content,
            scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var work = worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, cancellation.Token).AsTask();
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var db = scenario.Factory.CreateDbContext())
                await db.SourceRootScanStates.ExecuteUpdateAsync(set => set.SetProperty(x => x.VerificationPaused, true));
            await work.WaitAsync(TimeSpan.FromSeconds(3));
            var status = (await store.ReadAsync(scenario.RootId, default))!;
            Assert.Equal("foreground-or-policy", status.Run!.Reason);
            Assert.Equal(128 * 1024, status.Run.SourceBytesRead);
            Assert.Equal(0, status.Run.RetainedBytesRead);
            using var free = new WindowsSourceVerificationReaderSlot(scenario.Runtime).TryAcquire();
            Assert.NotNull(free);
        }
        finally
        {
            cancellation.Cancel();
            try { await work; } catch (OperationCanceledException) { }
        }
    }
}
