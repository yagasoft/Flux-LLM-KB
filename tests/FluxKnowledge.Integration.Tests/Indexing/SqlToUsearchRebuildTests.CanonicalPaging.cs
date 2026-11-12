using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Canonical_rebuild_crosses_empty_raw_windows_and_restores_a_member_missing_from_the_projection()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Historical evidence.", segmented: true);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var original = await context.Vectors.AsNoTracking().SingleAsync();
        await context.Vectors.ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, true));
        await AddDeletedCanonicalPaddingAsync(context, original, 300);
        var first = await environment.AddRetainedAndPumpAsync("First current canonical evidence after an entirely ineligible window.");
        await AddDeletedCanonicalPaddingAsync(context, original, 300);
        var second = await environment.AddRetainedAndPumpAsync("Last current canonical evidence after another ineligible window.");
        var expected = await (from vector in context.Vectors join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
            where (artifact.PipelineRecordId == first || artifact.PipelineRecordId == second) && !vector.IsDeleted
            orderby vector.VectorId select vector.VectorId).ToArrayAsync();
        Assert.Equal(2, expected.Length);
        Assert.True(expected[0] - original.VectorId > 256);
        Assert.True(expected[1] - expected[0] > 256);
        await context.CurrentIndexMembers.Where(value => value.VectorId == expected[1]).ExecuteDeleteAsync();
        var store = new SqlSegmentedIndexStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        await store.PrepareRebuildAsync(plan, CancellationToken.None);
        Assert.Equal(expected, await context.IndexCandidateMembers.Where(value => value.CandidateId == plan.OperationId && value.Phase == 1)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
        var options = UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot);
        var paths = new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        var rebuilt = await new UsearchProjectionRebuilder(options, paths, store, store,
            new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System),
            new UsearchLegacyProjectionBuilder(options, new WindowsIndexGenerationReadLeaseFactory(environment.IndexRoot)))
            .RunAsync(plan, CancellationToken.None);
        Assert.Equal(expected.Length, rebuilt.VectorCount);
        Assert.Equal(expected, await context.CurrentIndexMembers.OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Canonical_rebuild_finishes_when_every_raw_window_is_ineligible()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Historical evidence.", segmented: true);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var original = await context.Vectors.AsNoTracking().SingleAsync();
        await context.Vectors.ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, true));
        await AddDeletedCanonicalPaddingAsync(context, original, 600);
        var store = new SqlSegmentedIndexStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var plan = await store.ReadRebuildPlanAsync(Guid.NewGuid(), 2, CancellationToken.None);
        await store.PrepareRebuildAsync(plan, CancellationToken.None);
        var candidate = await context.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == plan.OperationId);
        Assert.True(candidate.WorklistComplete);
        Assert.Equal(0, candidate.AfterCount);
        Assert.Equal(-1, candidate.LastAppendOrdinal);
        Assert.Empty(await context.IndexCandidateMembers.Where(value => value.CandidateId == plan.OperationId).ToArrayAsync());
    }

    private static async Task AddDeletedCanonicalPaddingAsync(FluxKnowledgeDbContext context, VectorEntity template, int count)
    {
        var batch = Guid.NewGuid().ToString("N");
        context.Vectors.AddRange(Enumerable.Range(0, count).Select(index => new VectorEntity
        {
            TextChunkId = template.TextChunkId, ModelFingerprint = $"deleted-padding-{batch}-{index}",
            Dimensions = template.Dimensions, Values = template.Values, TextChunkContentHash = template.TextChunkContentHash,
            PayloadChecksum = template.PayloadChecksum, SearchInputHash = template.SearchInputHash,
            SourceRevision = template.SourceRevision, IsDeleted = true, IndexGenerationId = template.IndexGenerationId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        }));
        await context.SaveChangesAsync();
    }
}
