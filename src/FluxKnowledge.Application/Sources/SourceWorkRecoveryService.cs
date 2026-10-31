using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Application.Sources;

public sealed class SourceWorkRecoveryService(IServiceScopeFactory scopes, ILogger<SourceWorkRecoveryService> logger,
    IDatabaseStartupReadiness? readiness = null, IDeploymentValidationHold? hold = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (readiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await (hold ?? DeploymentValidationHold.None).WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await (hold ?? DeploymentValidationHold.None).WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
                using var scope = scopes.CreateScope();
                var watcher = scope.ServiceProvider.GetService<SourceWatchCoordinator>();
                if (watcher is not null) await watcher.ReleaseDueAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                await scope.ServiceProvider.GetRequiredService<ISourceWorkRecovery>().AdvanceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Source work recovery remains pending; source admission stays fenced."); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
