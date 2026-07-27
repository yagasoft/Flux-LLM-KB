using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

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
        long ordinal)
    {
        var artifactId = Guid.NewGuid();
        context.PipelineRecords.Add(new PipelineRecordEntity { Id = recordId, SourceIdentityId = sourceId, Revision = revision, ContentHash = new string('a', 64), RootLineageRecordId = recordId, CurrentStage = 3, RegisteredAtUtc = DateTimeOffset.UtcNow, IsDeleted = deleted });
        context.Artifacts.Add(new ArtifactEntity { Id = artifactId, PipelineRecordId = recordId, SourceRevision = revision, Stage = 3, ContentHash = new string('a', 64), ContentType = "text/plain", SearchText = "text", CreatedAtUtc = DateTimeOffset.UtcNow });
        var chunk = new TextChunkEntity { ArtifactId = artifactId, SourceRevision = revision, Ordinal = 0, StartOffset = 0, Length = 4, Content = "text", ContentHash = new string('a', 64) };
        context.TextChunks.Add(chunk);
        await context.SaveChangesAsync();
        context.Vectors.Add(new VectorEntity { TextChunkId = chunk.Id, SourceRevision = revision, ModelFingerprint = "deterministic-tokenhash-v1:256", Dimensions = 256, Values = new byte[1024], ContentHash = new string('b', 64), IndexGenerationId = origin, CreatedAtUtc = DateTimeOffset.UtcNow });
    }
}
