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
}
