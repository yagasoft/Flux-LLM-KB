using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Application.Sources;

/// <summary>Local automatic retained-processor replay; disabled options remain completely inert.</summary>
public sealed class RetainedProcessorActivationHostedService(
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<RetainedProcessorActivationService>()
                .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(result.ClaimedBranches + result.PromotedBranches > 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(30), stoppingToken)
                .ConfigureAwait(false);
        }
    }
}
