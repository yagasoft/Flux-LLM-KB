using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OverviewProjectionTests
{
    [Fact]
    public async Task Overview_state_reloads_the_SQL_projection_after_a_status_event()
    {
        var reader = new FakeProjectionReader(
            CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(2, 2));
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "pipeline", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

    [Fact]
    public async Task Overview_state_reloads_after_an_index_recovery_status_event()
    {
        var reader = new FakeProjectionReader(
            CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(1, 2));
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "index-recovery", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

    private static OverviewProjection CreateOverview(int workerQueuedCount, int indexedRecordCount) => new(
        workerQueuedCount,
        0,
        0,
        0,
        0,
        0,
        indexedRecordCount,
        "generation-a",
        new IndexRecoverySummary("Healthy", "generation-a", null, null, null, 0));

    private sealed class FakeProjectionReader(OverviewProjection current) : IProjectionReader
    {
        public OverviewProjection Current { get; private set; } = current;

        public void Replace(OverviewProjection projection) => Current = projection;

        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Current);

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(null);
    }
}
