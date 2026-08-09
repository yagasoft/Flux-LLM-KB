using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Application.Sources;

public interface ISourceScanWakeSignal
{
    void Notify();
    ValueTask WaitAsync(CancellationToken cancellationToken);
}

public sealed class ChannelSourceScanWakeSignal : ISourceScanWakeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async ValueTask WaitAsync(CancellationToken cancellationToken) =>
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Local in-process wake loop; SQL claims are the sole scan authority.</summary>
public sealed class SourceReconciliationService(
    IServiceScopeFactory scopeFactory,
    ISourceScanWakeSignal wakeSignal,
    TimeProvider timeProvider,
    SourceWatchCoordinator? watchCoordinator = null) : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WatchCadence = TimeSpan.FromSeconds(2);
    private readonly string _leaseOwner = $"source-reconciliation:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PumpDueWatchBatchesAsync(stoppingToken).ConfigureAwait(false);
        await RunAvailableAsync(stoppingToken).ConfigureAwait(false);
        var nextReconciliationAtUtc = timeProvider.GetUtcNow().Add(DefaultCadence);
        using var timer = new PeriodicTimer(WatchCadence);
        while (!stoppingToken.IsCancellationRequested)
        {
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var timerTask = timer.WaitForNextTickAsync(waitCancellation.Token).AsTask();
            var wakeTask = wakeSignal.WaitAsync(waitCancellation.Token).AsTask();
            var completed = await Task.WhenAny(timerTask, wakeTask).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            await waitCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(timerTask, wakeTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The unselected wait was deliberately cancelled before the next loop.
            }
            var released = await PumpDueWatchBatchesAsync(stoppingToken).ConfigureAwait(false);
            if (released > 0 || timeProvider.GetUtcNow() >= nextReconciliationAtUtc)
            {
                await RunAvailableAsync(stoppingToken).ConfigureAwait(false);
                nextReconciliationAtUtc = timeProvider.GetUtcNow().Add(DefaultCadence);
            }
        }
    }

    public Task<int> PumpDueWatchBatchesAsync(CancellationToken cancellationToken) =>
        watchCoordinator is null
            ? Task.FromResult(0)
            : watchCoordinator.ReleaseDueAsync(timeProvider.GetUtcNow(), DefaultCadence, cancellationToken).AsTask();

    public async Task RunAvailableAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ISourceScanControlStore>();
            ClaimedSourceScan? claim;
            try
            {
                claim = await control.ClaimNextReleasedAsync(
                    _leaseOwner, timeProvider.GetUtcNow(), DefaultCadence, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (claim is null)
            {
                var restartStore = scope.ServiceProvider.GetService<ISourceActivityRestartStore>();
                if (restartStore is null)
                {
                    return;
                }

                var offered = await restartStore.OfferUnlinkedInProcessActivitiesAsync(cancellationToken).ConfigureAwait(false);
                if (offered > 0)
                {
                    scope.ServiceProvider.GetService<IOutboxWakeSignal>()?.Notify();
                    continue;
                }

                return;
            }

            SourceScanResult result;
            try
            {
                var scanner = scope.ServiceProvider.GetRequiredService<ISourceScanner>();
                result = await scanner.ScanAsync(claim.SourceRoot, claim.ScanRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var failed = new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0);
                try
                {
                    await control.CompleteAsync(claim, failed, Truncate(exception.GetType().Name), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The durable claim remains fenced for recovery; do not re-complete it.
                }

                continue;
            }

            try
            {
                await control.CompleteAsync(claim, result, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The durable claim remains fenced for recovery; do not reclassify successful work as failed.
            }
        }
    }

    private static string Truncate(string reason) => reason.Length <= 1024 ? reason : reason[..1024];
}
