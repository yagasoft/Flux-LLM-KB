using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public static class GpuSchedulerServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeGpuScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(GpuSchedulerOptions.Default);
        services.TryAddSingleton<ChannelGpuSchedulerWakeSignal>();
        services.TryAddSingleton<IGpuSchedulerWakeSignal>(provider => provider.GetRequiredService<ChannelGpuSchedulerWakeSignal>());
        services.TryAddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.TryAddSingleton<IStatusEventPublisher, NullStatusEventPublisher>();
        services.TryAddScoped<SqlGpuSchedulerStore>(provider => new SqlGpuSchedulerStore(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<IGpuSchedulerStore>(provider => provider.GetRequiredService<SqlGpuSchedulerStore>());
        services.TryAddScoped<GpuSchedulerCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GpuSchedulerService>());
        return services;
    }

    private sealed class NullStatusEventPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
