using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

[Collection("sql-hosted-recovery")]
public sealed class StagingSqlGpuRankBackendTests : IAsyncLifetime
{
    private readonly NativeSqlServerFixture _sql = new();
    public Task InitializeAsync() => _sql.InitializeAsync();
    public Task DisposeAsync() => _sql.DisposeAsync();

    [NativeSqlServerFact]
    public async Task Successful_cohort_joins_exact_delivery_and_native_disposal_before_reuse()
    {
        var factory = new Factory(_sql.ConnectionString);
        await using (var db = factory.CreateDbContext())
        {
            db.GpuCapacitySlots.Add(new() { SlotKey = "gpu-0", UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var models = new Models();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = new StagingSqlGpuRankBackend(factory, models, () => { }, TimeProvider.System, stop.Token);
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow, "first", "span", default);
        await using var second = coordinator.Reserve(DateTimeOffset.UtcNow, "second", "span", default);
        var one = first.SubmitAsync("one", Pairs()).AsTask();
        var two = second.SubmitAsync("two", Pairs()).AsTask();
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, models.Loads); Assert.Equal(1, models.Disposals);
        await using var verify = factory.CreateDbContext();
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
        Assert.Equal((int)GpuCapacitySlotState.Available, (await verify.GpuCapacitySlots.SingleAsync()).State);
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
        var receipt = Assert.Single(backend.Snapshot());
        Assert.True(receipt.CallbackSettled); Assert.True(receipt.NativeDisposed); Assert.True(receipt.TerminalVerified);
        Assert.Equal(first.CohortId, receipt.CohortId);
    }

    [NativeSqlServerFact]
    public async Task Busy_before_handoff_preserves_existing_capacity_without_loading_models()
    {
        var factory = new Factory(_sql.ConnectionString);
        await using (var db = factory.CreateDbContext())
        {
            db.GpuCapacitySlots.Add(new() { SlotKey = "gpu-0", State = (int)GpuCapacitySlotState.Reserved, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var models = new Models();
        var backend = new StagingSqlGpuRankBackend(factory, models, () => { }, TimeProvider.System, default);
        var outcome = await backend.RunAsync(Guid.NewGuid(), (_, _) => throw new InvalidOperationException("entered model"), default);
        Assert.Equal(ExpandedGpuRankSettlement.Settled, outcome.Settlement);
        Assert.Equal("busy", Assert.IsType<PassageRetrievalRefusalException>(outcome.Failure).Status);
        Assert.Equal(0, models.Loads);
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, (await verify.GpuCapacitySlots.SingleAsync()).State);
    }

    private static RerankPassage[] Pairs() => Enumerable.Range(1, 100).Select(i => new RerankPassage(i, "passage " + i)).ToArray();
    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
    private sealed class Models : IBgeGpuModelFactory
    {
        internal int Loads, Disposals;
        public BgeRerankerPrecision Precision => BgeRerankerPrecision.Fp16Internal;
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(FluxKnowledge.Application.Gpu.GpuOwnedWorkContext owner) => throw new NotSupportedException();
        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(FluxKnowledge.Application.Gpu.GpuOwnedWorkContext owner)
        {
            Interlocked.Increment(ref Loads);
            return ValueTask.FromResult(new BgeGpuModelLease<IPassageReranker>(new Ranker(), () => Interlocked.Increment(ref Disposals)));
        }
        private sealed class Ranker : IPassageReranker
        {
            public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> p, CancellationToken ct) =>
                ValueTask.FromResult(new RerankResult(p.Select(s => new RerankScore(s.PassageId, .5f)).ToArray(), BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal)));
        }
    }
}
