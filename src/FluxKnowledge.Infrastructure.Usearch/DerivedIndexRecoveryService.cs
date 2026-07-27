using FluxKnowledge.Application.Indexing;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexRecoveryService(
    DerivedIndexRecoveryCoordinator coordinator,
    DerivedIndexRecoveryOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await coordinator.RunOnceAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = coordinator.Snapshot;
            if (snapshot.State == DerivedIndexRecoveryState.OperatorActionRequired)
            {
                await coordinator.WaitAsync(stoppingToken);
            }
            else
            {
                var delay = snapshot.State == DerivedIndexRecoveryState.RetryScheduled && snapshot.NextRetryAtUtc is { } retryAt
                    ? retryAt - timeProvider.GetUtcNow()
                    : options.ProbeInterval;
                await WaitForSignalOrDelayAsync(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, stoppingToken);
            }
            if (!stoppingToken.IsCancellationRequested) await coordinator.RunOnceAsync(stoppingToken);
        }
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = coordinator.WaitAsync(waitCancellation.Token).AsTask();
        var timer = Task.Delay(delay, timeProvider, waitCancellation.Token);
        await Task.WhenAny(signal, timer);
        await waitCancellation.CancelAsync();
        try { await Task.WhenAll(signal, timer); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false) { }
    }
}
