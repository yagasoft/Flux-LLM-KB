using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Parent-owned completion authority for the closed deterministic worker test frame.</summary>
internal interface INativeWorkerTestLifecycleScriptSource
{
    ValueTask<NativeWorkerReceiptAndCompleteScript?> GetReceiptAndCompleteAsync(
        GpuExecutorBatchHandle handle,
        CancellationToken cancellationToken);
}

internal sealed record NativeWorkerReceiptAndCompleteScript(
    IReadOnlyList<GpuExecutorResultReceipt> Receipts,
    Guid CallbackOperationId,
    GpuBatchCallback Callback)
{
    public void ValidateFor(GpuExecutorBatchHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        ArgumentNullException.ThrowIfNull(Receipts);
        if (Receipts.Count == 0 || CallbackOperationId == Guid.Empty)
        {
            throw new ArgumentException("A completion script requires receipts and a callback operation.");
        }

        if (Receipts.Any(receipt => receipt is null) ||
            Receipts.Select(receipt => receipt.OperationId).Distinct().Count() != Receipts.Count ||
            Receipts.Select(receipt => receipt.MiniTaskId).Distinct().Count() != Receipts.Count ||
            Receipts.Any(receipt => receipt.Handle != handle || receipt.Disposition != GpuMiniTaskBoundaryDisposition.Completed || receipt.EvidenceClass != GpuExecutorEvidenceClass.TaskOutcomeConfirmed))
        {
            throw new ArgumentException("Completion receipts must be distinct, completed and exactly pre-authorised for the delivered handle.");
        }

        foreach (var receipt in Receipts) receipt.Validate();
        if (Receipts.Any(receipt => receipt.OperationId == CallbackOperationId))
        {
            throw new ArgumentException("The callback operation must be distinct from receipt operations.");
        }

        Callback.Validate();
        if (Callback.Handle != handle || Callback.Kind != GpuBatchCallbackKind.Completed || !Callback.CapacityReleased ||
            Callback.Outcomes.Any(outcome => outcome.Disposition != GpuMiniTaskBoundaryDisposition.Completed) ||
            !Callback.Outcomes.Select(outcome => outcome.MiniTaskId).Order().SequenceEqual(Receipts.Select(receipt => receipt.MiniTaskId).Order()))
        {
            throw new ArgumentException("The completion callback must exactly confirm the pre-authorised completed task set.");
        }
    }
}
