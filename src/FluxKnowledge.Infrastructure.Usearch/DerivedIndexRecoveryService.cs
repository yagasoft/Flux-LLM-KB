using FluxKnowledge.Application.Indexing;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexRecoveryService(DerivedIndexRecoveryCoordinator coordinator, DerivedIndexRecoveryOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => coordinator.RunOnceAsync(stoppingToken).AsTask(), stoppingToken);
        using var timer = new PeriodicTimer(options.ProbeInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var signal = coordinator.WaitAsync(stoppingToken).AsTask();
            var probe = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            await Task.WhenAny(signal, probe);
            if (!stoppingToken.IsCancellationRequested) await coordinator.RunOnceAsync(stoppingToken);
        }
    }
}
