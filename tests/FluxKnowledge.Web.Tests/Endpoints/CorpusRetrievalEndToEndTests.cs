using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Web.NativeV1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class CorpusRetrievalEndToEndTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Scoped_HTTP_hybrid_search_above_ten_thousand_vectors_returns_exact_citations_without_ANN_and_deletion_invalidates_them()
    {
        const string winning = "RepositoryWinner EvidenceMarker.";
        var text = string.Join('\n', Enumerable.Range(0, 10_001).Select(index => index == 10_000 ? winning : $"Segment {index:D5}."));
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline.",
            new PassageBuilder(new WordTokenizer(), new PassagePolicy(2, 2, 128, 0, 0)));
        var record = await pipeline.AddRetainedAndPumpAsync(text);
        await using var db = await pipeline.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var root = await (from value in db.PipelineRecords join source in db.SourceRevisions on value.SourceRevisionId equals source.Id
            where value.Id == record select source.SourceRootId).SingleAsync();
        var reader = new SqlCorpusRetrievalReader(pipeline.Factory);
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var ann = new NoAnnFactory();
        var engine = new HybridPassageRetrievalEngine(reader, reader,
            new SqlCorpusGenerationLeaseStore(pipeline.Factory, TimeProvider.System), ann, new Models(pipeline.Embeddings),
            new WindowsInteractiveGpuOwnerProbe(), codec, new LocalPrivateContentDisclosure(), searchTimeout: TimeSpan.FromSeconds(25));
        var service = new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure(), new(true), engine);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<INativeV1Facade>(new CorpusOnlyFacade(service));
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        await using var app = builder.Build();
        app.MapFluxKnowledgeNativeV1();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var search = await client.PostAsJsonAsync("/api/v1/corpus/search", new { query = "RepositoryWinner EvidenceMarker", scope = "root", root_id = root, limit = 1 });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var result = searchJson.RootElement.GetProperty("result");
        Assert.Equal("hybrid", result.GetProperty("retrievalMode").GetString());
        Assert.Equal("ready", result.GetProperty("semanticStatus").GetString());
        var hit = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal(winning, hit.GetProperty("passage").GetString()!.Trim());
        Assert.Equal(root, hit.GetProperty("rootId").GetGuid());
        var request = new { evidence_ref = hit.GetProperty("evidenceRef").GetString(), context_characters = 0 };
        using var read = await client.PostAsJsonAsync("/api/v1/corpus/read", request);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(hit.GetProperty("passage").GetString(), readJson.RootElement.GetProperty("result").GetProperty("text").GetString());
        Assert.Equal(0, ann.OpenCalls);
        Assert.Empty(await db.CorpusQueryLeases.ToArrayAsync());
        await db.PipelineRecords.Where(value => value.Id == record).ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, true));
        using var stale = await client.PostAsJsonAsync("/api/v1/corpus/read", request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleJson = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("evidence-stale", staleJson.RootElement.GetProperty("reasonCode").GetString());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_code_proof_returns_the_whole_late_method_over_HTTP_and_withholds_encoded_credentials(bool privateLiteral)
    {
        var method = privateLiteral ? "public string LateAnswer() {\nreturn \"eyJwYXNzd29yZCI6InN5bnRoZXRpYyJ9\";\n}" :
            "public int LateAnswer() {\nreturn 42;\n}";
        var code = "// 😀\r\n" + string.Concat(Enumerable.Repeat("// safe padding\r\n", 1_500)) +
            "namespace Sample { class C {\r\n" + method.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n} }";
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(
            fixture, "Baseline.", new PassageBuilder(new WordTokenizer()));
        await pipeline.AddAndPumpAtPathAsync(code, "late.cs");
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(pipeline.Factory),
            new CorpusEvidenceCodec(new EphemeralDataProtectionProvider()), new LocalPrivateContentDisclosure(), new(true));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<INativeV1Facade>(new CorpusOnlyFacade(service));
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        await using var app = builder.Build();
        app.MapFluxKnowledgeNativeV1();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var search = await client.PostAsJsonAsync("/api/v1/corpus/search", new { query = "LateAnswer", scope = "all", limit = 1 });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var results = searchJson.RootElement.GetProperty("result").GetProperty("results");
        if (privateLiteral)
        {
            Assert.Empty(results.EnumerateArray());
            Assert.DoesNotContain("eyJwYXNz", searchJson.RootElement.ToString(), StringComparison.Ordinal);
            return;
        }
        var hit = Assert.Single(results.EnumerateArray());
        Assert.True(hit.GetProperty("startOffset").GetInt32() > 16_384);
        using var read = await client.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = hit.GetProperty("evidenceRef").GetString(), context_characters = 4096 });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var result = readJson.RootElement.GetProperty("result");
        Assert.Contains(method, result.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(hit.GetProperty("startOffset").GetInt32(), result.GetProperty("citedStart").GetInt32());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_ingress_workers_publication_HTTP_search_and_reference_read_return_the_same_complete_passage(bool hybrid)
    {
        var text = string.Concat(Enumerable.Repeat("Every payment requires a manager approval. ", 8)) +
            "INV-42 confirms the final approved payment.";
        var passages = new PassageBuilder(new WordTokenizer(), new PassagePolicy(12, 16, 160, 8, 80));
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, text, passages);
        var reader = new SqlCorpusRetrievalReader(pipeline.Factory);
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        HybridPassageRetrievalEngine? engine = null;
        if (hybrid)
        {
            await using var context = await pipeline.Factory.CreateDbContextAsync();
            await context.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
            engine = new(reader, reader, new SqlCorpusGenerationLeaseStore(pipeline.Factory, TimeProvider.System),
                new UsearchCorpusAnnLeaseFactory(pipeline.Store, new UsearchGenerationValidator()), new Models(pipeline.Embeddings),
                new WindowsInteractiveGpuOwnerProbe(), codec, new LocalPrivateContentDisclosure());
        }
        var service = new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure(), new CorpusRetrievalOptions(true), engine);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<INativeV1Facade>(new CorpusOnlyFacade(service));
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        await using var app = builder.Build();
        app.MapFluxKnowledgeNativeV1();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var search = await client.PostAsJsonAsync("/api/v1/corpus/search", new { query = "INV-42", scope = "all", limit = 1 });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var result = searchJson.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("candidateCount", out _));
        Assert.Equal(hybrid ? "hybrid" : "lexical", result.GetProperty("retrievalMode").GetString());
        if (hybrid) Assert.Equal("ready", result.GetProperty("semanticStatus").GetString());
        var hit = Assert.Single(result.GetProperty("results").EnumerateArray());
        var body = hit.GetProperty("passage").GetString();
        await using (var context = await pipeline.Factory.CreateDbContextAsync())
            Assert.Equal((await context.TextChunks.SingleAsync(c => c.Id == hit.GetProperty("chunkId").GetInt64())).Content, body);
        using var read = await client.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = hit.GetProperty("evidenceRef").GetString(), context_characters = 0 });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(body, readJson.RootElement.GetProperty("result").GetProperty("text").GetString());
    }

    [NativeSqlServerFact]
    public async Task Published_plain_text_is_searchable_and_readable_over_real_HTTP_and_SQL()
    {
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).Options;
        IDbContextFactory<FluxKnowledgeDbContext> factory = new LocalFactory(options);
        var rootId = Guid.NewGuid();
        var sourceRevisionId = Guid.NewGuid();
        var sourceIdentityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var canonicalText = string.Concat(Enumerable.Repeat("A retained ordinary text answer explains the approval process. ", 10)) +
            "The evidence marker is INV-42.";
        var hash = Hash(canonicalText);
        var passage = Assert.Single(new PassageBuilder(new WordTokenizer()).Build(canonicalText));
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = @"C:\retained-corpus", DisplayName = "Corpus",
                State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                MaximumFileBytes = 1024 * 1024, AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 60, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            context.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = sourceRevisionId, SourceRootId = rootId,
                StableSourceIdentity = @"C:\retained-corpus\sample.txt", Revision = 1,
                ContentSha256 = hash, CanonicalPath = @"C:\retained-corpus\sample.txt",
                Classification = "AcceptedUtf8Text", Extension = ".txt",
                ByteLength = canonicalText.Length, DiscoveredAtUtc = now
            });
            context.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceIdentityId, SourceKind = "local file",
                StableKey = @"C:\retained-corpus\sample.txt", CreatedAtUtc = now
            });
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId, SourceIdentityId = sourceIdentityId, SourceRevisionId = sourceRevisionId,
                Revision = 1, ContentHash = hash, RootLineageRecordId = recordId,
                CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true,
                RegisteredAtUtc = now
            });
            context.Artifacts.Add(new ArtifactEntity
            {
                Id = artifactId, PipelineRecordId = recordId, SourceRevision = 1,
                Stage = (int)PipelineStage.CanonicalIndex, ContentHash = hash,
                ContentType = "text/plain", SearchText = canonicalText, CreatedAtUtc = now
            });
            context.TextChunks.Add(new TextChunkEntity
            {
                ArtifactId = artifactId, SourceRevision = 1, Ordinal = 0,
                StartOffset = 0, Length = canonicalText.Length, Content = canonicalText,
                ContentHash = hash, PassagePolicyFingerprint = passage.PassagePolicyFingerprint,
                ContextHeader = passage.ContextHeader, SearchInputHash = passage.SearchInputHash
            });
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new CorpusEvidenceCodec(new EphemeralDataProtectionProvider()),
            new LocalPrivateContentDisclosure(), new CorpusRetrievalOptions(CoherentPassagesEnabled: true));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<INativeV1Facade>(new CorpusOnlyFacade(service));
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        builder.Services.AddSingleton<INativeOperationStore>(new SqlNativeOperationStore(factory, TimeProvider.System));
        builder.Services.AddSingleton<ICodexHookAuditWriter>(new SqlCodexHookAuditWriter(factory));
        builder.Services.AddSingleton<ICodexPromptContextService>(new CodexPromptContextService(service));
        builder.Services.AddSingleton<NativeCodexHookService>();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            await next(context);
        });
        app.UseLocalOperatorLoopbackGate();
        app.MapFluxKnowledgeNativeV1();
        app.MapFluxKnowledgeNativeCodexHooks();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var search = await client.PostAsJsonAsync("/api/v1/corpus/search",
            new { query = "INV-42", scope = "root", root_id = rootId });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var hit = Assert.Single(searchJson.RootElement.GetProperty("result").GetProperty("results").EnumerateArray());
        Assert.Equal(canonicalText, hit.GetProperty("passage").GetString());
        Assert.Equal(rootId, hit.GetProperty("rootId").GetGuid());
        var evidenceRef = hit.GetProperty("evidenceRef").GetString();

        using var read = await client.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = evidenceRef, context_characters = 0 });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(canonicalText, readJson.RootElement.GetProperty("result").GetProperty("text").GetString());

        using var hook = await client.PostAsJsonAsync("/native/v1/codex/hooks/UserPromptSubmit",
            new { prompt = "evidence marker", cwd = @"C:\retained-corpus" });
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        using var hookJson = JsonDocument.Parse(await hook.Content.ReadAsStringAsync());
        var packet = hookJson.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        using var packetRecord = JsonDocument.Parse(packet[(packet.IndexOf('\n') + 1)..]);
        Assert.Equal(canonicalText, packetRecord.RootElement.GetProperty("passage").GetString());
        var hookReference = packetRecord.RootElement.GetProperty("evidence_ref").GetString()!;
        Assert.Equal(canonicalText, (await service.ReadAsync(new CorpusReadRequest(hookReference, 0), CancellationToken.None)).Text);
        using var unscopedHook = await client.PostAsJsonAsync("/native/v1/codex/hooks/UserPromptSubmit",
            new { prompt = "evidence marker" });
        Assert.Equal("{\"continue\":true}", await unscopedHook.Content.ReadAsStringAsync());

        await using (var context = await factory.CreateDbContextAsync())
        {
            var record = await context.PipelineRecords.FindAsync(recordId);
            Assert.NotNull(record);
            record.IsDeleted = true;
            await context.SaveChangesAsync();
        }
        using var stale = await client.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = evidenceRef, context_characters = 128 });
        using var staleJson = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("evidence-stale", staleJson.RootElement.GetProperty("reasonCode").GetString());
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class WordTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-http-word-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class Models(IEmbeddingProvider embedding) : IScheduledPassageInference, IPassageReranker
    {
        public EmbeddingProfile EmbeddingProfile { get; } = new(DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
        public string RerankerFingerprint => "synthetic-http-ranker-v1";
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken ct) => work(embedding, this, ct);
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct) =>
            ValueTask.FromResult(new RerankResult(passages.Select((p, i) => new RerankScore(p.PassageId, -i)).ToArray(), RerankerFingerprint));
    }

    private sealed class NoAnnFactory : ICorpusAnnLeaseFactory
    {
        public int OpenCalls { get; private set; }
        public ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken cancellationToken)
        { OpenCalls++; throw new InvalidOperationException("Scoped requests must not load ANN vectors."); }
    }

    private sealed class LocalFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class CorpusOnlyFacade(CorpusRetrievalService service) : INativeV1Facade
    {
        public async ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            request switch
            {
                CorpusSearchRequest search => await service.SearchAsync(search, cancellationToken),
                CorpusReadRequest read => await service.ReadAsync(read, cancellationToken),
                _ => throw new NativeOperationException("invalid-request")
            };
        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
