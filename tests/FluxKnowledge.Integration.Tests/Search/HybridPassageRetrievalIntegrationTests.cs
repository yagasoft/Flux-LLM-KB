using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed class HybridPassageRetrievalIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private static readonly PassageBuilder Builder = new(new Words());
    public async Task InitializeAsync()
    {
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await context.SourceDeletionCleanupItems.ExecuteDeleteAsync();
        await context.SourceDeletionOperations.ExecuteDeleteAsync();
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Complete_hybrid_passage_flows_through_real_sql_ann_ranking_and_evidence_readback()
    {
        using var trace = new HybridSearchTraceListener();
        using var activity = new System.Diagnostics.Activity("search acceptance").Start();
        const string body = "Employees receive twenty days of annual leave.";
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, body, Builder);
        await BindInputsAsync(environment);
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var models = new Models(environment.Embeddings);
        var engine = new HybridPassageRetrievalEngine(reader, reader,
            new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System),
            new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator()), models,
            new WindowsInteractiveGpuOwnerProbe(), codec, new LocalPrivateContentDisclosure());
        var response = await engine.SearchAsync("holiday entitlement", new("all", [], null), 3, CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Equal(body, hit.Passage);
        Assert.Contains("semantic:dense", hit.Explanation);
        Assert.Contains("rerank:trained", hit.Explanation);
        Assert.Equal("ready", response.SemanticStatus);
        Assert.Equal(hit.ChunkId, Assert.Single(models.Inputs).PassageId);
        var read = await new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure(), new(true))
            .ReadAsync(new(hit.EvidenceRef, 0), CancellationToken.None);
        Assert.Equal(hit.Passage, read.Text);
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await context.CorpusQueryLeases.ToArrayAsync());
        var selected = Assert.Single(trace.Events, entry => entry.Id == 1 && Equals(entry["traceId"], activity.TraceId.ToString()));
        Assert.Contains("spanId", selected.Names);
        Assert.Equal(activity.SpanId.ToString(), selected["spanId"]);
        Assert.Equal(hit.ChunkId.ToString(System.Globalization.CultureInfo.InvariantCulture), selected["denseIds"]);
        Assert.Equal(selected["denseIds"], selected["shortlistIds"]);
        Assert.Equal(string.Empty, selected["lexicalIds"]);
        var completed = Assert.Single(trace.Events, entry => entry.Id == 2 && Equals(entry["traceId"], activity.TraceId.ToString()));
        Assert.Equal("ready", completed["semanticStatus"]);
        Assert.Equal(selected["searchId"], completed["searchId"]);
        Assert.Equal(selected["spanId"], completed["spanId"]);
        Assert.Equal(selected["shortlistIds"], completed["resultIds"]);
        Assert.True((double)completed["elapsedMs"]! >= 0);
        var serialized = System.Text.Json.JsonSerializer.Serialize(trace.Events);
        Assert.DoesNotContain(body, serialized);
        Assert.DoesNotContain("holiday entitlement", serialized);
        Assert.DoesNotContain(trace.Events, entry => entry.Id == 0);
    }

    [NativeSqlServerFact]
    public async Task Root_and_workspace_dense_search_use_complete_scoped_membership_without_global_ann_filtering()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrelated global answer.", Builder);
        var recordId = await environment.AddRetainedAndPumpAsync("The scoped annual allowance is twenty days.");
        _ = await environment.AddRetainedAndPumpAsync("The other root has ninety days.");
        await BindInputsAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var revision = await (from record in context.PipelineRecords join source in context.SourceRevisions on record.SourceRevisionId equals source.Id
            where record.Id == recordId select source).SingleAsync();
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var sqlLease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(
            Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, CancellationToken.None);
        Assert.NotNull(sqlLease);
        await using var ann = new NoGlobalAnn(await new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator())
            .OpenAsync(sqlLease, CancellationToken.None));
        var query = await environment.Embeddings.CreateEmbeddingAsync("holiday entitlement", CancellationToken.None);
        foreach (var scope in new[]
        {
            new ResolvedCorpusScope("root", [revision.SourceRootId], null),
            new ResolvedCorpusScope("workspace", [revision.SourceRootId], Path.GetDirectoryName(revision.CanonicalPath))
        })
        {
            var result = await reader.ReadDenseCandidatesAsync(ann, scope, query.Values, CancellationToken.None);
            Assert.Equal("ready", result.Status);
            var passage = Assert.Single(result.Candidates);
            Assert.Equal(recordId, passage.PipelineRecordId);
            Assert.Equal(revision.SourceRootId, passage.RootId);
            Assert.Equal("The scoped annual allowance is twenty days.", passage.Content);
        }
    }

    [NativeSqlServerTheory]
    [InlineData(10000, "ready")]
    [InlineData(10001, "scope-capacity-exceeded")]
    public async Task Scope_capacity_is_checked_over_the_entire_eligible_generation_before_top_k(int count, string expectedStatus)
    {
        var builder = new PassageBuilder(new Words(), new PassagePolicy(2, 2, 128, 0, 0));
        var text = string.Join("\n", Enumerable.Range(0, count).Select(i => $"Segment {i:D5}."));
        Assert.Equal(count, builder.Build(text).Count);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline.", builder);
        var recordId = await environment.AddRetainedAndPumpAsync(text);
        await BindInputsAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var rootId = await (from record in context.PipelineRecords join source in context.SourceRevisions on record.SourceRevisionId equals source.Id
            where record.Id == recordId select source.SourceRootId).SingleAsync();
        var sqlLease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(
            Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, CancellationToken.None);
        Assert.NotNull(sqlLease);
        await using var ann = new NoGlobalAnn(await new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator())
            .OpenAsync(sqlLease, CancellationToken.None));
        var query = await environment.Embeddings.CreateEmbeddingAsync("Segment", CancellationToken.None);
        var result = await new SqlCorpusRetrievalReader(environment.Factory).ReadDenseCandidatesAsync(ann,
            new("root", [rootId], null), query.Values, CancellationToken.None);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(count == 10000 ? 100 : 0, result.Candidates.Count);
        Assert.All(result.Candidates, passage => Assert.Equal(rootId, passage.RootId));
    }

    private static async Task BindInputsAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        // The established model-free unbatched fixture predates mandatory checkpoint
        // input binding. Bind its synthetic vectors to the exact persisted search input.
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
    }
    private sealed class Words : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-words-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
    private sealed class Models(IEmbeddingProvider embedding) : IScheduledPassageInference, IPassageReranker
    {
        public EmbeddingProfile EmbeddingProfile { get; } = new(DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
        public string RerankerFingerprint => "synthetic-ranker";
        public IReadOnlyList<RerankPassage> Inputs { get; private set; } = [];
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken ct) => work(embedding, this, ct);
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
        { Inputs = passages; return ValueTask.FromResult(new RerankResult(passages.Select((p, i) => new RerankScore(p.PassageId, -i)).ToArray(), RerankerFingerprint)); }
    }
    private sealed class NoGlobalAnn(ICorpusAnnLease inner) : ICorpusAnnLease
    {
        public Guid LeaseId => inner.LeaseId;
        public IndexGenerationDescriptor Generation => inner.Generation;
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => inner.IsCurrentAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken ct) =>
            throw new InvalidOperationException("Scoped search must not call global ANN.");
    }
}
