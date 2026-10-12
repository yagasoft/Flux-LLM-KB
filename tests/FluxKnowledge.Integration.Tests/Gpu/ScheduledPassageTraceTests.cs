using System.Diagnostics;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Models;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

[Collection("sql-full-text")]
public sealed class ScheduledPassageTraceTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Owned_callbacks_attribute_real_sql_events_to_distinct_request_spans()
    {
        using var trace = new HybridSearchTraceListener();
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.");
        const string body = "The scoped annual allowance is twenty days.";
        var recordId = await environment.AddRetainedAndPumpAsync(body);
        await using var setup = await environment.Factory.CreateDbContextAsync();
        await setup.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var rootId = await (from record in setup.PipelineRecords
            join revision in setup.SourceRevisions on record.SourceRevisionId equals revision.Id
            where record.Id == recordId select revision.SourceRootId).SingleAsync();
        var (store, executor, lifecycle) = await CreateExecutorAsync(environment.Factory);
        var inference = new BgeScheduledPassageInference(executor, new BgeGpuInferenceSession(new BgeGpuInferenceTests.RecordingModels()));
        var query = await environment.Embeddings.CreateEmbeddingAsync("annual allowance", CancellationToken.None);
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var calls = 0;
        async Task<string> RequestAsync()
        {
            using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId($"00-{traceId}-{ActivitySpanId.CreateRandom()}-01").Start();
            var spanId = request.SpanId.ToString();
            var result = await inference.ExecuteAsync(async (embedding, reranker, ct) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                await embedding.CreateEmbeddingAsync("annual allowance", ct);
                await using var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)
                    .TryAcquireAsync(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current,
                        DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, ct);
                Assert.NotNull(lease);
                var selected = await new SqlCorpusRetrievalReader(environment.Factory)
                    .ReadDenseCandidatesAsync(lease, new("root", [rootId], null), query.Values, ct);
                Assert.Equal("ready", selected.Status);
                var passage = Assert.Single(selected.Candidates);
                await reranker.RerankAsync("annual allowance", [new(passage.ChunkId, passage.Content)], ct);
                return passage.Content;
            }, CancellationToken.None);
            Assert.Equal(body, result);
            return spanId;
        }
        var first = RequestAsync();
        var second = RequestAsync();
        await WaitForRequestsAsync(environment.Factory, 2);
        using var scheduler = new Activity("unrelated scheduler").Start();
        await DeliverNextAsync(store, executor);
        await DeliverNextAsync(store, executor);
        var spans = await Task.WhenAll(first, second);
        Assert.Equal(2, calls);
        Assert.Same(scheduler, Activity.Current);
        Assert.Equal(2, lifecycle.ReceiptActivities.Count);
        Assert.All(lifecycle.ReceiptActivities, value => Assert.Same(scheduler, value));
        Assert.All(lifecycle.ReceiptCorrelations, value => Assert.Equal((scheduler.TraceId.ToString(), scheduler.SpanId.ToString()), value));
        var mappings = trace.Events.Where(entry => entry.Id == 4 && Equals(entry["traceId"], traceId)).ToArray();
        Assert.Equal(spans.Order(), mappings.Select(entry => (string)entry["spanId"]!).Order());
        foreach (var span in spans)
        {
            var leaseEvents = trace.Events.Where(entry => entry.Id == 7 && Equals(entry["traceId"], traceId) && Equals(entry["spanId"], span)).ToArray();
            Assert.Contains(leaseEvents, entry => Equals(entry["phase"], "lease-persisted"));
            var page = Assert.Single(trace.Events, entry => entry.Id == 5 && Equals(entry["traceId"], traceId) && Equals(entry["spanId"], span));
            Assert.Equal(1, page["rows"]);
        }
        Assert.DoesNotContain(trace.Events, entry => entry.Id is 5 or 7 && Equals(entry["traceId"], scheduler.TraceId.ToString()));
        await AssertReleasedAsync(environment.Factory, 2, GpuMiniTaskExecutionState.Completed);
    }

    [NativeSqlServerTheory]
    [InlineData("success")]
    [InlineData("refusal")]
    [InlineData("cancelled")]
    [InlineData("absent")]
    public async Task Ended_or_absent_request_context_is_scoped_through_cleanup(string outcome)
    {
        using var trace = new HybridSearchTraceListener();
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var (store, executor, lifecycle) = await CreateExecutorAsync(factory);
        var inference = new BgeScheduledPassageInference(executor, new BgeGpuInferenceSession(new BgeGpuInferenceTests.RecordingModels()));
        using var caller = new CancellationTokenSource();
        var previous = Activity.Current;
        using var request = outcome == "absent" ? null : new Activity("ended request").Start();
        if (request is null) Activity.Current = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<(string TraceId, string SpanId)>();
        var calls = 0;
        Task<int> result;
        try
        {
            result = inference.ExecuteAsync(async (embedding, reranker, ct) =>
            {
                Interlocked.Increment(ref calls);
                observed.Add(HybridSearchDiagnostics.CurrentCorrelation);
                await embedding.CreateEmbeddingAsync("query", ct);
                entered.TrySetResult();
                try
                {
                    await release.Task;
                    observed.Add(HybridSearchDiagnostics.CurrentCorrelation);
                    ct.ThrowIfCancellationRequested();
                    if (outcome == "refusal") throw new PassageRetrievalRefusalException("index-updating");
                    await reranker.RerankAsync("query", [new(8, "passage")], ct);
                    return 42;
                }
                finally
                {
                    // Exercise the real diagnostic lookup after the caller and its Activity end.
                    await using var lease = await new SqlCorpusGenerationLeaseStore(factory, TimeProvider.System)
                        .TryAcquireAsync(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current,
                            DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, CancellationToken.None);
                    Assert.Null(lease);
                    observed.Add(HybridSearchDiagnostics.CurrentCorrelation);
                }
            }, caller.Token).AsTask();
        }
        finally
        {
            request?.Stop();
            Activity.Current = previous;
        }
        await WaitForRequestsAsync(factory, 1);
        using var scheduler = new Activity("unrelated scheduler").Start();
        var delivery = DeliverNextAsync(store, executor);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (outcome == "cancelled")
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
                await using var pending = await factory.CreateDbContextAsync();
                Assert.Equal((int)GpuCapacitySlotState.Reserved, await pending.GpuCapacitySlots.Select(slot => slot.State).SingleAsync());
            }
        }
        finally { release.TrySetResult(); }
        await delivery.WaitAsync(TimeSpan.FromSeconds(8));
        if (outcome == "refusal") await Assert.ThrowsAsync<InvalidOperationException>(() => result);
        else if (outcome != "cancelled") Assert.Equal(42, await result);
        Assert.Equal(1, calls);
        Assert.Equal(3, observed.Count);
        var expected = (request?.TraceId.ToString() ?? string.Empty, request?.SpanId.ToString() ?? string.Empty);
        Assert.All(observed, value => Assert.Equal(expected, value));
        var lateLeaseEvents = trace.Events.Where(entry => entry.Id == 7 && Equals(entry["traceId"], expected.Item1) && Equals(entry["spanId"], expected.Item2)).ToArray();
        Assert.Contains(lateLeaseEvents, entry => Equals(entry["outcome"], "generation-unavailable"));
        Assert.DoesNotContain(trace.Events, entry => entry.Id == 7 && Equals(entry["traceId"], scheduler.TraceId.ToString()));
        Assert.Same(scheduler, Activity.Current);
        Assert.Same(scheduler, Assert.Single(lifecycle.ReceiptActivities));
        Assert.Equal((scheduler.TraceId.ToString(), scheduler.SpanId.ToString()), Assert.Single(lifecycle.ReceiptCorrelations));
        await AssertReleasedAsync(factory, 1, outcome is "refusal" or "cancelled"
            ? GpuMiniTaskExecutionState.OutcomeUncertain : GpuMiniTaskExecutionState.Completed);
    }

    private static async Task<(SqlGpuSchedulerStore, GpuInteractiveExecutor, Lifecycle)> CreateExecutorAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var store = new SqlGpuSchedulerStore(factory);
        await using var setup = await factory.CreateDbContextAsync();
        setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await setup.SaveChangesAsync();
        var lifecycle = new Lifecycle(store);
        return (store, new GpuInteractiveExecutor(store, lifecycle, store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, 10), lifecycle);
    }

    private static async Task WaitForRequestsAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, int count)
    {
        await using var context = await factory.CreateDbContextAsync();
        for (var attempt = 0; await context.GpuMiniTasks.CountAsync() < count && attempt < 100; attempt++) await Task.Delay(10);
        Assert.Equal(count, await context.GpuMiniTasks.CountAsync());
    }

    private static async Task DeliverNextAsync(SqlGpuSchedulerStore store, GpuInteractiveExecutor executor)
    {
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        await executor.DeliverAsync(Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None);
    }

    private static async Task AssertReleasedAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, int count, GpuMiniTaskExecutionState expected)
    {
        await using var context = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.Select(slot => slot.State).SingleAsync());
        var tasks = await context.GpuMiniTasks.ToArrayAsync();
        Assert.Equal(count, tasks.Length);
        Assert.All(tasks, task => Assert.Equal((int)expected, task.ExecutionState));
        Assert.Equal(count, await context.GpuExecutorResultReceipts.CountAsync());
        Assert.Empty(await context.CorpusQueryLeases.ToArrayAsync());
    }

    private sealed class Lifecycle(SqlGpuSchedulerStore store) : IGpuExecutorLifecycleSink
    {
        public List<Activity?> ReceiptActivities { get; } = [];
        public List<(string TraceId, string SpanId)> ReceiptCorrelations { get; } = [];
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement request, CancellationToken ct) => store.AcknowledgeAsync(request, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty request, CancellationToken ct) => store.MarkDeliveryUncertainAsync(request, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt request, CancellationToken ct)
        {
            ReceiptActivities.Add(Activity.Current);
            ReceiptCorrelations.Add(HybridSearchDiagnostics.CurrentCorrelation);
            return store.RecordReceiptAsync(request, ct);
        }
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence request, CancellationToken ct) => store.RecordTrustedEvidenceAsync(request, ct);
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operation, GpuBatchCallback request, CancellationToken ct) => store.ApplyBatchCallbackAsync(operation, request, ct);
    }
}
