using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
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
        const string canonicalText = "A retained ordinary text answer, evidence marker INV-42.";
        var hash = Hash(canonicalText);
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
                ContentHash = hash
            });
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new CorpusEvidenceCodec(new EphemeralDataProtectionProvider()),
            new LocalPrivateContentDisclosure());
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
        Assert.Contains("INV-42", hit.GetProperty("passage").GetString());
        Assert.Equal(rootId, hit.GetProperty("rootId").GetGuid());
        var evidenceRef = hit.GetProperty("evidenceRef").GetString();

        using var read = await client.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = evidenceRef, context_characters = 128 });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Contains(canonicalText, readJson.RootElement.GetProperty("result").GetProperty("text").GetString());

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
