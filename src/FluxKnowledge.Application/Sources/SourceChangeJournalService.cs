using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Application.Sources;

public sealed class SourceChangeJournalService(IServiceScopeFactory scopes, ISourceScanWakeSignal wake,
    SourceJournalOptions options, ILogger<SourceChangeJournalService> logger,
    IDatabaseStartupReadiness? readiness = null, IDeploymentValidationHold? hold = null,
    ISourceJournalWakeSignal? journalWake = null, TimeProvider? timeProvider = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        await (readiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await (hold ?? DeploymentValidationHold.None).WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
        var clock = timeProvider ?? TimeProvider.System;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds), clock);
        var intakeWake = journalWake ?? new ChannelSourceJournalWakeSignal();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<ISourceIncrementalCoordinator>().PumpAsync(stoppingToken).ConfigureAwait(false) > 0)
                        wake.Notify();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception e)
                { logger.LogWarning("Source journal catch-up did not confirm completion ({FailureType}); durable cursors remain fenced.", e.GetType().Name); }

                try
                {
                    using var scope = scopes.CreateScope();
                    var verification = scope.ServiceProvider.GetService<ISourceVerificationScheduler>();
                    if (verification is not null && await verification.PumpAsync(stoppingToken).ConfigureAwait(false) > 0)
                        wake.Notify();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception e)
                { logger.LogWarning("Source verification scheduling did not confirm completion ({FailureType}); durable work remains pending.", e.GetType().Name); }

                // Even a continuous watcher storm cannot turn the bounded RPC into a spin loop.
                // The independent periodic timer is not postponed by successful hints.
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await SourceReconciliationService.WaitForTimerOrWakeAsync(
                    timer.WaitForNextTickAsync(waitCancellation.Token).AsTask(),
                    intakeWake.WaitAsync(waitCancellation.Token).AsTask(), waitCancellation, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
