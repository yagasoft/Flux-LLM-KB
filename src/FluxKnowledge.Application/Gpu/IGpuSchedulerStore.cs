using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

public interface IGpuSchedulerStore
{
    ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(
        GpuMiniTaskHandoffRequest request,
        CancellationToken cancellationToken);

    ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(
        Guid operationId,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission,
        CancellationToken cancellationToken);

    ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(
        Guid operationId,
        GpuBatchCallback callback,
        CancellationToken cancellationToken);

    ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(
        Guid operationId,
        GpuCapacityUncertaintyRequest request,
        CancellationToken cancellationToken);

    ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(
        Guid operationId,
        GpuTrustedCapacityReconciliation request,
        CancellationToken cancellationToken);

    ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(
        Guid operationId,
        GpuTaskOutcomeReconciliation request,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(
        DateTimeOffset heartbeatNotAfterUtc,
        CancellationToken cancellationToken);

    ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken);

    ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(
        Guid operationId,
        long expectedGeneration,
        CancellationToken cancellationToken);

    /// <summary>Acknowledges one exact, durably consumed wake after its admission attempt committed durably.</summary>
    ValueTask<bool> AcknowledgeWakeAsync(
        Guid operationId,
        Guid consumptionOperationId,
        CancellationToken cancellationToken);

    ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken);
}
