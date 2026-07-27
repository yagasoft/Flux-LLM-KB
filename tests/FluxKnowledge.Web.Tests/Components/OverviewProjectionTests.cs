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
            new OverviewProjection(1, 0, 0, 0, 0, 0, 1, "generation-a"));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(new OverviewProjection(2, 0, 0, 0, 0, 0, 2, "generation-a"));
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "pipeline", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

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
