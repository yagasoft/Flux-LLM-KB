using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Application.Documents;

public enum DocumentOcrExecutionPhase { Unclaimed, Claimed, NativeStartPermitted, CleanupConfirmed, Settled, Blocked }
public enum DocumentOcrCleanupKind { VerifiedUnstarted, OwnedJobEmpty, StoppedMaintenance }
public enum DocumentOcrLifecycleDisposition { Applied, AlreadyApplied, StaleOwner, SourceWithdrawn, CleanupUnproven, LegacyOwnerUnproven, InvalidResult, Blocked, ResultAvailable }

public sealed record DocumentOcrSupervisorIdentity(Guid InstanceId, GpuInteractiveOwnerIdentity Process)
{
    public void Validate()
    {
        if (InstanceId == Guid.Empty) throw new ArgumentException("document-ocr-supervisor-invalid");
        Process.Validate();
    }
}

public sealed record DocumentOcrExecutionLease(GpuExecutorBatchHandle Handle, Guid MiniTaskId,
    Guid ExecutionInstanceId, DocumentOcrSupervisorIdentity Supervisor,
    DateTimeOffset ClaimedAtUtc, DateTimeOffset DeadlineAtUtc)
{
    public void Validate()
    {
        Handle.Validate(); Supervisor.Validate();
        if (MiniTaskId == Guid.Empty || ExecutionInstanceId == Guid.Empty || ClaimedAtUtc == default ||
            ClaimedAtUtc.Offset != TimeSpan.Zero || DeadlineAtUtc.Offset != TimeSpan.Zero || DeadlineAtUtc <= ClaimedAtUtc)
            throw new ArgumentException("document-ocr-execution-lease-invalid");
    }
}

public sealed record DocumentOcrProcessIdentity(int ProcessId, DateTimeOffset StartedAtUtc, string MachineFingerprint)
{
    public void Validate() => new GpuInteractiveOwnerIdentity(ProcessId, StartedAtUtc, MachineFingerprint).Validate();
}

public sealed record DocumentOcrCleanupProof(Guid ExecutionInstanceId, DocumentOcrCleanupKind Kind,
    DocumentOcrProcessIdentity? Process, DateTimeOffset ObservedAtUtc, string? MaintenanceReceiptSha256 = null);

public sealed record DocumentOcrLifecycleResult(DocumentOcrLifecycleDisposition Disposition,
    string ReasonCode, DocumentOcrExecutionLease? Lease = null)
{
    public bool Accepted => Disposition is DocumentOcrLifecycleDisposition.Applied or DocumentOcrLifecycleDisposition.AlreadyApplied;
}

public interface IDocumentOcrExecutionObserver
{
    ValueTask<DocumentOcrLifecycleResult> PermitNativeStartAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity process, CancellationToken cancellationToken);
    ValueTask<DocumentOcrLifecycleResult> ConfirmCleanupAsync(DocumentOcrExecutionLease lease,
        DocumentOcrCleanupProof proof, CancellationToken cancellationToken);
}

public sealed record DocumentOcrExecutionContext(DocumentOcrExecutionLease Lease, IDocumentOcrExecutionObserver Observer);

public sealed record DocumentOcrRecoveryObservation(DocumentOcrCleanupProof? Proof, string ReasonCode, bool OwnerRunning = false);
public interface IDocumentOcrProcessRecovery
{
    ValueTask<DocumentOcrRecoveryObservation> InspectAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity? process, bool hasStoredResult, bool cleanupConfirmed, CancellationToken cancellationToken);
}
