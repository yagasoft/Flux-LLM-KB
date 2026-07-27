using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OverviewProjectionTests
{
    [Fact]
    public async Task Overview_initialization_buffers_an_index_recovery_event_published_during_the_first_projection_read()
    {
        var initialRecovery = new IndexRecoverySummary(
            "RetryScheduled",
            "generation-a",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T08:00:05Z"),
            "TransientIo",
            3);
        var recovered = new IndexRecoverySummary(
            "Healthy",
            "generation-b",
            DateTimeOffset.Parse("2026-07-27T08:01:00Z"),
            null,
            null,
            4);
        var reader = new FirstReadBlockingProjectionReader(CreateOverview(1, 1, initialRecovery));
        var state = new OverviewProjectionState(reader);
        var feed = new StatusEventFeed();

        var initialize = state.SubscribeAndReloadAsync(feed, CancellationToken.None).AsTask();
        await reader.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await feed.PublishAsync(
            new StatusChanged(null, "index-recovery", DateTimeOffset.UtcNow),
            CancellationToken.None);
        reader.Replace(CreateOverview(1, 2, recovered));
        reader.CompleteFirstRead();

        await using var subscription = await initialize;
        var statusChanged = await subscription.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await state.HandleStatusChangedAsync(statusChanged, CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
        Assert.Equal("Healthy", state.Current.IndexRecovery.State);
        Assert.Equal("generation-b", state.Current.IndexRecovery.ActiveGeneration);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T08:01:00Z"), state.Current.IndexRecovery.LastCompletedAtUtc);
        Assert.Equal(4, state.Current.IndexRecovery.CleanedCandidateCount);
    }

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

    private static OverviewProjection CreateOverview(
        int workerQueuedCount,
        int indexedRecordCount,
        IndexRecoverySummary? recovery = null) => new(
        workerQueuedCount,
        0,
        0,
        0,
        0,
        0,
        indexedRecordCount,
        "generation-a",
        recovery ?? new IndexRecoverySummary("Healthy", "generation-a", null, null, null, 0));

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

    private sealed class FirstReadBlockingProjectionReader(OverviewProjection current) : IProjectionReader
    {
        private readonly TaskCompletionSource _firstReadMayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OverviewProjection Current { get; private set; } = current;

        public void Replace(OverviewProjection projection) => Current = projection;

        public void CompleteFirstRead() => _firstReadMayComplete.SetResult();

        public async ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
        {
            var snapshot = Current;
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstReadStarted.SetResult();
                await _firstReadMayComplete.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(null);
    }
}
