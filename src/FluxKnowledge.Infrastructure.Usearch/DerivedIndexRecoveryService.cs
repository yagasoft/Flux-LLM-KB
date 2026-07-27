using FluxKnowledge.Application.Indexing;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexRecoveryService : BackgroundService
{
    private readonly DerivedIndexRecoveryCoordinator _coordinator;
    private readonly DerivedIndexRecoveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task>? _deterministicWait;

    public DerivedIndexRecoveryService(
        DerivedIndexRecoveryCoordinator coordinator,
        DerivedIndexRecoveryOptions options,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? deterministicWait = null)
    {
        _coordinator = coordinator;
        _options = options;
        _timeProvider = timeProvider;
        _deterministicWait = deterministicWait;
    }

    public Task RunForTestingAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _coordinator.RunOnceAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = _coordinator.Snapshot;
            if (snapshot.State == DerivedIndexRecoveryState.OperatorActionRequired)
            {
                await _coordinator.WaitAsync(stoppingToken);
                continue;
            }
            if (snapshot.State == DerivedIndexRecoveryState.RetryScheduled && snapshot.NextRetryAtUtc is { } retryAt)
            {
                var retryDelay = retryAt - _timeProvider.GetUtcNow();
                if (retryDelay > TimeSpan.Zero)
                {
                    await WaitForRetryDueAsync(retryDelay, stoppingToken);
                    if (!stoppingToken.IsCancellationRequested) await _coordinator.RunOnceAsync(stoppingToken, retryDueWaited: true);
                    continue;
                }
            }
            await WaitForSignalOrDelayAsync(_options.ProbeInterval, stoppingToken);
            if (!stoppingToken.IsCancellationRequested) await _coordinator.RunOnceAsync(stoppingToken);
        }
    }

    private async Task WaitForRetryDueAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_deterministicWait is not null)
        {
            await _deterministicWait(delay, cancellationToken);
            return;
        }
        await Task.Delay(delay, _timeProvider, cancellationToken);
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_deterministicWait is not null)
        {
            await _deterministicWait(delay, cancellationToken);
            return;
        }
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _coordinator.WaitAsync(waitCancellation.Token).AsTask();
        var timer = Task.Delay(delay, _timeProvider, waitCancellation.Token);
        await Task.WhenAny(signal, timer);
        await waitCancellation.CancelAsync();
        try { await Task.WhenAll(signal, timer); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false) { }
    }
}
