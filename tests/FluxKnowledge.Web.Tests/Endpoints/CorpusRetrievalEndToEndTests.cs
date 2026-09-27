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
        await using var app = builder.Build();
        app.MapFluxKnowledgeNativeV1();
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
