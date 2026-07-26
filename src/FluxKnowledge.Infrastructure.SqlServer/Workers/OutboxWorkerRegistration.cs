using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class OutboxWorkerRegistration
{
    private readonly IReadOnlyDictionary<string, IStageWorker> _workers;
    private readonly IReadOnlyCollection<string> _operations;

    public OutboxWorkerRegistration(IEnumerable<IStageWorker> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        var registered = new Dictionary<string, IStageWorker>(StringComparer.Ordinal);
        foreach (var worker in workers)
        {
            if (!registered.TryAdd(worker.Operation, worker))
            {
                throw new InvalidOperationException(
                    $"More than one stage worker is registered for '{worker.Operation}'.");
            }
        }

        _workers = registered;
        _operations = registered.Keys.ToArray();
    }

    public IReadOnlyCollection<string> Operations => _operations;

    public IStageWorker? Find(string operation) =>
        _workers.TryGetValue(operation, out var worker) ? worker : null;
}

public static class OutboxWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeOutboxWorkers(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ChannelOutboxWakeSignal>();
        services.TryAddSingleton<IOutboxWakeSignal>(
            provider => provider.GetRequiredService<ChannelOutboxWakeSignal>());
        services.TryAddSingleton<IStatusEventPublisher, NullStatusEventPublisher>();
        services.TryAddScoped<SqlPipelineStore>(
            provider => new SqlPipelineStore(
                provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
                provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<IRegistrationStore>(
            provider => provider.GetRequiredService<SqlPipelineStore>());
        services.TryAddScoped<IPipelineStageReader>(
            provider => provider.GetRequiredService<SqlPipelineStore>());
        services.TryAddScoped<SqlJobClaimStore>();
        services.TryAddScoped<IJobClaimStore>(
            provider => provider.GetRequiredService<SqlJobClaimStore>());
        services.TryAddScoped<SqlOutboxStore>();
        services.TryAddScoped<IOutboxStore>(
            provider => provider.GetRequiredService<SqlOutboxStore>());
        services.TryAddScoped<IStageTransitionStore>(
            provider => new SqlStageTransitionStore(
                provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
                provider.GetService<IStageTransitionFailureInjector>(),
                provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<StageTransitionService>();
        services.TryAddScoped<RegisterUtf8FileHandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IStageWorker, ExtractUtf8StageWorker>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IStageWorker, NormaliseTextStageWorker>());
        services.TryAddScoped<OutboxWorkerRegistration>();
        services.TryAddSingleton<OutboxPumpService>();
        services.TryAddSingleton<IOutboxPump>(
            provider => provider.GetRequiredService<OutboxPumpService>());
        services.AddSingleton<IHostedService>(
            provider => provider.GetRequiredService<OutboxPumpService>());
        return services;
    }

    private sealed class NullStatusEventPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(
            StatusChanged statusChanged,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
