using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class CapturedGenerationIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Candidate_window_reports_correlated_selection_boundaries()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.", segmented: true);
        var target = await AddRoutingDocument(environment, "Current rooted evidence.", "CRM");
        var reader = RoutingReader(new NoVectorPayloads());
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        var trace = Guid.NewGuid().ToString("N"); using var phases = new WindowPhases(trace);
        var result = await HybridSearchDiagnostics.WithRequestCorrelationAsync(trace, "window", () =>
            reader.ReadDenseCandidateWindowAsync(ann, new("root", [target.RootId], null), vector.Values, default));
        Assert.Equal("ready", result.Status);
        Assert.Equal(new[] { "quality-live-bindings", "quality-ann-baseline", "quality-ann-pool" },
            phases.Rows.Where(p => p.Phase is "quality-live-bindings" or "quality-ann-baseline" or "quality-ann-pool").Select(p => p.Phase));
        Assert.Equal(4, phases.Rows.Count(p => p.Phase == "quality-readability"));
        Assert.All(phases.Rows.Where(p => p.Phase.StartsWith("quality-", StringComparison.Ordinal)), p =>
        { Assert.Equal("completed", p.Outcome); Assert.True(p.Milliseconds >= 0 && double.IsFinite(p.Milliseconds)); });
    }

    [NativeSqlServerFact]
    public async Task Candidate_window_hydrates_more_than_a_hundred_scoped_passages_without_sql_vectors()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.",
            segmented: true, segmentMaximumVectors: 512);
        var text = string.Join("\n\n", Enumerable.Range(1, 160).Select(i =>
            $"Candidate paragraph {i:D4}. " + string.Concat(Enumerable.Repeat("This paragraph records independent evidence and its applicable conditions. ", 12))));
        var target = await AddRoutingDocument(environment, text, "CRM");
        var reader = RoutingReader(new NoVectorPayloads());
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("candidate evidence", default);
        var window = await reader.ReadDenseCandidateWindowAsync(ann, new("root", [target.RootId], null), vector.Values, default);
        Assert.Equal(100, window.BaselineRawMatches);
        Assert.Equal(100, window.Baseline.Count);
        Assert.InRange(window.PoolRawMatches, 101, 400);
        Assert.InRange(window.Pool.Count, 101, 400);
        Assert.All(window.Pool, p => Assert.Equal(target.RecordId, p.PipelineRecordId));
        // This plain-text fixture does not have C# proof rows. Fresh canonical
        // binding/text validation, rather than a non-null code proof, is required.
        var reads = await reader.ReadForDisclosureAsync(window.Pool.Select(p => new CorpusEvidenceBinding(2,
            p.RootId, p.OwnerSourceRevisionId, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.SourceIdentity))),
            p.PipelineRecordId, p.PipelineRecordRevision, p.ArtifactId, p.ArtifactHash, p.ChunkId, p.ChunkHash,
            p.StartOffset, p.Length, p.CorpusEpoch)).ToArray(), default);
        Assert.Equal(window.Pool.Count, reads.Count);
        Assert.All(window.Pool, p =>
        {
            Assert.Equal(p.Content, reads[p.ChunkId].Text);
            Assert.Equal(p.StartOffset, reads[p.ChunkId].StartOffset);
            Assert.NotNull(reads[p.ChunkId].DisclosureText);
        });
    }

    [NativeSqlServerFact]
    public async Task Candidate_window_preserves_the_actual_baseline_with_one_captured_scope_for_both_calls()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.", segmented: true);
        var target = await AddRoutingDocument(environment, "Scoped candidate evidence.", "CRM");
        var other = await AddRoutingDocument(environment, "Other root evidence.", "CRM");
        var commands = new NoVectorPayloads(); var reader = RoutingReader(commands);
        using var services = environment.Services.CreateScope();
        await using var ann = new WindowAnn(await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default));
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        var scope = new ResolvedCorpusScope("workspace", [target.RootId, other.RootId], target.Path.ToLowerInvariant());
        var old = await reader.ReadDenseCandidatesAsync(ann, scope, vector.Values, default);
        ann.Calls.Clear(); var before = commands.LiveBindingReads;
        var window = await reader.ReadDenseCandidateWindowAsync(ann, scope, vector.Values, default);
        Assert.Equal(new[] { 100, 400 }, ann.Calls);
        Assert.Same(ann.BaselineSelection, ann.PoolSelection);
        Assert.Same(ann.BaselineQuery, ann.PoolQuery); Assert.Same(ann.BaselineScope, ann.PoolScope);
        Assert.Equal(before + 1, commands.LiveBindingReads);
        Assert.Equal("ready", window.Status);
        Assert.Equal(old.Candidates.Select(p => p.ChunkId), window.Baseline.Select(p => p.ChunkId));
        Assert.NotEmpty(window.Pool);
        Assert.All(window.Baseline.Concat(window.Pool), p => Assert.Equal(target.RecordId, p.PipelineRecordId));
        Assert.DoesNotContain(window.Pool, p => p.PipelineRecordId == other.RecordId);
        Assert.InRange(window.BaselineRawMatches, 0, 100); Assert.InRange(window.PoolRawMatches, 0, 400);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ann.SearchAsync(vector.Values, 101, scope, ann.BaselineSelection!, default).AsTask());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_window_checks_publication_between_baseline_and_expansion(bool changeEpoch)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.", segmented: true);
        var target = await AddRoutingDocument(environment, "Current rooted evidence.", "CRM");
        var reader = RoutingReader(new NoVectorPayloads());
        using var services = environment.Services.CreateScope();
        await using var ann = new WindowAnn(await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default));
        await ann.PrepareAsync(reader, default);
        ann.AfterBaseline = async () =>
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            if (changeEpoch) await db.IndexState.ExecuteUpdateAsync(set => set.SetProperty(p => p.CorpusEpoch, Guid.NewGuid()));
            else
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                await SqlPublishedPassageSelection.AcquireFenceAsync(db, default);
                await SqlPublishedPassageSelection.AdvanceVersionAsync(db, DateTimeOffset.UtcNow, default);
                await transaction.CommitAsync();
            }
        };
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        var work = reader.ReadDenseCandidateWindowAsync(ann, new("root", [target.RootId], null), vector.Values, default).AsTask();
        if (changeEpoch) await Assert.ThrowsAsync<PublicationSnapshotConflictException>(() => work);
        else
        {
            var result = await work;
            Assert.Equal("ready", result.Status); Assert.NotEmpty(result.Baseline); Assert.NotEmpty(result.Pool);
        }
    }

    [NativeSqlServerFact]
    public async Task Candidate_window_waits_for_actual_pool_cancellation_settlement()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.", segmented: true);
        var target = await AddRoutingDocument(environment, "Current rooted evidence.", "CRM");
        var reader = RoutingReader(new NoVectorPayloads());
        using var services = environment.Services.CreateScope();
        await using var ann = new WindowAnn(await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default)) { DelayPool = true };
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        using var caller = new CancellationTokenSource();
        var trace = Guid.NewGuid().ToString("N"); using var phases = new WindowPhases(trace);
        var work = HybridSearchDiagnostics.WithRequestCorrelationAsync(trace, "window", () =>
            reader.ReadDenseCandidateWindowAsync(ann, new("root", [target.RootId], null), vector.Values, caller.Token)).AsTask();
        await ann.PoolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        await ann.PoolCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(work.IsCompleted); Assert.False(ann.PoolSettled);
        ann.ReleasePool.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.True(ann.PoolSettled);
        Assert.Contains(phases.Rows, p => p.Phase == "quality-ann-pool" && p.Outcome == "cancelled");
    }

    private sealed class WindowPhases(string trace) : EventListener
    {
        internal ConcurrentQueue<(string Phase, string Outcome, double Milliseconds)> Rows { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "FluxKnowledge-HybridSearch") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventId == 6 && data.Payload is { Count: 6 } p && (string?)p[0] == trace && Rows.Count < 4096)
                Rows.Enqueue(((string)p[3]!, (string)p[4]!, (double)p[5]!));
        }
    }

    private sealed class WindowAnn(ICorpusAnnLease inner) : ICorpusAnnLease, IAnnCandidatePool
    {
        public Guid LeaseId => inner.LeaseId;
        public IndexGenerationDescriptor Generation => inner.Generation;
        public List<int> Calls { get; } = [];
        public ICorpusAnnScopeSelection? BaselineSelection, PoolSelection;
        public IReadOnlyList<float>? BaselineQuery, PoolQuery;
        public ResolvedCorpusScope? BaselineScope, PoolScope;
        public Func<Task>? AfterBaseline;
        public bool DelayPool, PoolSettled;
        public TaskCompletionSource PoolStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PoolCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePool { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPrepared(ICorpusAnnBindingPreparation metadata) => inner.IsPrepared(metadata);
        public ValueTask PrepareAsync(ICorpusAnnBindingPreparation metadata, CancellationToken ct) => inner.PrepareAsync(metadata, ct);
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => inner.IsCurrentAsync(ct);
        public ValueTask<bool> IsReadableAsync(CancellationToken ct) => inner.IsReadableAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> q, int limit, CancellationToken ct) => inner.SearchAsync(q, limit, ct);
        public async ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> q, int limit, ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken ct)
        {
            Calls.Add(limit); BaselineSelection = selection; BaselineQuery = q; BaselineScope = scope;
            var result = await inner.SearchAsync(q, limit, scope, selection, ct);
            if (AfterBaseline is not null) await AfterBaseline();
            return result;
        }
        public async ValueTask<IReadOnlyList<AnnMatch>> SearchCandidatePoolAsync(IReadOnlyList<float> q, int limit, ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken ct)
        {
            Calls.Add(limit); PoolSelection = selection; PoolQuery = q; PoolScope = scope; PoolStarted.TrySetResult();
            try
            {
                if (DelayPool)
                {
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { PoolCancelled.TrySetResult(); await ReleasePool.Task; throw; }
                }
                return await ((IAnnCandidatePool)inner).SearchCandidatePoolAsync(q, limit, scope, selection, ct);
            }
            finally { PoolSettled = true; }
        }
    }
}
