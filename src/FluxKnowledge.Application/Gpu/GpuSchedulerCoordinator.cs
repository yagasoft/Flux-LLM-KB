using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

public sealed class GpuSchedulerCoordinator(
    IGpuSchedulerStore store,
    IGpuAdmissionGate admissionGate,
    IStatusEventPublisher statusPublisher,
    IGpuSchedulerWakeSignal wakeSignal,
    TimeProvider timeProvider,
    GpuSchedulerOptions options,
    IGpuExecutorDispatchSignal? executorDispatchSignal = null)
{
    public async ValueTask<GpuMiniTaskHandoffResult> HandoffAsync(
        GpuMiniTaskHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await store.GpuTaskHandoffAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Committed && !result.IsIdempotentReplay)
        {
            try
            {
                await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                wakeSignal.Notify(GpuSchedulerWakeReason.WorkReady);
            }
        }

        return result;
    }

    public ValueTask<GpuSchedulerAdmissionRoundResult> AdmitAsync(
        GpuSchedulerWakeReason wakeReason,
        CancellationToken cancellationToken) =>
        AdmitAsync(Guid.NewGuid(), wakeReason, cancellationToken);

    public async ValueTask<GpuSchedulerAdmissionRoundResult> AdmitAsync(
        Guid operationId,
        GpuSchedulerWakeReason wakeReason,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A scheduler admission operation ID is required.", nameof(operationId));
        }

        if (wakeReason == 0 ||
            (wakeReason & ~KnownWakeReasons) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wakeReason));
        }

        var result = await store.RunAdmissionRoundAsync(
                operationId,
                wakeReason,
                options,
                DecideAdmissionAsync,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Committed)
        {
            var localWakeReason = result.Disposition switch
            {
                GpuAdmissionDisposition.Defer => GpuSchedulerWakeReason.DeferredRetry,
                GpuAdmissionDisposition.Busy => wakeReason,
                _ => (GpuSchedulerWakeReason)0
            };
            if (localWakeReason != 0)
            {
                try
                {
                    await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    wakeSignal.Notify(localWakeReason);
                }
            }
            else
            {
                try
                {
                    await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (result.Disposition == GpuAdmissionDisposition.Admit && !result.IsIdempotentReplay)
                    {
                        NotifyExecutorDispatch();
                    }
                }
            }
        }

        return result;
    }

    public async ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(
        Guid operationId,
        GpuBatchCallback callback,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(callback.Outcomes);
        callback = callback with { Outcomes = callback.Outcomes.ToArray() };
        callback.Validate();
        var result = await store.ApplyBatchCallbackAsync(operationId, callback, cancellationToken).ConfigureAwait(false);
        if (result.Committed)
        {
            var wakeReason = callback.Kind == GpuBatchCallbackKind.SafeBoundary
                ? GpuSchedulerWakeReason.SafeBoundary
                : (GpuSchedulerWakeReason)0;
            if (callback.CapacityReleased)
            {
                wakeReason |= GpuSchedulerWakeReason.CapacityReleased;
            }

            try
            {
                await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (wakeReason != 0)
                {
                    wakeSignal.Notify(wakeReason);
                }
            }
        }

        return result;
    }

    public async ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(
        Guid operationId,
        GpuCapacityUncertaintyRequest request,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(request);
        var result = await store.MarkCapacityUncertainAsync(operationId, request, cancellationToken).ConfigureAwait(false);
        if (result.Committed)
        {
            await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(
        Guid operationId,
        GpuTrustedCapacityReconciliation request,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var result = await store.ReconcileCapacityAsync(operationId, request, cancellationToken).ConfigureAwait(false);
        if (result.Committed)
        {
            try
            {
                await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                wakeSignal.Notify(
                    GpuSchedulerWakeReason.Reconciliation |
                    GpuSchedulerWakeReason.CapacityReleased);
            }
        }

        return result;
    }

    public async ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(
        Guid operationId,
        GpuTaskOutcomeReconciliation request,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var result = await store.ReconcileTaskOutcomeAsync(operationId, request, cancellationToken).ConfigureAwait(false);
        if (result.Committed)
        {
            try
            {
                await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                wakeSignal.Notify(GpuSchedulerWakeReason.Reconciliation);
            }
        }

        return result;
    }

    private async ValueTask<GpuAdmissionDecision> DecideAdmissionAsync(
        GpuBatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        var decision = await admissionGate.DecideAsync(candidate, cancellationToken).ConfigureAwait(false);
        return decision.Validate(options);
    }

    private ValueTask PublishStatusAsync(CancellationToken cancellationToken) =>
        statusPublisher.PublishAsync(
            new StatusChanged(null, "gpu-scheduler", timeProvider.GetUtcNow()),
            cancellationToken);

    private void NotifyExecutorDispatch()
    {
        try
        {
            executorDispatchSignal?.Notify();
        }
        catch
        {
            // A local prompt is not scheduler state and cannot alter a committed admission.
        }
    }

    private static void RequireLifecycleOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A lifecycle operation ID is required.", nameof(operationId));
        }
    }

    private const GpuSchedulerWakeReason KnownWakeReasons =
        GpuSchedulerWakeReason.WorkReady |
        GpuSchedulerWakeReason.SafeBoundary |
        GpuSchedulerWakeReason.CapacityReleased |
        GpuSchedulerWakeReason.DeferredRetry |
        GpuSchedulerWakeReason.StartupRecovery |
        GpuSchedulerWakeReason.Reconciliation;
}
