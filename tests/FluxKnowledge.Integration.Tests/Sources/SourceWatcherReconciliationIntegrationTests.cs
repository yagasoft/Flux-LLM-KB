using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceWatcherReconciliationIntegrationTests
{
    [Fact]
    public async Task Record_wakes_only_after_the_durable_store_completes()
    {
        var wake = new RecordingWakeSignal();
        var coordinator = new SourceWatchCoordinator(new CompletedStore(), wake);

        await coordinator.RecordAsync(new SourceWatchSignal(SourceRootId.New(), SourceWatchSignalKind.Overflow, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(1, wake.Count);
    }

    [Fact]
    public async Task Failed_record_does_not_wake()
    {
        var wake = new RecordingWakeSignal();
        var coordinator = new SourceWatchCoordinator(new FailingStore(), wake);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecordAsync(new SourceWatchSignal(SourceRootId.New(), SourceWatchSignalKind.Changed, DateTimeOffset.UtcNow), CancellationToken.None).AsTask());

        Assert.Equal(0, wake.Count);
    }

    private sealed class RecordingWakeSignal : ISourceScanWakeSignal
    {
        public int Count { get; private set; }
        public void Notify() => Count++;
        public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CompletedStore : FluxKnowledge.Application.Ports.ISourceRootWatchStore
    {
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>([]);
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<FluxKnowledge.Application.Ports.ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset nowUtc, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) => ValueTask.FromResult<FluxKnowledge.Application.Ports.ClaimedSourceWatchBatch?>(null);
        public ValueTask ReleaseScanAsync(FluxKnowledge.Application.Ports.ClaimedSourceWatchBatch batch, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FailingStore : FluxKnowledge.Application.Ports.ISourceRootWatchStore
    {
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>([]);
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken) => ValueTask.FromException(new InvalidOperationException("not committed"));
        public ValueTask<FluxKnowledge.Application.Ports.ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset nowUtc, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) => ValueTask.FromResult<FluxKnowledge.Application.Ports.ClaimedSourceWatchBatch?>(null);
        public ValueTask ReleaseScanAsync(FluxKnowledge.Application.Ports.ClaimedSourceWatchBatch batch, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
