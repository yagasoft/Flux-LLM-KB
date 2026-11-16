using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed partial class GpuInteractiveSettlementTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expanded_foreground_admission_preserves_the_busy_owner_and_original_execution_deadline(bool expire)
    {
        var factory = SqlTestData.CreateFactory(_sql);
        var store = new SqlGpuSchedulerStore(factory, interactiveExecutionTimeout: TimeSpan.FromSeconds(20),
            interactiveQueueTimeout: TimeSpan.FromSeconds(6));
        await AddSlot(factory);
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10, [BgeExpandedGpuRankBackend.Profile],
            executionTimeout: TimeSpan.FromSeconds(20));
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowModelLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = executor.StartOwned<int>(async owner =>
        {
            var allocation = owner.BeginNativeAllocation();
            occupied.TrySetResult();
            await releaseOwner.Task.ConfigureAwait(false);
            allocation.ConfirmReleased();
            return new(42, true);
        }, CancellationToken.None);
        Task? backgroundDelivery = null;
        Task<RerankResult>? ranking = null;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var models = new ExpandedModels();
        var backend = new BgeExpandedGpuRankBackend(executor, new DelayedExpandedModels(models, allowModelLoad.Task));
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        try
        {
            await WaitForRequest(factory, background.RequestId);
            var occupiedHandle = await Admit(store, BgeExpandedGpuRankBackend.Profile.EstimatedBytes);
            backgroundDelivery = executor.DeliverAsync(occupiedHandle, default).AsTask();
            await occupied.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await using var ticket = coordinator.Reserve(DateTimeOffset.UtcNow, "indexing-admission", "span", caller.Token);
            // Submission is on time even when the native model loads after the six-second payload cutoff.
            ranking = ticket.SubmitAsync("question", [new RerankPassage(1, "answer")]).AsTask();
            await using var db = await factory.CreateDbContextAsync();
            for (var attempt = 0; attempt < 100 && !await db.GpuMiniTasks.AnyAsync(t => t.ModelRuntimeKey == BgeExpandedGpuRankBackend.Profile.RuntimeKey); attempt++)
                await Task.Delay(10);
            var foreground = await db.GpuMiniTasks.AsNoTracking().SingleAsync(t => t.ModelRuntimeKey == BgeExpandedGpuRankBackend.Profile.RuntimeKey);
            Assert.Equal(TimeSpan.FromSeconds(14), foreground.ExecutionDeadlineUtc - foreground.QueueDeadlineUtc);
            var created = foreground.QueueDeadlineUtc!.Value.AddSeconds(-6);
            await DelayUntil(created.AddMilliseconds(expire ? 6300 : 4400));
            if (expire)
            {
                // The rank ticket preserves queue expiry; the retrieval layer maps it to lexical fallback.
                await Assert.ThrowsAsync<TimeoutException>(() => ranking.WaitAsync(TimeSpan.FromSeconds(3)));
                var receipt = Assert.Single(backend.Snapshot());
                Assert.Equal(nameof(GpuInteractiveSettlementKind.CancelledBeforeExecution), receipt.OwnershipKind);
                Assert.False(receipt.CallbackSettled);
                Assert.Equal(0, models.Loads);
                var current = await db.GpuMiniTasks.AsNoTracking().SingleAsync(t => t.Id == foreground.Id);
                Assert.Equal((int)GpuMiniTaskExecutionState.Cancelled, current.ExecutionState);
                Assert.Null(current.BatchId);
                var slot = await db.GpuCapacitySlots.AsNoTracking().SingleAsync();
                Assert.Equal((int)GpuCapacitySlotState.Reserved, slot.State);
                Assert.Equal(occupiedHandle.BatchId, slot.ActiveBatchId);
                Assert.Single(await db.GpuBatches.AsNoTracking().ToArrayAsync());
                Assert.False(background.Settlement.IsCompleted);
            }
            else
            {
                Assert.False(ranking.IsCompleted);
                Assert.Equal((int)GpuMiniTaskExecutionState.Ready,
                    (await db.GpuMiniTasks.AsNoTracking().SingleAsync(t => t.Id == foreground.Id)).ExecutionState);
                Assert.Equal(occupiedHandle.BatchId, (await db.GpuCapacitySlots.AsNoTracking().SingleAsync()).ActiveBatchId);
                releaseOwner.TrySetResult();
                await backgroundDelivery.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(42, await background.Result);
                Assert.Equal(GpuInteractiveSettlementKind.Released, (await background.Settlement).Kind);
                var handle = await Admit(store, BgeExpandedGpuRankBackend.Profile.EstimatedBytes);
                var delivery = executor.DeliverAsync(handle, default).AsTask();
                await DelayUntil(created.AddMilliseconds(6500));
                allowModelLoad.TrySetResult();
                Assert.Equal(backend.RerankerFingerprint, (await ranking.WaitAsync(TimeSpan.FromSeconds(3))).ModelFingerprint);
                await delivery.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(1, models.Loads);
                Assert.Equal(1, models.Disposals);
                var receipt = Assert.Single(backend.Snapshot());
                Assert.True(receipt.NativeDisposed);
                Assert.True(receipt.TerminalVerified);
                Assert.Equal(nameof(GpuInteractiveSettlementKind.Released), receipt.OwnershipKind);
                Assert.Equal((int)GpuCapacitySlotState.Available, (await db.GpuCapacitySlots.AsNoTracking().SingleAsync()).State);
                Assert.Equal(2, await db.GpuBatches.CountAsync());
            }
        }
        finally
        {
            releaseOwner.TrySetResult();
            allowModelLoad.TrySetResult();
            caller.Cancel();
            if (ranking is not null) await ranking.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(8));
            if (backgroundDelivery is not null) await backgroundDelivery.WaitAsync(TimeSpan.FromSeconds(8));
            await background.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task DelayUntil(DateTimeOffset target)
    {
        var remaining = target - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    private sealed class DelayedExpandedModels(ExpandedModels inner, Task ready) : IBgeGpuModelFactory
    {
        public BgeRerankerPrecision Precision => inner.Precision;
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext owner) =>
            throw new InvalidOperationException("GPU-query-embedding-not-allowed");
        public async ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext owner)
        {
            await ready.WaitAsync(owner.CancellationToken).ConfigureAwait(false);
            return await inner.OpenRerankerAsync(owner).ConfigureAwait(false);
        }
    }
}
