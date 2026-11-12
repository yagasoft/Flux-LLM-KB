using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

/// <summary>Run in a disposable testhost; its parent must join process exit before guarded catalogue cleanup.</summary>
[Collection("sql-hosted-recovery")]
public sealed class StagingGpuRankFaultControls : IAsyncLifetime
{
    internal const string ReceiptEnvironment = "FLUXKNOWLEDGE_STAGING_GPU_FAULT_RECEIPTS";
    private readonly NativeSqlServerFixture _sql = new();
    private string _receipt = "";
    private bool _created;
    public async Task InitializeAsync()
    {
        var root = Environment.GetEnvironmentVariable(ReceiptEnvironment);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root) ||
            !Path.GetFullPath(root).StartsWith(@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Explicit private parent-cleanup receipt directory required.");
        await _sql.InitializeAsync(); _created=true;
        _receipt = Path.Combine(root,_sql.DatabaseName+".json");
        using var process = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(_receipt,JsonSerializer.Serialize(new
        {
            schemaVersion=1, catalogue=_sql.DatabaseName, fileRoot=Environment.GetEnvironmentVariable(NativeSqlServerFixture.FileRootEnvironmentVariable),
            processId=process.Id,startedUtc=process.StartTime.ToUniversalTime(),syntheticModels=true,actualGpuUsed=false,
            cleanup="parent must observe this exact process incarnation exit, then use NativeSqlServerFixture ownership/file guards"
        }));
    }
    public Task DisposeAsync() => _created ? File.WriteAllTextAsync(_receipt+".test-ended","database intentionally retained until exact testhost exit") : Task.CompletedTask;

    [StagingGpuFaultControlFact]
    public async Task Cancellation_joins_entered_native_work_but_remains_closed_after_generic_executor_failure()
    {
        var factory = await SetupAsync(); var models = new Models(); using var owner = new CancellationTokenSource();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = new StagingSqlGpuRankBackend(factory,models,() => { },TimeProvider.System,stop.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = backend.RunAsync(Guid.NewGuid(),async (_,_) => { entered.TrySetResult(); await release.Task; return Scores(); },owner.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); owner.Cancel();
            Assert.False(pending.IsCompleted); Assert.Equal(0,models.Disposals);
            await using var db = factory.CreateDbContext();
            Assert.Equal((int)GpuCapacitySlotState.Reserved,(await db.GpuCapacitySlots.SingleAsync()).State);
        }
        finally { release.TrySetResult(); }
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1,models.Disposals); Assert.Empty(outcome.Results);
        await AssertClosed(backend,outcome,models);
    }

    [StagingGpuFaultControlFact]
    public async Task Failed_native_disposal_keeps_the_allocation_live_and_never_publishes_scores()
    {
        var factory = await SetupAsync(); var models = new Models { FailDispose=true };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = new StagingSqlGpuRankBackend(factory,models,() => { },TimeProvider.System,stop.Token);
        var outcome = await backend.RunAsync(Guid.NewGuid(),(_,_) => ValueTask.FromResult(Scores()),default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Assert.Single(backend.Snapshot()).NativeDisposed); Assert.Empty(outcome.Results);
        await AssertClosed(backend,outcome,models);
        await using var db = factory.CreateDbContext();
        Assert.NotEqual((int)GpuCapacitySlotState.Available,(await db.GpuCapacitySlots.SingleAsync()).State);
    }

    [StagingGpuFaultControlFact]
    public async Task Missing_positive_terminal_observation_refuses_scores_and_reuse_even_when_capacity_is_available()
    {
        var factory = await SetupAsync(); var models = new Models();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = new StagingSqlGpuRankBackend(factory,models,() => { },TimeProvider.System,stop.Token,
            _ => ValueTask.FromException(new IOException("synthetic-terminal-observer-failure")));
        var outcome = await backend.RunAsync(Guid.NewGuid(),(_,_) => ValueTask.FromResult(Scores()),default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(backend.Snapshot()).NativeDisposed); Assert.False(Assert.Single(backend.Snapshot()).TerminalVerified);
        Assert.Empty(outcome.Results);
        await AssertClosed(backend,outcome,models);
        await using var db = factory.CreateDbContext();
        Assert.Equal((int)GpuCapacitySlotState.Available,(await db.GpuCapacitySlots.SingleAsync()).State);
    }

    private static async Task AssertClosed(StagingSqlGpuRankBackend backend,ExpandedGpuRankOutcome outcome,Models models)
    {
        Assert.Equal(ExpandedGpuRankSettlement.RetainedUncertain,outcome.Settlement);
        var again = await backend.RunAsync(Guid.NewGuid(),(_,_) => throw new InvalidOperationException("reopened"),default);
        Assert.Equal(ExpandedGpuRankSettlement.RetainedUncertain,again.Settlement); Assert.Equal(1,models.Loads);
    }
    private static IReadOnlyDictionary<Guid,RerankResult> Scores() => new Dictionary<Guid,RerankResult>
        { [Guid.NewGuid()] = new([new RerankScore(1,.5f)],BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal)) };
    private async Task<Factory> SetupAsync()
    {
        var factory = new Factory(_sql.ConnectionString);
        await using var db = factory.CreateDbContext();
        db.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey="gpu-0",UpdatedAtUtc=DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        return factory;
    }
    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);
    }
    private sealed class Models : IBgeGpuModelFactory
    {
        internal bool FailDispose;
        internal int Loads,Disposals;
        public BgeRerankerPrecision Precision => BgeRerankerPrecision.Fp16Internal;
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(FluxKnowledge.Application.Gpu.GpuOwnedWorkContext owner) => throw new NotSupportedException();
        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(FluxKnowledge.Application.Gpu.GpuOwnedWorkContext owner)
        {
            Loads++; var allocation = owner.BeginNativeAllocation();
            return ValueTask.FromResult(new BgeGpuModelLease<IPassageReranker>(new Ranker(),() =>
            { Disposals++; if(FailDispose) throw new IOException("synthetic-native-dispose-failed"); allocation.ConfirmReleased(); }));
        }
        private sealed class Ranker : IPassageReranker
        {
            public ValueTask<RerankResult> RerankAsync(string query,IReadOnlyList<RerankPassage> p,CancellationToken ct) => throw new NotSupportedException();
        }
    }
}

internal sealed class StagingGpuFaultControlFactAttribute : FactAttribute
{
    public StagingGpuFaultControlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StagingGpuRankFaultControls.ReceiptEnvironment)))
            Skip="Explicit disposable testhost and post-exit cleanup receipts required.";
    }
}
