using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

/// <summary>
/// Opaque, immutable fence for one durably admitted executor dispatch.
/// </summary>
public sealed record GpuExecutorBatchHandle(
    Guid BatchId,
    string CapacitySlotKey,
    string ExecutorKey,
    long AdmissionGeneration,
    Guid DispatchId)
{
    public void Validate()
    {
        if (BatchId == Guid.Empty)
        {
            throw new ArgumentException("An executor handle requires a batch ID.", nameof(BatchId));
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(CapacitySlotKey, nameof(CapacitySlotKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(ExecutorKey, nameof(ExecutorKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (AdmissionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AdmissionGeneration));
        }

        if (DispatchId == Guid.Empty)
        {
            throw new ArgumentException("An executor handle requires a dispatch ID.", nameof(DispatchId));
        }
    }
}

public enum GpuExecutorDispatchState
{
    PendingDelivery,
    Acknowledged,
    ReceiptRecorded,
    DeliveryUncertain,
    Terminal
}

public enum GpuExecutorEvidenceClass
{
    CapacityReleaseConfirmed,
    TaskOutcomeConfirmed,
    TaskOutcomeUncertainConfirmed
}

public sealed record GpuExecutorAcknowledgement(Guid OperationId, GpuExecutorBatchHandle Handle)
{
    public void Validate()
    {
        RequireOperationId(OperationId);
        ArgumentNullException.ThrowIfNull(Handle);
        Handle.Validate();
    }

    internal static void RequireOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An executor operation ID is required.", nameof(operationId));
        }
    }
}

public sealed record GpuExecutorDeliveryUncertainty(Guid OperationId, GpuExecutorBatchHandle Handle)
{
    public void Validate()
    {
        GpuExecutorAcknowledgement.RequireOperationId(OperationId);
        ArgumentNullException.ThrowIfNull(Handle);
        Handle.Validate();
    }
}

public sealed record GpuExecutorResultReceipt
{
    private readonly byte[]? _opaqueResultDigest;

    public GpuExecutorResultReceipt(
        Guid operationId,
        GpuExecutorBatchHandle handle,
        Guid miniTaskId,
        GpuMiniTaskBoundaryDisposition disposition,
        byte[]? opaqueResultDigest,
        GpuExecutorEvidenceClass evidenceClass)
    {
        OperationId = operationId;
        Handle = handle;
        MiniTaskId = miniTaskId;
        Disposition = disposition;
        _opaqueResultDigest = opaqueResultDigest?.ToArray();
        EvidenceClass = evidenceClass;
    }

    public Guid OperationId { get; }

    public GpuExecutorBatchHandle Handle { get; }

    public Guid MiniTaskId { get; }

    public GpuMiniTaskBoundaryDisposition Disposition { get; }

    public byte[]? OpaqueResultDigest => _opaqueResultDigest?.ToArray();

    public GpuExecutorEvidenceClass EvidenceClass { get; }

    public void Validate()
    {
        GpuExecutorAcknowledgement.RequireOperationId(OperationId);
        ArgumentNullException.ThrowIfNull(Handle);
        Handle.Validate();
        if (MiniTaskId == Guid.Empty)
        {
            throw new ArgumentException("A result receipt requires a mini-task ID.", nameof(MiniTaskId));
        }

        if (!Enum.IsDefined(Disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(Disposition));
        }

        if (OpaqueResultDigest is not null && OpaqueResultDigest.Length != 32)
        {
            throw new ArgumentException("A result digest must be exactly 32 bytes when supplied.", nameof(OpaqueResultDigest));
        }

        var expectedEvidenceClass = Disposition switch
        {
            GpuMiniTaskBoundaryDisposition.Completed => GpuExecutorEvidenceClass.TaskOutcomeConfirmed,
            GpuMiniTaskBoundaryDisposition.OutcomeUncertain => GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed,
            _ => throw new ArgumentOutOfRangeException(nameof(Disposition))
        };
        if (EvidenceClass != expectedEvidenceClass)
        {
            throw new ArgumentException("Receipt evidence must match its task outcome disposition.", nameof(EvidenceClass));
        }
    }
}

public sealed record GpuExecutorTrustedEvidence(
    Guid OperationId,
    GpuExecutorBatchHandle Handle,
    string VerifierKey,
    DateTimeOffset ObservedAtUtc,
    GpuExecutorEvidenceClass EvidenceClass)
{
    public void Validate()
    {
        GpuExecutorAcknowledgement.RequireOperationId(OperationId);
        ArgumentNullException.ThrowIfNull(Handle);
        Handle.Validate();
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(VerifierKey, nameof(VerifierKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (ObservedAtUtc == default || ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Trusted evidence observations must use a non-default UTC timestamp.",
                nameof(ObservedAtUtc));
        }
        if (!Enum.IsDefined(EvidenceClass))
        {
            throw new ArgumentOutOfRangeException(nameof(EvidenceClass));
        }
    }
}

public sealed record GpuExecutorDispatchMutationResult(bool Accepted, bool Committed);
