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
    public static IServiceCollection AddFluxKnowledgeGpuScheduler(
        this IServiceCollection services,
        NativeWorkerOptions? nativeWorkerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(GpuSchedulerOptions.Default);
        services.TryAddSingleton<ChannelGpuSchedulerWakeSignal>();
        services.TryAddSingleton<IGpuSchedulerWakeSignal>(provider => provider.GetRequiredService<ChannelGpuSchedulerWakeSignal>());
        services.TryAddSingleton<ChannelGpuExecutorDispatchSignal>();
        services.TryAddSingleton<IGpuExecutorDispatchSignal>(provider => provider.GetRequiredService<ChannelGpuExecutorDispatchSignal>());
        services.TryAddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.TryAddSingleton<IStatusEventPublisher, NullStatusEventPublisher>();
        services.TryAddScoped<SqlGpuSchedulerStore>(provider => new SqlGpuSchedulerStore(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<IGpuSchedulerStore>(provider => provider.GetRequiredService<SqlGpuSchedulerStore>());
        services.TryAddScoped<IGpuExecutorDispatchStore>(provider => provider.GetRequiredService<SqlGpuSchedulerStore>());
        services.TryAddScoped<SqlNativeWorkerInstanceStore>(provider => new SqlNativeWorkerInstanceStore(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<INativeWorkerInstanceStore>(provider => provider.GetRequiredService<SqlNativeWorkerInstanceStore>());
        services.TryAddScoped<GpuSchedulerCoordinator>();
        services.TryAddScoped<GpuExecutorLifecycleCoordinator>();
        services.TryAddScoped<IGpuExecutorLifecycleSink>(provider => provider.GetRequiredService<GpuExecutorLifecycleCoordinator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GpuSchedulerService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GpuExecutorDispatchRecoveryService>());
        var resolvedNativeWorkerOptions = nativeWorkerOptions ?? new NativeWorkerOptions();
        resolvedNativeWorkerOptions.Validate();
        services.TryAddSingleton(resolvedNativeWorkerOptions);
        if (resolvedNativeWorkerOptions.Enabled)
        {
            services.TryAddSingleton<INativeWorkerProcessLauncher, NativeWorkerProcessLauncher>();
            services.TryAddSingleton<NativeWorkerSupervisorService>(provider => new NativeWorkerSupervisorService(
                provider.GetRequiredService<NativeWorkerOptions>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<INativeWorkerProcessLauncher>(),
                provider.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<NativeWorkerExecutorAdapter>();
            services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<NativeWorkerSupervisorService>());
            services.AddSingleton<IGpuExecutorAdapter>(provider =>
                provider.GetRequiredService<NativeWorkerExecutorAdapter>());
        }
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
