using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Segmented_small_merge_rewrites_at_most_four_small_segments_and_reuses_the_large_control()
    {
        var controlText = string.Join(' ', Enumerable.Range(0, 350).Select(index => $"word_{index:000}"));
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, controlText, segmented: true, segmentMaximumVectors: 8);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var control = Assert.Single(await db.IndexSegments.AsNoTracking().ToArrayAsync());
        Assert.InRange(control.VectorCount, 3, 8);
        var controlBytes = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(control.DirectoryPath, "index.usearch")));
        var controlMembers = await db.IndexSegmentVectors.Where(value => value.SegmentId == control.Id)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync();
        for (var index = 0; index < 4; index++) await environment.AddAndPumpAsync($"Small source {index} remains eligible.");
        var before = await environment.ActiveGenerationAsync();
        Assert.Equal(5, before.SegmentCount);
        var oldIds = await db.IndexGenerationSegments.Where(value => value.GenerationId == before.Id).Select(value => value.SegmentId).ToArrayAsync();
        var cache = environment.Services.GetRequiredService<UsearchSegmentCache>();
        Guid controlInstance;
        await using (var borrowed = await cache.BorrowAsync(SqlSegmentedIndexStore.Describe(control), CancellationToken.None))
            controlInstance = borrowed.NativeInstanceId;
        using var scope = environment.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>();
        var candidate = await publisher.BuildAndPlaceAsync(Guid.Empty, CancellationToken.None);
        var stored = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == candidate.SegmentedCandidateId);
        Assert.Equal(4, JsonSerializer.Deserialize<string[]>(stored.RepackedSegmentsJson)!.Length);
        await environment.AddAndPumpAsync("The next publication coalesces bounded small segments.");
        var after = await environment.ActiveGenerationAsync();
        Assert.Equal(2, after.SegmentCount);
        var segments = await (from member in db.IndexGenerationSegments join segment in db.IndexSegments on member.SegmentId equals segment.Id
                              where member.GenerationId == after.Id select segment).AsNoTracking().ToArrayAsync();
        Assert.Contains(segments, value => value.Id == control.Id);
        var replacement = Assert.Single(segments, value => value.Id != control.Id);
        Assert.Equal(5, replacement.VectorCount);
        Assert.DoesNotContain(replacement.Id, oldIds);
        Assert.Equal(controlBytes, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(control.DirectoryPath, "index.usearch"))));
        Assert.Equal(controlMembers, await db.IndexSegmentVectors.Where(value => value.SegmentId == control.Id)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
        Assert.Equal(before.VectorCount + 1, after.VectorCount);
        await using (var borrowed = await cache.BorrowAsync(SqlSegmentedIndexStore.Describe(control), CancellationToken.None))
            Assert.Equal(controlInstance, borrowed.NativeInstanceId);
        await AssertSegmentedOracleAsync(environment);

        // The competing publication has made this actual four-segment merge stale.
        Assert.False(await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().TryActivateRefreshAsync(candidate, CancellationToken.None));
        await publisher.AbandonAsync(candidate, CancellationToken.None);
        Assert.Equal(3, (await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == stored.Id)).State);
        await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Segmented_small_merge_obeys_raw_byte_budget_even_when_vector_count_would_fit()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The first small source.", segmented: true,
            segmentMaximumVectors: 8, segmentMaximumPayloadBytes: 3L * DeterministicTokenHashEmbeddingProvider.Dimensions * sizeof(float));
        for (var index = 0; index < 5; index++) await environment.AddAndPumpAsync($"Another small source {index}.");
        Assert.Equal(6, (await environment.ActiveGenerationAsync()).SegmentCount);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.False(await db.IndexPublicationCandidates.AnyAsync(value => value.RepackedSegmentsJson != "[]"));
        await AssertSegmentedOracleAsync(environment);
    }
}
