using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web;

/// <summary>Warms local CPU models without delaying GPU search or strict index readiness.</summary>
internal sealed class BgeCpuWarmupService(ResidentCpuPassageInference pool, ILogger<BgeCpuWarmupService> logger,
    TimeProvider? timeProvider = null) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _observation;
    private Task? _disposalObservation;
    private readonly Guid _warmupId = Guid.NewGuid();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Resident CPU passage warmup starting. Process {ProcessId}, warmup {WarmupId}.",
            Environment.ProcessId, _warmupId);
        _observation = ObserveAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
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

    public void Dispose() => _stopping.Dispose();

    private async Task ObserveAsync()
    {
        try
        {
            while (true)
            {
                _stopping.Token.ThrowIfCancellationRequested();
                try
                {
                    await pool.WarmAsync(_stopping.Token).ConfigureAwait(false);
                    _stopping.Token.ThrowIfCancellationRequested();
                    logger.LogInformation("Resident CPU passage search is ready with two bounded lanes. Process {ProcessId}, warmup {WarmupId}.",
                        Environment.ProcessId, _warmupId);
                    return;
                }
                catch (CpuWarmupMemoryUnavailableException)
                {
                    logger.LogWarning("Resident CPU passage warmup is waiting for memory before loading models; retrying in 60 seconds. Process {ProcessId}, warmup {WarmupId}.",
                        Environment.ProcessId, _warmupId);
                    await Task.Delay(TimeSpan.FromMinutes(1), timeProvider ?? TimeProvider.System, _stopping.Token).ConfigureAwait(false);
                }
            }
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
