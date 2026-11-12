using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class CapturedGenerationIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Query_aware_capture_keeps_the_global_population_and_scope_with_one_live_statement()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.", segmented: true);
        var target = await AddRoutingDocument(environment, "Directed target evidence.", "CRM");
        var other = await AddRoutingDocument(environment, "Different root evidence.", "CRM");
        var commands = new NoVectorPayloads();
        var reader = RoutingReader(commands);
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        var scope = new ResolvedCorpusScope("workspace", [target.RootId, other.RootId], target.Path.ToLowerInvariant());
        var baseline = await reader.ReadDenseCandidatesAsync(ann, scope, vector.Values, default);
        var before = commands.LiveBindingReads;
        var aware = await ((IHybridPassageCandidateReader)reader).ReadDenseCandidatesAsync(ann, scope, "CRM", vector.Values, default);
        Assert.Equal(before + 1, commands.LiveBindingReads);
        Assert.Equal(baseline.Candidates.Select(p => p.ChunkId), aware.Candidates.Select(p => p.ChunkId));
        Assert.Equal(SourceDirectedRetrievalStatus.Ready, aware.SourceDirectedStatus);
        Assert.NotEmpty(aware.SourceDirectedCandidates);
        Assert.All(aware.SourceDirectedCandidates, p => Assert.Equal(target.RecordId, p.PipelineRecordId));
        Assert.DoesNotContain(aware.SourceDirectedCandidates, p => p.PipelineRecordId == other.RecordId);
        var global = await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), vector.Values, default);
        var globalAware = await ((IHybridPassageCandidateReader)reader).ReadDenseCandidatesAsync(ann,
            new("all", [], null), "CRM", vector.Values, default);
        Assert.Equal(global.Candidates.Select(p => p.ChunkId), globalAware.Candidates.Select(p => p.ChunkId));
        Assert.Equal(SourceDirectedRetrievalStatus.NotRequested, globalAware.SourceDirectedStatus);
        Assert.Empty(globalAware.SourceDirectedCandidates);
    }

    [NativeSqlServerFact]
    public async Task Query_aware_capture_remains_readable_after_unrelated_publication_advances()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline generation.", segmented: true);
        var target = await AddRoutingDocument(environment, "A current retained document.", "CRM");
        var reader = RoutingReader(new NoVectorPayloads());
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        await ann.PrepareAsync(reader, default);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, default);
            await SqlPublishedPassageSelection.AdvanceVersionAsync(db, DateTimeOffset.UtcNow, default);
            await transaction.CommitAsync();
        }
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        var result = await ((IHybridPassageCandidateReader)reader).ReadDenseCandidatesAsync(ann,
            new("root", [target.RootId], null), "CRM", vector.Values, default);
        Assert.True(await ann.IsReadableAsync(default));
        Assert.Equal("ready", result.Status);
        Assert.Equal(SourceDirectedRetrievalStatus.Ready, result.SourceDirectedStatus);
        Assert.Contains(result.SourceDirectedCandidates, p => p.PipelineRecordId == target.RecordId);
    }

    [NativeSqlServerTheory]
    [InlineData("timeout")]
    [InlineData("publication")]
    [InlineData("identity")]
    [InlineData("caller")]
    [InlineData("late")]
    [InlineData("epoch-timeout")]
    public async Task Only_settled_source_subdeadline_cancellation_can_fall_back_to_the_valid_global_population(string fault)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline generation.", segmented: true);
        var target = await AddRoutingDocument(environment, "A current retained document.", "CRM");
        var reader = RoutingReader(new NoVectorPayloads());
        using var services = environment.Services.CreateScope();
        var inner = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(
            await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        await using var ann = new SourceFaultAnn(inner, fault, async () =>
        {
            if (fault != "epoch-timeout") return;
            await using var db = await environment.Factory.CreateDbContextAsync();
            await db.IndexState.ExecuteUpdateAsync(set => set.SetProperty(p => p.CorpusEpoch, Guid.NewGuid()));
        });
        await ann.PrepareAsync(reader, default);
        var vector = await environment.Embeddings.CreateEmbeddingAsync("CRM", default);
        using var caller = new CancellationTokenSource();
        var pending = ((IHybridPassageCandidateReader)reader).ReadDenseCandidatesAsync(ann,
            new("root", [target.RootId], null), "CRM", vector.Values, caller.Token).AsTask();
        if (fault == "caller")
        {
            await ann.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else if (fault is "timeout" or "late")
        {
            if (fault == "late")
            {
                await ann.SourceBudgetCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(pending.IsCompleted); Assert.False(ann.SourceWorkSettled);
                ann.ReleaseSource.TrySetResult();
            }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ready", result.Status);
            Assert.Contains(result.Candidates, p => p.PipelineRecordId == target.RecordId);
            Assert.Empty(result.SourceDirectedCandidates);
            Assert.Equal(SourceDirectedRetrievalStatus.Timeout, result.SourceDirectedStatus);
        }
        else if (fault is "publication" or "epoch-timeout") await Assert.ThrowsAsync<PublicationSnapshotConflictException>(() => pending);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Equal("synthetic-source-identity-conflict", error.Message);
        }
        Assert.Equal(2, ann.SearchCalls);
        Assert.True(ann.SourceWorkSettled);
    }

    private SqlCorpusRetrievalReader RoutingReader(NoVectorPayloads commands) => new(new Factory(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(commands).Options), new CorpusAnnBindingCache());

    private static async Task<(Guid RecordId, Guid RootId, string Path)> AddRoutingDocument(
        SqlToUsearchRebuildTests.PipelineEnvironment environment, string text, string tag)
    {
        var recordId = await environment.AddRetainedAndPumpAsync(text, tag);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var record = await db.PipelineRecords.SingleAsync(p => p.Id == recordId);
        var revision = await db.SourceRevisions.SingleAsync(p => p.Id == record.SourceRevisionId);
        var root = await db.SourceRootConfigurations.SingleAsync(p => p.Id == revision.SourceRootId);
        // This synthetic pipeline fixture predates bound contextual search inputs.
        // Match the canonical synthetic inputs, as the existing captured-generation fixtures do.
        await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        return (recordId, root.Id, root.CanonicalPath);
    }

    private sealed class SourceFaultAnn(ICorpusAnnLease inner, string fault, Func<Task> beforeSource) : ICorpusAnnLease
    {
        public int SearchCalls { get; private set; }
        public bool SourceWorkSettled { get; private set; }
        public TaskCompletionSource SourceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SourceBudgetCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid LeaseId => inner.LeaseId;
        public IndexGenerationDescriptor Generation => inner.Generation;
        public bool IsPrepared(ICorpusAnnBindingPreparation metadata) => inner.IsPrepared(metadata);
        public ValueTask PrepareAsync(ICorpusAnnBindingPreparation metadata, CancellationToken ct) => inner.PrepareAsync(metadata, ct);
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => inner.IsCurrentAsync(ct);
        public ValueTask<bool> IsReadableAsync(CancellationToken ct) => inner.IsReadableAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken ct) => inner.SearchAsync(query, limit, ct);
        public async ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit,
            ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken ct)
        {
            if (++SearchCalls == 1) return await inner.SearchAsync(query, limit, scope, selection, ct);
            try
            {
                using var registration = ct.Register(() => SourceBudgetCancelled.TrySetResult());
                SourceStarted.TrySetResult();
                await beforeSource();
                if (fault == "late") { await ReleaseSource.Task; ct.ThrowIfCancellationRequested(); }
                if (fault is "timeout" or "caller" or "epoch-timeout") { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new Exception("Cancellation missing"); }
                if (fault == "publication") throw new PublicationSnapshotConflictException("synthetic source publication conflict");
                throw new InvalidOperationException("synthetic-source-identity-conflict");
            }
            finally { SourceWorkSettled = true; }
        }
    }
}
