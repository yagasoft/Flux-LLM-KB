using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Application.Documents;

public sealed record DocumentOcrLegacyRecoveryBinding(GpuExecutorBatchHandle Handle, Guid MiniTaskId,
    Guid ParentJobId, string CapacityOwnerKey, byte[] ResultDigest, Guid RecoveryInstanceId);

public sealed record DocumentOcrMaintenanceVerification(DocumentOcrSupervisorIdentity Verifier,
    DateTimeOffset ObservedAtUtc, string EvidenceSha256);

/// <summary>Privileged, fresh OS/driver verification. A receipt hash by itself is not authority.</summary>
public interface IDocumentOcrMaintenanceVerifier
{
    ValueTask<DocumentOcrMaintenanceVerification?> VerifyStoppedAndCleanAsync(CancellationToken cancellationToken);
}
