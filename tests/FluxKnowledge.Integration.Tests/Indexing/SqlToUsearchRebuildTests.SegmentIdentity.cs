using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Sealing_cannot_adopt_a_physical_segment_from_another_build_incarnation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The candidate owns its physical build.", publish: false, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var draft = await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var candidate = await store.PrepareAsync(draft, CancellationToken.None);
        var incarnation = Guid.NewGuid();
        var builder = new UsearchSegmentBuilder(UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot));
        var segments = await builder.BuildAsync(candidate.ModelFingerprint, candidate.Dimensions,
            store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None), CancellationToken.None, incarnation: incarnation);
        var vectors = new List<CanonicalVector>();
        await foreach (var vector in store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None)) vectors.Add(vector);
        var segment = Assert.Single(segments) with { PhysicalIncarnation = incarnation };
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SealSegmentAsync(candidate.Id, segment, vectors, CancellationToken.None));
        Assert.Equal("index-segment-seal-invalid", refusal.Message);
        Assert.False(await db.IndexSegments.AnyAsync());
        Assert.True(File.Exists(Path.Combine(segment.DirectoryPath, "index.usearch")));
    }

    [NativeSqlServerTheory]
    [InlineData("unrelated")]
    [InlineData("duplicate")]
    [InlineData("checksum")]
    [InlineData("missing")]
    public async Task Segment_sealing_requires_exact_distinct_members_and_current_canonical_checksums(string fault)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            "The candidate must retain exact canonical membership.", publish: false, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var draft = await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var candidate = await store.PrepareAsync(draft, CancellationToken.None);
        var vectors = new List<CanonicalVector>();
        await foreach (var vector in store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None)) vectors.Add(vector);
        var original = Assert.Single(vectors);
        var builder = new UsearchSegmentBuilder(UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot),
            new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot));
        var segment = Assert.Single(await builder.BuildAsync(candidate.ModelFingerprint, candidate.Dimensions,
            store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None), CancellationToken.None, incarnation: candidate.Id));
        if (fault == "unrelated") vectors[0] = original with { VectorId = long.MaxValue };
        if (fault == "duplicate") { vectors.Add(original); segment = segment with { VectorCount = 2 }; }
        if (fault == "checksum") vectors[0] = original with { PayloadChecksum = new string('0', 64) };
        if (fault == "missing") await db.Vectors.Where(value => value.VectorId == original.VectorId).ExecuteDeleteAsync();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SealSegmentAsync(candidate.Id, segment, vectors, CancellationToken.None));
        Assert.Equal("index-segment-membership-invalid", refusal.Message);
        Assert.False(await db.IndexSegments.AnyAsync());
        Assert.False(await db.IndexSegmentVectors.AnyAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manifest_sealing_rejects_missing_and_duplicate_replacement_members(bool duplicate)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            string.Join(' ', Enumerable.Range(0, 200).Select(index => $"member_{index}")), publish: false, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var draft = await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        var candidate = await store.PrepareAsync(draft, CancellationToken.None);
        var options = UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot);
        var paths = new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        var built = await new UsearchSegmentBuilder(options, paths, new(MaximumVectors: 1)).BuildAsync(
            candidate.ModelFingerprint, candidate.Dimensions, store.ReadBuildVectorsAsync(candidate.Id, CancellationToken.None),
            CancellationToken.None, (segment, vectors, token) => store.SealSegmentAsync(candidate.Id, segment, vectors, token), candidate.Id);
        Assert.True(built.Count >= 2);
        var first = await db.IndexSegmentVectors.AsNoTracking().SingleAsync(value => value.SegmentId == built[0].Id);
        if (duplicate)
        {
            db.IndexSegmentVectors.Add(new() { SegmentId = built[1].Id, VectorId = first.VectorId });
            await db.SaveChangesAsync();
        }
        else await db.IndexSegmentVectors.Where(value => value.SegmentId == first.SegmentId && value.VectorId == first.VectorId).ExecuteDeleteAsync();
        var manifest = new UsearchManifestValidator(options, paths).Place(candidate.PublicationStamp,
            candidate.ModelFingerprint, candidate.Dimensions, built);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SealManifestAsync(candidate.Id, manifest, CancellationToken.None));
        Assert.Equal("index-manifest-replacement-membership-invalid", refusal.Message);
        Assert.Equal(0, await db.IndexPublicationCandidates.Where(value => value.Id == candidate.Id).Select(value => value.State).SingleAsync());
        Assert.False(await db.IndexGenerations.AnyAsync(value => value.Id == manifest.Generation.Id));
    }
}
