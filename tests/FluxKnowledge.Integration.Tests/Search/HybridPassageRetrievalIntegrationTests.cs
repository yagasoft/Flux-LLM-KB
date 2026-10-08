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
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed class HybridPassageRetrievalIntegrationTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
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
        var phases = trace.Events.Where(entry => entry.Id == 6 && Equals(entry["searchId"], selected["searchId"])).ToArray();
        Assert.Contains(phases, entry => Equals(entry["phase"], "inference-admission") && Equals(entry["outcome"], "begin"));
        Assert.Contains(phases, entry => Equals(entry["phase"], "generation-lease") && Equals(entry["outcome"], "completed"));
        Assert.Contains(phases, entry => Equals(entry["phase"], "dense-selection") && Equals(entry["outcome"], "completed"));
        Assert.Contains(phases, entry => Equals(entry["phase"], "rerank") && Equals(entry["outcome"], "completed"));
        Assert.All(phases, entry =>
        {
            Assert.Equal(activity.TraceId.ToString(), entry["traceId"]);
            Assert.Equal(activity.SpanId.ToString(), entry["spanId"]);
            Assert.InRange((double)entry["elapsedMs"]!, 0, (double)completed["elapsedMs"]!);
        });
        var serialized = System.Text.Json.JsonSerializer.Serialize(trace.Events);
        Assert.DoesNotContain(body, serialized);
        Assert.DoesNotContain("holiday entitlement", serialized);
        Assert.DoesNotContain(trace.Events, entry => entry.Id == 0);
    }

    [NativeSqlServerFact]
    public async Task Independently_loaded_code_proofs_preserve_hybrid_search_and_exact_readback()
    {
        const string method = "public int LateAnswer() {\nreturn 42;\n}";
        var code = string.Concat(Enumerable.Repeat("// safe padding\n", 1_500)) +
            "namespace Sample { class C {\n" + method + "\n} }";
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline.", Builder);
        await environment.AddAndPumpAtPathAsync(code, "late.cs");
        await BindInputsAsync(environment);
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(
            Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, DeterministicTokenHashEmbeddingProvider.Fingerprint,
            256, CancellationToken.None);
        Assert.NotNull(lease);
        await using (var ann = await new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator())
            .OpenAsync(lease, CancellationToken.None))
        {
            var query = await environment.Embeddings.CreateEmbeddingAsync("LateAnswer", CancellationToken.None);
            var dense = await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query.Values, CancellationToken.None);
            Assert.Contains(dense.Candidates, candidate => candidate.DisclosureProof is not null);
        }
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var engine = new HybridPassageRetrievalEngine(reader, reader,
            new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System),
            new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator()), new Models(environment.Embeddings),
            new WindowsInteractiveGpuOwnerProbe(), codec, new LocalPrivateContentDisclosure());
        var response = await engine.SearchAsync("LateAnswer", new("all", [], null), 3, CancellationToken.None);
        Assert.Equal("ready", response.SemanticStatus);
        var hit = Assert.Single(response.Results, result => result.Passage.Contains("LateAnswer", StringComparison.Ordinal));
        var readContext = await reader.ReadAsync(codec.Decode(hit.EvidenceRef), 4096, CancellationToken.None);
        Assert.NotNull(readContext);
        Assert.NotNull(readContext.TextProof);
        Assert.False(new LocalPrivateContentDisclosure().EvaluateCode(readContext.Text,
            FluxKnowledge.Application.Visibility.LocalDisclosureKind.RetainedDetail, readContext.TextProof).Withheld);
        Assert.NotNull(readContext.DisclosureText);
        Assert.False(new LocalPrivateContentDisclosure().EvaluateCode(readContext.DisclosureText,
            FluxKnowledge.Application.Visibility.LocalDisclosureKind.RetainedDetail, readContext.GuardProof).Withheld);
        var read = await new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure())
            .ReadAsync(new(hit.EvidenceRef, 4096), CancellationToken.None);
        Assert.Contains(method, read.Text, StringComparison.Ordinal);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.True(await db.CanonicalCodeDisclosureProofs.AnyAsync());
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
        await using var ann = new NoGlobalAnn(sqlLease);
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
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(10000)]
    [InlineData(10001)]
    public async Task Scoped_selection_returns_the_exact_oracle_including_a_winner_after_the_former_cutoff(int count)
    {
        var builder = new PassageBuilder(new Words(), new PassagePolicy(2, 2, 128, 0, 0));
        const string winning = "RepositoryWinner EvidenceMarker.";
        var text = string.Join("\n", Enumerable.Range(0, count).Select(i => i == count - 1 ? winning : $"Segment {i:D5}."));
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
        await using var ann = new NoGlobalAnn(sqlLease);
        var query = await environment.Embeddings.CreateEmbeddingAsync(winning, CancellationToken.None);
        var oracleVectors = await (from vector in context.Vectors join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
            where artifact.PipelineRecordId == recordId select new { vector.VectorId, vector.TextChunkId, vector.Values })
            .ToArrayAsync();
        var oracle = oracleVectors.Select(vector => new
        {
            vector.VectorId, vector.TextChunkId,
            Score = 1d - Enumerable.Range(0, query.Values.Count).Sum(index =>
                (double)BitConverter.ToSingle(vector.Values, index * sizeof(float)) * query.Values[index])
        }).OrderBy(vector => vector.Score).ThenBy(vector => vector.VectorId).Take(100).ToArray();
        Assert.Equal(oracleVectors.Max(vector => vector.VectorId), oracle[0].VectorId);
        using var trace = new HybridSearchTraceListener(sampleLiveMemory: true);
        using var activity = new System.Diagnostics.Activity("scoped exact selection").Start();
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);
        var baselineAllocation = GC.GetTotalAllocatedBytes(precise: true);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await new SqlCorpusRetrievalReader(environment.Factory).ReadDenseCandidatesAsync(ann,
            new("root", [rootId], null), query.Values, CancellationToken.None);
        Assert.Equal("ready", result.Status);
        Assert.Equal(oracle.Select(vector => vector.TextChunkId), result.Candidates.Select(value => value.ChunkId));
        Assert.Equal(winning, result.Candidates[0].Content.Trim());
        Assert.All(result.Candidates, passage => Assert.Equal(rootId, passage.RootId));
        var pages = trace.Events.Where(entry => entry.Id == 5 && Equals(entry["traceId"], activity.TraceId.ToString())).ToArray();
        Assert.Equal(count, pages.Sum(entry => (int)entry["rows"]!));
        Assert.Equal(count / 256 + 1, pages.Length);
        Assert.All(pages, entry =>
        {
            Assert.InRange((int)entry["rows"]!, 0, 256);
            Assert.InRange((int)entry["retainedCandidates"]!, 0, 100);
            Assert.Equal((int)entry["rows"]! * 256L * sizeof(float), (long)entry["payloadBytes"]!);
        });
        output.WriteLine("Scope={0}; pages={1}; search_ms={2:F1}; peak_live_delta_bytes={3}; allocated_bytes={4} (allocation is cumulative, not retained)",
            count, pages.Length, timer.Elapsed.TotalMilliseconds,
            Math.Max(0, trace.PeakLiveManagedBytes - baselineMemory), GC.GetTotalAllocatedBytes(precise: true) - baselineAllocation);
        // Keep the independently materialised oracle alive across both memory samples;
        // otherwise its collection would conceal the reader's retained page/heap cost.
        GC.KeepAlive(oracleVectors);
        GC.KeepAlive(oracle);
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
    private sealed class NoGlobalAnn(ICorpusGenerationLease inner) : ICorpusAnnLease
    {
        public Guid LeaseId => inner.LeaseId;
        public IndexGenerationDescriptor Generation => inner.Generation;
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => inner.IsCurrentAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken ct) =>
            throw new InvalidOperationException("Scoped search must not call global ANN.");
    }
}
