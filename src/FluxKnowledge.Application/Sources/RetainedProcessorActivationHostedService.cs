using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Application.Sources;

/// <summary>Local automatic retained-processor replay; disabled options remain completely inert.</summary>
public sealed class RetainedProcessorActivationHostedService(
    IServiceScopeFactory scopeFactory,
    IDeploymentValidationHold? deploymentValidationHold = null,
    IRetainedProcessorFailureClassifier? failureClassifier = null,
    ILogger<RetainedProcessorActivationHostedService>? logger = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    // The policy is optional for application-only compositions; without it failures retain StopHost behaviour.
    private readonly IRetainedProcessorFailureClassifier? _failureClassifier = failureClassifier;
    private readonly ILogger<RetainedProcessorActivationHostedService>? _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await (deploymentValidationHold ?? DeploymentValidationHold.None)
                    .WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
                var delay = TimeSpan.FromSeconds(30);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<RetainedProcessorActivationService>()
                        .RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    if (result.ClaimedBranches + result.PromotedBranches > 0) delay = TimeSpan.Zero;
                }
                catch (Exception exception) when (_failureClassifier?.TryClassify(exception, out _) == true)
                {
                    _failureClassifier.TryClassify(exception, out var errorNumber);
                    _logger?.LogWarning("Retained processor iteration deferred after SQL error {SqlErrorNumber} ({ExceptionType}); a fresh iteration will revalidate durable work.",
                        errorNumber, exception.GetType().Name);
                }
                // The scope and any failed transaction are disposed before delaying/re-entering.
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
