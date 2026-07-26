using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class OutboxPumpService(
    IServiceScopeFactory scopeFactory,
    ChannelOutboxWakeSignal wakeSignal,
    TimeProvider timeProvider,
    ILogger<OutboxPumpService>? logger = null) : BackgroundService, IOutboxPump
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(60);
    private readonly string _leaseOwner = $"in-process-outbox:{Guid.NewGuid():N}";
    private readonly ILogger<OutboxPumpService> _logger =
        logger ?? NullLogger<OutboxPumpService>.Instance;
    public async ValueTask<int> PumpOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registration = scope.ServiceProvider
            .GetRequiredService<OutboxWorkerRegistration>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobClaimStore>();
        var transitions = scope.ServiceProvider.GetRequiredService<StageTransitionService>();
        var processed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var dispatch = await outbox.ClaimNextDueAsync(
                    _leaseOwner,
                    now,
                    LeaseDuration,
                    registration.Operations,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dispatch is null)
            {
                break;
            }

            var job = await jobs.ClaimForDispatchAsync(
                    dispatch,
                    _leaseOwner,
                    now,
                    LeaseDuration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (job is null)
            {
                await outbox.ReleaseAsync(
                        dispatch,
                        now.AddSeconds(5),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            var worker = registration.Find(dispatch.Operation);
            if (worker is null)
            {
                await transitions.FailAsync(
                        new StageFailureRequest(
                            dispatch,
                            job,
                            $"no registered stage worker for operation '{dispatch.Operation}'",
                            null,
                            nameof(OutboxPumpService)),
                        cancellationToken)
                    .ConfigureAwait(false);
                processed++;
                continue;
            }

            try
            {
                await worker.ExecuteAsync(
                        new StageWorkItem(dispatch, job),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await transitions.FailAsync(
                        new StageFailureRequest(
                            dispatch,
                            job,
                            "stage worker failed non-retryably",
                            exception.Message,
                            nameof(OutboxPumpService)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            processed++;
        }

        return processed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PumpOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "The SQL outbox pump iteration failed; the hosted loop will continue.");
                }

                await WaitForWakeOrFallbackAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForWakeOrFallbackAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wakeTask = wakeSignal.WaitAsync(waitCancellation.Token).AsTask();
        var fallbackTask = Task.Delay(
            FallbackInterval,
            timeProvider,
            waitCancellation.Token);
        _ = await Task.WhenAny(wakeTask, fallbackTask).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(wakeTask, fallbackTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
    }
}
