using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web;

/// <summary>Warms verified CPU models without delaying GPU search or strict index readiness.</summary>
internal sealed class BgeCpuWarmupService(ResidentCpuPassageInference pool, ILogger<BgeCpuWarmupService> logger) : IHostedService
{
    private Task? _observation;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _observation = ObserveAsync(pool.WarmAsync(CancellationToken.None));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await pool.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_observation is not null) await _observation.ConfigureAwait(false);
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
