using FluxKnowledge.Application.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// Rereads durable pending dispatches after process start, a local prompt, or a bounded
/// missed-prompt fallback. It never admits work, changes scheduler state, or derives
/// capacity from time; only an adapter's explicit lifecycle request can mutate durability.
/// </summary>
public sealed class GpuExecutorDispatchRecoveryService(
    IServiceScopeFactory scopeFactory,
    ChannelGpuExecutorDispatchSignal dispatchSignal,
    TimeProvider timeProvider,
    GpuSchedulerOptions options,
    IEnumerable<IGpuExecutorAdapter> adapters,
    ILogger<GpuExecutorDispatchRecoveryService>? logger = null) : BackgroundService
{
    private readonly ILogger<GpuExecutorDispatchRecoveryService> _logger =
        logger ?? NullLogger<GpuExecutorDispatchRecoveryService>.Instance;
    private readonly IReadOnlyDictionary<string, IGpuExecutorAdapter> _adapters = BuildAdapterMap(adapters);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await DeliverPendingDispatchesAsync(stoppingToken).ConfigureAwait(false);
                await WaitForPromptOrFallbackAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask DeliverPendingDispatchesAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<GpuExecutorBatchHandle> pending;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IGpuExecutorDispatchStore>();
            pending = await store.ReadPendingDispatchesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "GPU executor dispatch recovery could not read pending durable dispatches.");
            return;
        }

        foreach (var handle in pending)
        {
            if (!_adapters.TryGetValue(handle.ExecutorKey, out var adapter))
            {
                continue;
            }

            try
            {
                await DeliverWithinFallbackIntervalAsync(adapter, handle, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("GPU executor adapter delivery was cancelled; the durable dispatch remains pending.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "GPU executor adapter delivery failed; the durable dispatch remains pending.");
            }
        }
    }

    private async ValueTask DeliverWithinFallbackIntervalAsync(
        IGpuExecutorAdapter adapter,
        GpuExecutorBatchHandle handle,
        CancellationToken stoppingToken)
    {
        using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delivery = adapter.DeliverAsync(handle, deliveryCancellation.Token);
        if (delivery.IsCompleted)
        {
            await delivery.ConfigureAwait(false);
            return;
        }

        var deliveryTask = delivery.AsTask();
        var timeout = Task.Delay(options.FallbackInterval, timeProvider, deliveryCancellation.Token);
        var completed = await Task.WhenAny(deliveryTask, timeout).ConfigureAwait(false);
        if (completed == deliveryTask)
        {
            deliveryCancellation.Cancel();
            await deliveryTask.ConfigureAwait(false);
            return;
        }

        await timeout.ConfigureAwait(false);
        deliveryCancellation.Cancel();
        _ = ObserveLateDeliveryAsync(deliveryTask);
        _logger.LogWarning("GPU executor adapter delivery timed out; the durable dispatch remains pending.");
    }

    private async Task ObserveLateDeliveryAsync(Task delivery)
    {
        try
        {
            await delivery.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "GPU executor adapter completed after its local delivery timeout.");
        }
    }

    private async ValueTask WaitForPromptOrFallbackAsync(CancellationToken stoppingToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var prompt = dispatchSignal.WaitAsync(waitCancellation.Token).AsTask();
        var fallback = Task.Delay(options.FallbackInterval, timeProvider, waitCancellation.Token);
        var completed = await Task.WhenAny(prompt, fallback).ConfigureAwait(false);
        waitCancellation.Cancel();

        try
        {
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The unselected local wait was cancelled after the prompt or bounded fallback won.
        }

        try
        {
            await Task.WhenAll(prompt, fallback).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation of the losing local wait is expected and carries no durable meaning.
        }
    }

    private static IReadOnlyDictionary<string, IGpuExecutorAdapter> BuildAdapterMap(
        IEnumerable<IGpuExecutorAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var map = new Dictionary<string, IGpuExecutorAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (adapter is null || string.IsNullOrWhiteSpace(adapter.ExecutorKey) || map.ContainsKey(adapter.ExecutorKey))
            {
                continue;
            }

            map.Add(adapter.ExecutorKey, adapter);
        }

        return map;
    }
}
