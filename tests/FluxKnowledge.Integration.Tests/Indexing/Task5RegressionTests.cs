using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class Task5RegressionTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Deleted_latest_revision_suppresses_the_source_without_resurrecting_older_vectors()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var sourceId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceIdentities.Add(new SourceIdentityEntity { Id = sourceId, SourceKind = "local file", StableKey = "C:\\revision.txt", CreatedAtUtc = DateTimeOffset.UtcNow });
            context.IndexGenerations.Add(new IndexGenerationEntity { Id = origin, ModelFingerprint = "deterministic-tokenhash-v1:256", Dimensions = 256, IndexPath = "pending", MetadataChecksum = new string('0', 64), CreatedAtUtc = DateTimeOffset.UtcNow });
            await AddRevisionAsync(context, first, sourceId, 1, false, origin, 1);
            await AddRevisionAsync(context, second, sourceId, 2, true, origin, 2);
            await context.SaveChangesAsync();
        }

        var eligible = await new SqlPipelineStore(factory).ReadEligibleVectorsAsync(CancellationToken.None);

        Assert.Empty(eligible);
    }

    [NativeSqlServerFact]
    public async Task Retained_unsuppressed_revision_remains_eligible_while_legacy_records_keep_latest_record_semantics()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var rootId = Guid.NewGuid();
        var retainedIdentityId = Guid.NewGuid();
        var legacyIdentityId = Guid.NewGuid();
        var retainedCurrentRevisionId = Guid.NewGuid();
        var retainedSuppressedRevisionId = Guid.NewGuid();
        var retainedCurrentRecordId = Guid.NewGuid();
        var retainedSuppressedRecordId = Guid.NewGuid();
        var legacyOlderRecordId = Guid.NewGuid();
        var legacyDeletedLatestRecordId = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = $"C:\\retained-eligibility\\{rootId:N}", DisplayName = "Retained eligibility",
                State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false,
                MaximumFileBytes = 16 * 1024 * 1024, AllowedClassificationsJson = "[]", CrawlMode = 0,
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            context.SourceIdentities.AddRange(
                new SourceIdentityEntity { Id = retainedIdentityId, SourceKind = "retained local source", StableKey = "retained-source", CreatedAtUtc = now },
                new SourceIdentityEntity { Id = legacyIdentityId, SourceKind = "local file", StableKey = "legacy-source", CreatedAtUtc = now });
            context.SourceRevisions.AddRange(
                CreateSourceRevision(rootId, retainedCurrentRevisionId, "retained-source", 1, suppressedAtUtc: null, now),
                CreateSourceRevision(rootId, retainedSuppressedRevisionId, "retained-source", 2, suppressedAtUtc: now, now));
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = origin, ModelFingerprint = "deterministic-tokenhash-v1:256", Dimensions = 256,
                IndexPath = "pending", MetadataChecksum = new string('0', 64), CreatedAtUtc = now
            });
            await AddRevisionAsync(context, retainedCurrentRecordId, retainedIdentityId, 1, false, origin, 1,
                retainedCurrentRevisionId, "retained eligible text");
            await AddRevisionAsync(context, retainedSuppressedRecordId, retainedIdentityId, 2, false, origin, 2,
                retainedSuppressedRevisionId, "retained suppressed text");
            await AddRevisionAsync(context, legacyOlderRecordId, legacyIdentityId, 1, false, origin, 3,
                null, "legacy older text");
            await AddRevisionAsync(context, legacyDeletedLatestRecordId, legacyIdentityId, 2, true, origin, 4,
                null, "legacy deleted latest text");
            await context.SaveChangesAsync();
        }

        await using var verification = await factory.CreateDbContextAsync();
        var vectorIdByRecord = await (
                from vector in verification.Vectors
                join chunk in verification.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in verification.Artifacts on chunk.ArtifactId equals artifact.Id
                select new { artifact.PipelineRecordId, vector.VectorId })
            .ToDictionaryAsync(row => row.PipelineRecordId, row => row.VectorId);
        var retainedCurrentVectorId = vectorIdByRecord[retainedCurrentRecordId];
        var suppressedVectorId = vectorIdByRecord[retainedSuppressedRecordId];
        var legacyOlderVectorId = vectorIdByRecord[legacyOlderRecordId];
        var legacyDeletedLatestVectorId = vectorIdByRecord[legacyDeletedLatestRecordId];

        var eligible = await new SqlPipelineStore(factory).ReadEligibleVectorsAsync(CancellationToken.None);
        Assert.Equal([retainedCurrentVectorId], eligible.Select(vector => vector.VectorId));

        var hydrated = await new SqlSearchHydrator(factory).HydrateAsync(
            [
                new FusedCandidate(retainedCurrentVectorId, 4, 1, null),
                new FusedCandidate(suppressedVectorId, 3, 2, null),
                new FusedCandidate(legacyOlderVectorId, 2, 3, null),
                new FusedCandidate(legacyDeletedLatestVectorId, 1, 4, null)
            ],
            10,
            CancellationToken.None);
        Assert.Equal([retainedCurrentRecordId], hydrated.Select(hit => hit.PipelineRecordId.Value));

        var lexical = new SqlFullTextSearch(factory);
        IReadOnlyList<RankedCandidate> matches = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (matches.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            matches = await lexical.SearchAsync("retained", 10, CancellationToken.None);
            if (matches.Count == 0)
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal([retainedCurrentVectorId], matches.Select(match => match.VectorId));
    }

    [NativeSqlServerFact]
    public async Task Repeated_cleanup_clears_active_generation_before_membership_and_generation_rows()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        await SqlTestData.ClearPipelineAsync(_fixture);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Null((await context.IndexState.SingleAsync(state => state.Id == 1)).ActiveIndexGenerationId);
        Assert.Empty(await context.IndexGenerationVectors.ToListAsync());
        Assert.Empty(await context.IndexGenerations.ToListAsync());
    }

    private static async Task AddRevisionAsync(
        FluxKnowledgeDbContext context,
        Guid recordId,
        Guid sourceId,
        long revision,
        bool deleted,
        Guid origin,
        long ordinal,
        Guid? retainedSourceRevisionId = null,
        string searchText = "text")
    {
        var artifactId = Guid.NewGuid();
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(searchText)));
        context.PipelineRecords.Add(new PipelineRecordEntity { Id = recordId, SourceIdentityId = sourceId, SourceRevisionId = retainedSourceRevisionId, Revision = revision, ContentHash = contentHash, RootLineageRecordId = recordId, CurrentStage = 3, RegisteredAtUtc = DateTimeOffset.UtcNow, IsDeleted = deleted });
        context.Artifacts.Add(new ArtifactEntity { Id = artifactId, PipelineRecordId = recordId, SourceRevision = revision, Stage = 3, ContentHash = contentHash, ContentType = "text/plain", SearchText = searchText, CreatedAtUtc = DateTimeOffset.UtcNow });
        var chunk = new TextChunkEntity { ArtifactId = artifactId, SourceRevision = revision, Ordinal = 0, StartOffset = 0, Length = searchText.Length, Content = searchText, ContentHash = contentHash };
        context.TextChunks.Add(chunk);
        await context.SaveChangesAsync();
        var values = new byte[1024];
        context.Vectors.Add(new VectorEntity { TextChunkId = chunk.Id, SourceRevision = revision, ModelFingerprint = "deterministic-tokenhash-v1:256", Dimensions = 256, Values = values, TextChunkContentHash = chunk.ContentHash, PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)), IndexGenerationId = origin, CreatedAtUtc = DateTimeOffset.UtcNow });
    }

    private static SourceRevisionEntity CreateSourceRevision(
        Guid rootId,
        Guid revisionId,
        string stableIdentity,
        long revision,
        DateTimeOffset? suppressedAtUtc,
        DateTimeOffset now) => new()
    {
        Id = revisionId,
        SourceRootId = rootId,
        StableSourceIdentity = stableIdentity,
        Revision = revision,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"C:\\retained-eligibility\\{revisionId:N}.txt",
        Classification = "AcceptedUtf8Text",
        Extension = ".txt",
        ByteLength = 1,
        DiscoveredAtUtc = now,
        SuppressedAtUtc = suppressedAtUtc,
        DiscoveryEvidenceJson = "{}"
    };
}
