namespace FluxKnowledge.Application.Gpu;

/// <summary>
/// Internal executor lifecycle boundary. It validates executor input before forwarding it to
/// durable dispatch storage or the existing scheduler callback primitive.
/// </summary>
public sealed class GpuExecutorLifecycleCoordinator(
    IGpuExecutorDispatchStore dispatchStore,
    GpuSchedulerCoordinator schedulerCoordinator) : IGpuExecutorLifecycleSink
{
    public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(
        GpuExecutorAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        acknowledgement.Validate();
        return dispatchStore.AcknowledgeAsync(acknowledgement, cancellationToken);
    }

    public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(
        GpuExecutorDeliveryUncertainty uncertainty,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uncertainty);
        uncertainty.Validate();
        return dispatchStore.MarkDeliveryUncertainAsync(uncertainty, cancellationToken);
    }

    public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(
        GpuExecutorResultReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        return dispatchStore.RecordReceiptAsync(receipt, cancellationToken);
    }

    public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(
        GpuExecutorTrustedEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        return dispatchStore.RecordTrustedEvidenceAsync(evidence, cancellationToken);
    }

    public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(
        Guid operationId,
        GpuBatchCallback callback,
        CancellationToken cancellationToken) =>
        schedulerCoordinator.HandleCallbackAsync(operationId, callback, cancellationToken);
}
