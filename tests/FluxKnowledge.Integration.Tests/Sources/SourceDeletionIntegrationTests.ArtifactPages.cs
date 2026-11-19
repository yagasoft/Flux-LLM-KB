using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artifact_pages_bound_text_bytes_preserve_principals_and_resume_after_acknowledgement_loss(bool loseAcknowledgement)
    {
        var graph = await SeedArtifactPageGraphAsync([(48, 0), (48, 0), (60, 0)]);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var first = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var firstStore = loseAcknowledgement
            ? new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString), TimeProvider.System)
            : store;
        Assert.Equal("purge-records", (await firstStore.PurgeAsync(first, null, default)).Phase);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[1..]);
        // Replaying a committed claim must not consume the next payload page.
        Assert.Equal("purge-records", (await store.PurgeAsync(first, null, default)).Phase);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[1..]);

        var resumed = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var second = Assert.IsType<SourceDeletionWorkItem>(await resumed.ClaimNextAsync(default));
        Assert.Equal("purge-records", (await resumed.PurgeAsync(second, null, default)).Phase);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[2..]);
        await CompleteArtifactGraphAsync(resumed, graph.Source, graph.Control, graph.Operation);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artifact_pages_count_metadata_bytes_and_allow_one_oversized_item_to_progress(bool oversized)
    {
        // A metadata-only 4 MiB artifact plus 62 MiB of text exceeds the page
        // budget. An individually oversized artifact must still make progress.
        var graph = await SeedArtifactPageGraphAsync(oversized ? [(70, 0), (1, 0)] : [(0, 4), (62, 0)]);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var first = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("purge-records", (await store.PurgeAsync(first, null, default)).Phase);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[1..]);
        await CompleteArtifactGraphAsync(store, graph.Source, graph.Control, graph.Operation);
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_artifact_delete_rolls_back_payload_and_principals()
    {
        var graph = await SeedArtifactPageGraphAsync([(48, 0), (48, 0)]);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        using var cancellation = new CancellationTokenSource();
        var cancel = new ArtifactPageCancellation(cancellation);
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(cancel).Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlSourceDeletionStore(factory, TimeProvider.System).PurgeAsync(work, null, cancellation.Token).AsTask());
        Assert.True(cancel.Fired);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds);
        Assert.Equal("purge-records", (await store.PurgeAsync(work, null, default)).Phase);
        await AssertArtifactPageAnchorsAsync(graph.Source, graph.ArtifactIds[1..]);
    }

    private async Task<(SeededRoot Source, SeededRoot Control, Guid Operation, Guid[] ArtifactIds)> SeedArtifactPageGraphAsync(
        (int TextMiB, int MetadataMiB)[] sizes)
    {
        const int mib = 1024 * 1024;
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("artifact-page-target", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("artifact-page-control", SourceRootState.Enabled, now);
        var operation = await AddDeletionAsync(source, now);
        await using var setup = CreateContext();
        setup.SourceArtifacts.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = source.RevisionId,
            StoreRelativePath = "synthetic-artifact-page", ContentSha256 = new string('a', 64),
            ByteLength = 4, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        setup.Artifacts.Add(new() { Id = Guid.NewGuid(), PipelineRecordId = control.RecordId, SourceRevision = 1,
            Stage = 1, ContentHash = new string('b', 64), ContentType = "text/plain", SearchText = "unrelated", CreatedAtUtc = now });
        await setup.SaveChangesAsync();
        await setup.SourceDeletionOperations.Where(value => value.Id == operation)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Phase, "purge-records"));
        var ids = Enumerable.Range(1, sizes.Length).Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:D12}")).ToArray();
        for (var index = 0; index < sizes.Length; index++)
        {
            var (textMiB, metadataMiB) = sizes[index];
            var metadata = metadataMiB == 0 ? null : "\"" + new string('m', metadataMiB * mib / 2 - 2) + "\"";
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT Artifacts(Id,PipelineRecordId,SourceRevision,Stage,ContentHash,ContentType,SearchText,DocumentMetadataJson,CreatedAtUtc,WindowEligibilityVersion)
                VALUES({ids[index]},{source.RecordId},1,{index + 1},{new string('a',64)},'text/plain',
                    REPLICATE(CONVERT(nvarchar(max),'x'),{textMiB * mib / 2}),{metadata},SYSUTCDATETIME(),NEWID());
                """);
        }
        return (source, control, operation, ids);
    }

    private async Task AssertArtifactPageAnchorsAsync(SeededRoot source, Guid[] remaining)
    {
        await using var check = CreateContext();
        var actual = await check.Artifacts.Where(value => value.PipelineRecordId == source.RecordId).OrderBy(value => value.Id)
            .Select(value => value.Id).ToArrayAsync();
        Assert.Equal(remaining, actual);
        Assert.True(await check.SourceRootConfigurations.AnyAsync(value => value.Id == source.RootId));
        Assert.True(await check.SourceRevisions.AnyAsync(value => value.Id == source.RevisionId));
        Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id == source.RecordId));
        Assert.True(await check.Jobs.AnyAsync(value => value.Id == source.JobId));
        Assert.True(await check.SourceArtifacts.AnyAsync(value => value.SourceRevisionId == source.RevisionId));
    }

    private async Task CompleteArtifactGraphAsync(SqlSourceDeletionStore store, SeededRoot source, SeededRoot control, Guid operation,
        Action<double>? unitElapsed = null)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await store.PurgeAsync(work, null, default);
            unitElapsed?.Invoke(watch.Elapsed.TotalMilliseconds);
            if (result.Completed) break;
        }
        await using var check = CreateContext();
        Assert.Equal(3, (await check.SourceDeletionOperations.SingleAsync(value => value.Id == operation)).State);
        Assert.False(await check.SourceRootConfigurations.AnyAsync(value => value.Id == source.RootId));
        Assert.False(await check.SourceRevisions.AnyAsync(value => value.Id == source.RevisionId));
        Assert.False(await check.PipelineRecords.AnyAsync(value => value.Id == source.RecordId));
        Assert.False(await check.Jobs.AnyAsync(value => value.Id == source.JobId));
        Assert.False(await check.SourceArtifacts.AnyAsync(value => value.SourceRevisionId == source.RevisionId));
        Assert.Equal(control.RecordId, (await check.Artifacts.SingleAsync()).PipelineRecordId);
        Assert.True(await check.Jobs.AnyAsync(value => value.Id == control.JobId));
    }

    private sealed class ArtifactPageCancellation(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("source-delete-artifact-page-delete", StringComparison.Ordinal))
            { Fired = true; cancellation.Cancel(); }
            return ValueTask.FromResult(result);
        }
    }
}
