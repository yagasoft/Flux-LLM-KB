using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceScanControlServiceTests
{
    [Fact]
    public async Task Release_wakes_after_a_successful_idempotent_store_result()
    {
        var store = new ReturningStore(released: false);
        var signal = new RecordingWakeSignal();
        var service = new SourceScanControlService(store, signal);

        var released = await service.ReleaseAsync(
            SourceRootId.New(),
            SourceScanRequestId.New(),
            "operator",
            CancellationToken.None);

        Assert.False(released);
        Assert.Equal(1, signal.NotificationCount);
    }

    private sealed class ReturningStore(bool released) : ISourceRootStore
    {
        public ValueTask<SourceRootReceipt> CreateAsync(
            SourceRootCreateRequest request,
            ScanStartIntent startIntent,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> ReleaseAsync(
            SourceRootId sourceRootId,
            SourceScanRequestId sourceScanRequestId,
            string actor,
            CancellationToken cancellationToken) => ValueTask.FromResult(released);
    }

    private sealed class RecordingWakeSignal : IOutboxWakeSignal
    {
        public int NotificationCount { get; private set; }

        public void Notify() => NotificationCount++;

        public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
