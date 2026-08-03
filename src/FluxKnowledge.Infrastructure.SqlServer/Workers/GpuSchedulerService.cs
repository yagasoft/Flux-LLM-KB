using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// A local, signal-driven reader of SQL scheduler state. It never owns execution,
/// capacity release, or recovery of active work.
/// </summary>
public sealed class GpuSchedulerService(
    IServiceScopeFactory scopeFactory,
    IGpuSchedulerWakeSignal wakeSignal,
    TimeProvider timeProvider,
    GpuSchedulerOptions options,
    ILogger<GpuSchedulerService>? logger = null) : BackgroundService
{
    private readonly ILogger<GpuSchedulerService> _logger = logger ?? NullLogger<GpuSchedulerService>.Instance;
    private long? _observedWakeGeneration;
    private PendingWakeConsumptionRequest? _pendingWakeConsumption;
    private PendingCapacityUncertaintyTransition? _pendingCapacityUncertaintyTransition;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var startup = true;
            var pendingAdmissionReason = (GpuSchedulerWakeReason)0;
            Guid? pendingAdmissionOperationId = null;
            var nextDiagnosticAtUtc = timeProvider.GetUtcNow();
            while (!stoppingToken.IsCancellationRequested)
            {
                var wake = new GpuSchedulerWakeSnapshot(0, 0, null);
                try
                {
                    var now = timeProvider.GetUtcNow();
                    if (_pendingCapacityUncertaintyTransition is not null || now >= nextDiagnosticAtUtc)
                    {
                        // Retain the pending operation ID until both the durable transition and its status
                        // publication complete. A failed diagnostic retries on the normal safe fallback.
                        nextDiagnosticAtUtc = now.Add(options.FallbackInterval);
                        await RunStaleReservationDiagnosticAsync(
                                now - options.UnresponsiveDiagnosticAge,
                                stoppingToken)
                            .ConfigureAwait(false);
                        nextDiagnosticAtUtc = timeProvider.GetUtcNow().Add(options.UnresponsiveDiagnosticAge);
                    }

                    var wakeWasConsumed = _pendingWakeConsumption is not null;
                    if (wakeWasConsumed)
                    {
                        wake = (await ResolvePendingWakeConsumptionAsync(stoppingToken).ConfigureAwait(false)).Snapshot;
                    }
                    else
                    {
                        wake = await ReadWakeStateAsync(stoppingToken).ConfigureAwait(false);
                        wakeWasConsumed = wake.ConsumptionOperationId is not null;
                    }

                    now = timeProvider.GetUtcNow();
                    var reason = (GpuSchedulerWakeReason)0;
                    if (pendingAdmissionReason != 0)
                    {
                        reason = pendingAdmissionReason;
                    }
                    else if (startup)
                    {
                        if (wakeWasConsumed)
                        {
                            _observedWakeGeneration = wake.Generation;
                            if (wake.Reasons != 0)
                            {
                                reason = AdmissionReasonsForConsumedWake(wake, now);
                                if (reason == 0)
                                {
                                    startup = false;
                                }
                            }
                            else
                            {
                                reason = GpuSchedulerWakeReason.StartupRecovery;
                            }
                        }
                        else if (CanAttemptAdmission(wake, now))
                        {
                            var consumption = await ConsumeLatestWakeAsync(
                                    wake.Generation,
                                    stoppingToken)
                                .ConfigureAwait(false);
                            wake = consumption.Snapshot;
                            _observedWakeGeneration = wake.Generation;
                            if (wake.Reasons != 0)
                            {
                                reason = AdmissionReasonsForConsumedWake(wake, now);
                                if (reason == 0)
                                {
                                    startup = false;
                                }
                            }
                            else
                            {
                                reason = GpuSchedulerWakeReason.StartupRecovery;
                            }
                        }
                        else
                        {
                            _observedWakeGeneration = wake.Generation;
                            if (wake.Reasons == 0)
                            {
                                reason = GpuSchedulerWakeReason.StartupRecovery;
                            }
                            else
                            {
                                startup = false;
                            }
                        }
                    }
                    else if (wakeWasConsumed)
                    {
                        _observedWakeGeneration = wake.Generation;
                        reason = AdmissionReasonsForConsumedWake(wake, now);
                    }
                    else if ((_observedWakeGeneration != wake.Generation || wake.Reasons != 0) &&
                             CanAttemptAdmission(wake, now))
                    {
                        var consumption = await ConsumeLatestWakeAsync(
                                wake.Generation,
                                stoppingToken)
                            .ConfigureAwait(false);
                        wake = consumption.Snapshot;
                        _observedWakeGeneration = wake.Generation;
                        reason = AdmissionReasonsForConsumedWake(wake, now);
                    }
                    else if (wake.NextDeferredAtUtc is { } deferred && deferred <= now)
                    {
                        reason = GpuSchedulerWakeReason.DeferredRetry;
                    }

                    if (reason != 0)
                    {
                        pendingAdmissionReason = reason;
                        pendingAdmissionOperationId ??= wake.ConsumptionOperationId is { } wakeConsumptionOperationId
                            ? CreateAdmissionOperationId(wakeConsumptionOperationId)
                            : Guid.NewGuid();
                        await RunAdmissionRoundAsync(
                                pendingAdmissionOperationId.Value,
                                reason,
                                stoppingToken)
                            .ConfigureAwait(false);
                        if (wake.ConsumptionOperationId is { } consumptionOperationId)
                        {
                            await AcknowledgeWakeAsync(consumptionOperationId, stoppingToken).ConfigureAwait(false);
                        }
                        pendingAdmissionReason = 0;
                        pendingAdmissionOperationId = null;
                        startup = false;
                        continue;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "The GPU scheduler iteration failed; durable state was retained.");
                }

                await WaitForWakeOrDueTimeAsync(
                        wake.NextDeferredAtUtc,
                        nextDiagnosticAtUtc,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async ValueTask RunAdmissionRoundAsync(
        Guid operationId,
        GpuSchedulerWakeReason reason,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<GpuSchedulerCoordinator>();
        await coordinator.AdmitAsync(operationId, reason, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AcknowledgeWakeAsync(Guid consumptionOperationId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGpuSchedulerStore>();
        await store.AcknowledgeWakeAsync(Guid.NewGuid(), consumptionOperationId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RunStaleReservationDiagnosticAsync(
        DateTimeOffset heartbeatNotAfterUtc,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGpuSchedulerStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<GpuSchedulerCoordinator>();

        if (_pendingCapacityUncertaintyTransition is not null)
        {
            await MarkPendingCapacityUncertainAsync(coordinator, cancellationToken).ConfigureAwait(false);
        }

        var staleReservations = await store
            .ReadStaleCapacityReservationsAsync(heartbeatNotAfterUtc, cancellationToken)
            .ConfigureAwait(false);
        foreach (var reservation in staleReservations)
        {
            _pendingCapacityUncertaintyTransition = new PendingCapacityUncertaintyTransition(
                Guid.NewGuid(),
                reservation);
            await MarkPendingCapacityUncertainAsync(coordinator, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask MarkPendingCapacityUncertainAsync(
        GpuSchedulerCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var pending = _pendingCapacityUncertaintyTransition
            ?? throw new InvalidOperationException("No pending GPU capacity uncertainty transition exists.");
        await coordinator
            .MarkCapacityUncertainAsync(pending.OperationId, pending.Request, cancellationToken)
            .ConfigureAwait(false);
        _pendingCapacityUncertaintyTransition = null;
    }

    private async ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGpuSchedulerStore>();
        return await store.ReadWakeStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(
        Guid operationId,
        long generation,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGpuSchedulerStore>();
        return await store.ConsumeWakeAsync(operationId, generation, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GpuSchedulerWakeConsumption> ConsumeLatestWakeAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        _pendingWakeConsumption ??= new PendingWakeConsumptionRequest(Guid.NewGuid(), generation);
        return await ResolvePendingWakeConsumptionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GpuSchedulerWakeConsumption> ResolvePendingWakeConsumptionAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = _pendingWakeConsumption
                ?? throw new InvalidOperationException("No pending GPU scheduler wake consumption exists.");
            var consumption = await ConsumeWakeAsync(
                    pending.OperationId,
                    pending.ExpectedGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            _pendingWakeConsumption = null;
            if (consumption.Consumed)
            {
                return consumption;
            }

            _pendingWakeConsumption = new PendingWakeConsumptionRequest(
                Guid.NewGuid(),
                consumption.Snapshot.Generation);
        }
    }

    private sealed record PendingWakeConsumptionRequest(Guid OperationId, long ExpectedGeneration);

    private sealed record PendingCapacityUncertaintyTransition(
        Guid OperationId,
        GpuCapacityUncertaintyRequest Request);

    private static Guid CreateAdmissionOperationId(Guid consumptionOperationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"FluxKnowledge.GpuWakeAdmission.v1:{consumptionOperationId:N}"));
        hash[7] = (byte)((hash[7] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var admissionOperationId = new Guid(hash.AsSpan(0, 16));
        if (admissionOperationId == consumptionOperationId)
        {
            hash[15] ^= 1;
            admissionOperationId = new Guid(hash.AsSpan(0, 16));
        }

        return admissionOperationId;
    }

    private static GpuSchedulerWakeReason AdmissionReasonsForConsumedWake(
        GpuSchedulerWakeSnapshot wake,
        DateTimeOffset now)
    {
        return wake.EffectiveAdmissionReasons ?? AdmissionReasonsForWakeAt(wake, now);
    }

    private static GpuSchedulerWakeReason AdmissionReasonsForWakeAt(
        GpuSchedulerWakeSnapshot wake,
        DateTimeOffset now)
    {
        var reasons = wake.Reasons;
        if (wake.NextDeferredAtUtc is { } deferred && deferred > now)
        {
            reasons &= ~GpuSchedulerWakeReason.DeferredRetry;
        }

        return reasons;
    }

    private static bool CanAttemptAdmission(GpuSchedulerWakeSnapshot wake, DateTimeOffset now) =>
        AdmissionReasonsForWakeAt(wake, now) != 0;

    private async Task WaitForWakeOrDueTimeAsync(
        DateTimeOffset? nextDeferredAtUtc,
        DateTimeOffset nextDiagnosticAtUtc,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var fallbackDue = now.Add(options.FallbackInterval);
        var due = nextDeferredAtUtc is { } deferred && deferred < fallbackDue ? deferred : fallbackDue;
        if (nextDiagnosticAtUtc < due)
        {
            due = nextDiagnosticAtUtc;
        }
        var delay = due > now ? due - now : TimeSpan.Zero;
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wakeTask = wakeSignal.WaitAsync(waitCancellation.Token).AsTask();
        var delayTask = Task.Delay(delay, timeProvider, waitCancellation.Token);
        _ = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(wakeTask, delayTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
    }
}
