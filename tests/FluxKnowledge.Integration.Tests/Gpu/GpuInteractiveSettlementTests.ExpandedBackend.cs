using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed partial class GpuInteractiveSettlementTests
{
    [NativeSqlServerFact]
    public async Task Expanded_cohort_uses_the_normal_executor_and_unloads_after_four_bounded_calls()
    {
        var factory = SqlTestData.CreateFactory(_sql);
        var store = new SqlGpuSchedulerStore(factory, interactiveExecutionTimeout: TimeSpan.FromSeconds(20),interactiveQueueTimeout:TimeSpan.FromSeconds(4)); await AddSlot(factory);
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10, new[] { BgeExpandedGpuRankBackend.Profile },
            executionTimeout: TimeSpan.FromSeconds(20));
        var models = new ExpandedModels(); var backend = new BgeExpandedGpuRankBackend(executor, models);
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow, "first", "span", caller.Token);
        await using var second = coordinator.Reserve(DateTimeOffset.UtcNow, "second", "span", caller.Token);
        var one = first.SubmitAsync("first", Enumerable.Range(1, 100).Select(i => new RerankPassage(i, "passage")).ToArray()).AsTask();
        var two = second.SubmitAsync("second", Enumerable.Range(1, 100).Select(i => new RerankPassage(i, "passage")).ToArray()).AsTask();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            for (var i = 0; i < 100 && !await db.GpuMiniTasks.AnyAsync(); i++) await Task.Delay(10);
            Assert.True(await db.GpuMiniTasks.AnyAsync(), System.Text.Json.JsonSerializer.Serialize(backend.Snapshot()));
            var handle = await Admit(store, BgeExpandedGpuRankBackend.Profile.EstimatedBytes); Assert.Equal(executor.ExecutorKey, handle.ExecutorKey);
            await executor.DeliverAsync(handle, default);
            await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(new[] { 50, 50, 50, 50 }, models.InputCounts);
            Assert.Equal(1, models.Loads); Assert.Equal(1, models.Disposals);
            Assert.Equal(BgeExpandedGpuRankBackend.Profile.RuntimeKey, (await db.GpuMiniTasks.SingleAsync()).ModelRuntimeKey);
            Assert.Equal(1, await db.GpuBatches.CountAsync());
            Assert.All(new[] { await one, await two }, r => Assert.Equal(backend.RerankerFingerprint, r.ModelFingerprint));
        }
        finally { caller.Cancel(); await Task.WhenAll(one, two).ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(8)); }
    }

    private sealed class ExpandedModels : IBgeGpuModelFactory
    {
        public BgeRerankerPrecision Precision => BgeRerankerPrecision.Fp16Internal;
        internal int Loads, Disposals;
        internal List<int> InputCounts { get; } = [];
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext owner) => throw new InvalidOperationException("GPU-query-embedding-not-allowed");
        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext owner)
        {
            owner.RequireActive(BgeExpandedGpuRankBackend.Profile.RuntimeKey, BgeExpandedGpuRankBackend.Profile.SettingsFingerprint);
            var allocation = owner.BeginNativeAllocation(); Loads++;
            return ValueTask.FromResult(new BgeGpuModelLease<IPassageReranker>(new Ranker(this), () => { Disposals++; allocation.ConfirmReleased(); }));
        }
        private sealed class Ranker(ExpandedModels models) : IPassageReranker
        {
            public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); models.InputCounts.Add(passages.Count);
                return ValueTask.FromResult(new RerankResult(passages.Select(p => new RerankScore(p.PassageId, .5f)).ToArray(),
                    BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal)));
            }
        }
    }
}
