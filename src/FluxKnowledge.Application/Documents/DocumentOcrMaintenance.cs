using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Application.Documents;

public sealed record DocumentOcrLegacyRecoveryBinding(GpuExecutorBatchHandle Handle, Guid MiniTaskId,
    Guid ParentJobId, string CapacityOwnerKey, byte[] ResultDigest, Guid RecoveryInstanceId);

public sealed record DocumentOcrTerminalCleanupBinding(GpuExecutorBatchHandle Handle, Guid MiniTaskId,
    Guid ParentJobId, Guid PipelineRecordId, Guid SourceRootId, long RootConfigurationRevision,
    string CapacityOwnerKey, byte[] RequestRowVersion, byte[] TaskRowVersion, byte[] JobRowVersion, Guid RecoveryInstanceId);

public sealed record DocumentOcrStoredExecutionRecoveryBinding(DocumentOcrExecutionLease Lease,
    DocumentOcrProcessIdentity NativeProcess, Guid ParentJobId, Guid PipelineRecordId, Guid RetainedSourceRevisionId,
    Guid SourceRootId, long RootConfigurationRevision, string CapacityOwnerKey, byte[] ResultDigest,
    byte[] RequestRowVersion, byte[] TaskRowVersion, byte[] JobRowVersion, Guid OperationId);

public sealed record DocumentOcrMaintenanceVerification(DocumentOcrSupervisorIdentity Verifier,
    DateTimeOffset ObservedAtUtc, string EvidenceSha256);

/// <summary>Privileged, fresh OS/driver verification. A receipt hash by itself is not authority.</summary>
public interface IDocumentOcrMaintenanceVerifier
{
    ValueTask<DocumentOcrMaintenanceVerification?> VerifyStoppedAndCleanAsync(CancellationToken cancellationToken);
}
