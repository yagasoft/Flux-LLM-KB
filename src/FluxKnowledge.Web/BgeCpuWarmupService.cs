using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web;

/// <summary>Warms verified CPU models without delaying GPU search or strict index readiness.</summary>
internal sealed class BgeCpuWarmupService(ResidentCpuPassageInference pool, ILogger<BgeCpuWarmupService> logger) : IHostedService
{
    private Task? _observation;
    private Task? _disposalObservation;
    private readonly Guid _warmupId = Guid.NewGuid();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Resident CPU passage warmup starting. Process {ProcessId}, warmup {WarmupId}.",
            Environment.ProcessId, _warmupId);
        _observation = ObserveAsync(pool.WarmAsync(CancellationToken.None));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Resident CPU passage shutdown starting with {ActiveSearchCount} active searches. Process {ProcessId}, warmup {WarmupId}.",
            pool.ActiveSearchCount, Environment.ProcessId, _warmupId);
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
            logger.LogWarning("Resident CPU passage shutdown deadline expired; native cleanup remains owned and observed until it settles. Process {ProcessId}, warmup {WarmupId}, active searches {ActiveSearchCount}.",
                Environment.ProcessId, _warmupId, pool.ActiveSearchCount);
        }
    }

    private async Task ObserveDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
            logger.LogInformation("Resident CPU passage native cleanup completed. Process {ProcessId}, warmup {WarmupId}.",
                Environment.ProcessId, _warmupId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Resident CPU passage shutdown failed; native cleanup outcome requires review. Process {ProcessId}, warmup {WarmupId}.",
                Environment.ProcessId, _warmupId);
        }
    }

    private async Task ObserveAsync(Task warm)
    {
        try
        {
            await warm.ConfigureAwait(false);
            logger.LogInformation("Resident CPU passage search is ready with two lanes and four reranking shards per lane. Process {ProcessId}, warmup {WarmupId}.",
                Environment.ProcessId, _warmupId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Resident CPU passage search warmup stopped. Process {ProcessId}, warmup {WarmupId}.",
                Environment.ProcessId, _warmupId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Resident CPU passage search is unavailable. Process {ProcessId}, warmup {WarmupId}.",
                Environment.ProcessId, _warmupId);
        }
    }
}
