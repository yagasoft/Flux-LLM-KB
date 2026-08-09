using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceWatchCoordinatorTests
{
    [Fact]
    public void Overflow_is_due_now_without_trusting_a_path()
    {
        var state = SourceWatchState.Empty(new SourceRootId(Guid.NewGuid()));

        var next = state.Observe(
            new SourceWatchSignal(state.SourceRootId, SourceWatchSignalKind.Overflow, DateTimeOffset.UnixEpoch),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        Assert.Equal(DateTimeOffset.UnixEpoch, next.DueAtUtc);
    }

    [Fact]
    public void Overflow_remains_due_when_a_later_normal_signal_arrives_before_claim()
    {
        var state = SourceWatchState.Empty(new SourceRootId(Guid.NewGuid()));
        var overflow = state.Observe(
            new SourceWatchSignal(state.SourceRootId, SourceWatchSignalKind.Overflow, DateTimeOffset.UnixEpoch),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        var next = overflow.Observe(
            new SourceWatchSignal(state.SourceRootId, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(1)),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        Assert.Equal(DateTimeOffset.UnixEpoch, next.DueAtUtc);
    }

    [Fact]
    public void Signal_count_saturates_when_a_noisy_root_is_already_at_the_limit()
    {
        var rootId = new SourceRootId(Guid.NewGuid());
        var state = new SourceWatchState(rootId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            int.MaxValue, 1, DateTimeOffset.UnixEpoch.AddSeconds(2));

        var next = state.Observe(
            new SourceWatchSignal(rootId, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(1)),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        Assert.Equal(int.MaxValue, next.SignalCount);
    }

    [Fact]
    public void Signals_coalesce_until_the_quiet_period_or_maximum_delay()
    {
        var state = SourceWatchState.Empty(new SourceRootId(Guid.NewGuid()));
        var first = state.Observe(
            new SourceWatchSignal(state.SourceRootId, SourceWatchSignalKind.Created, DateTimeOffset.UnixEpoch),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));
        var next = first.Observe(
            new SourceWatchSignal(state.SourceRootId, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(29)),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), next.DueAtUtc);
        Assert.Equal(2, next.SignalCount);
        Assert.Equal(2, next.DebounceGeneration);
    }

    [Fact]
    public async Task Coordinator_records_only_the_root_signal_contract()
    {
        var store = new RecordingStore();
        var coordinator = new SourceWatchCoordinator(store);
        var signal = new SourceWatchSignal(SourceRootId.New(), SourceWatchSignalKind.Renamed, DateTimeOffset.UnixEpoch);

        await coordinator.RecordAsync(signal, CancellationToken.None);

        Assert.Same(signal, store.Signal);
    }

    private sealed class RecordingStore : ISourceRootWatchStore
    {
        public SourceWatchSignal? Signal { get; private set; }

        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>([]);

        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken)
        {
            Signal = signal;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset nowUtc, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
