using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Generation_capture_preserves_membership_owned_overlap_shared_and_format_boundaries(bool membership, bool owned)
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("capture-target", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("capture-control", SourceRootState.Enabled, now);
        var operation = await AddDeletionAsync(source, now);
        var target = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var format2 = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            foreach (var id in new[] { target, unrelated, format2 })
                setup.IndexGenerations.Add(new() { Id = id, FormatVersion = id == format2 ? 2 : 1,
                    ModelFingerprint = "source-deletion-test:1", Dimensions = 1, IndexPath = "synthetic-capture-" + id.ToString("N"),
                    MetadataChecksum = new string('a', 64), VectorCount = 0, CreatedAtUtc = now, ValidatedAtUtc = now });
            if (owned)
                setup.Artifacts.Add(new() { Id = Guid.NewGuid(), PipelineRecordId = source.RecordId, SourceRevision = 1,
                    Stage = (int)PipelineStage.Embed, ContentHash = new string('a', 64),
                    ContentType = EmbedDraftDefaults.ArtifactContentType, SearchText = target.ToString("D"), CreatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var foreign = await AddVectorAsync(control, target, "foreign shared membership", now);
        await using (var setup = CreateContext())
        {
            setup.IndexGenerationVectors.Add(new() { GenerationId = target, VectorId = foreign.VectorId });
            var controlRecord = await setup.PipelineRecords.SingleAsync(value => value.Id == control.RecordId);
            controlRecord.CompletionCriteriaMet = false;
            controlRecord.CurrentStage = 0;
            await setup.SaveChangesAsync();
        }
        if (membership)
        {
            var vector = await AddVectorAsync(source, target, "owned membership", now);
            await using var setup = CreateContext();
            setup.IndexGenerationVectors.AddRange(new IndexGenerationVectorEntity { GenerationId = target, VectorId = vector.VectorId },
                new IndexGenerationVectorEntity { GenerationId = format2, VectorId = vector.VectorId });
            await setup.SaveChangesAsync();
        }
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operation, stopAtCutover: true);
        await using var check = CreateContext();
        var captured = await check.SourceDeletionCleanupItems.Where(value => value.SourceDeletionOperationId == operation && value.StorageKind == 2)
            .Select(value => value.RelativePath).ToArrayAsync();
        Assert.Equal(membership || owned ? [target.ToString("N")] : Array.Empty<string>(), captured);
        Assert.False(await check.IndexGenerations.Where(value => value.Id == unrelated || value.Id == format2)
            .AnyAsync(value => value.RetiredAtUtc != null));
        Assert.False((await check.Vectors.SingleAsync(value => value.VectorId == foreign.VectorId)).IsDeleted);
    }
}
