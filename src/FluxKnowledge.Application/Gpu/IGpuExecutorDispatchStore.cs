namespace FluxKnowledge.Application.Gpu;

public interface IGpuExecutorDispatchStore
{
    ValueTask<IReadOnlyList<GpuExecutorBatchHandle>> ReadPendingDispatchesAsync(CancellationToken cancellationToken);

    ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(
        GpuExecutorAcknowledgement acknowledgement,
        CancellationToken cancellationToken);

    ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(
        GpuExecutorDeliveryUncertainty uncertainty,
        CancellationToken cancellationToken);

    ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(
        GpuExecutorResultReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(
        GpuExecutorTrustedEvidence evidence,
        CancellationToken cancellationToken);
}
