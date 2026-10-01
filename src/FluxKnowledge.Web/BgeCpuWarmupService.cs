using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web;

/// <summary>Warms verified CPU models without delaying GPU search or strict index readiness.</summary>
internal sealed class BgeCpuWarmupService(ResidentCpuPassageInference pool, ILogger<BgeCpuWarmupService> logger) : IHostedService
{
    private Task? _observation;
    private Task? _disposalObservation;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _observation = ObserveAsync(pool.WarmAsync(CancellationToken.None));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var disposal = pool.DisposeAsync().AsTask();
        _disposalObservation ??= ObserveDisposalAsync(disposal);
        try
        {
            await disposal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_observation is not null) await _observation.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _disposalObservation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelling the host's wait cannot release sessions still executing native work.
            logger.LogWarning("Resident CPU passage shutdown deadline expired; native cleanup remains owned and observed until it settles.");
        }
    }

    private async Task ObserveDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
            logger.LogInformation("Resident CPU passage native cleanup completed.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Resident CPU passage native cleanup failed; uncertain native ownership remains retained.");
        }
    }

    private async Task ObserveAsync(Task warm)
    {
        try
        {
            await warm.ConfigureAwait(false);
            logger.LogInformation("Resident CPU passage search is ready with two lanes and four reranking shards per lane.");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Resident CPU passage search warmup stopped.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Resident CPU passage search is unavailable; GPU search remains available.");
        }
    }
}
