using System.Security.Cryptography;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlDocumentOcrStore
{
    /// <summary>Explicit stopped maintenance only; never registered in normal runtime recovery.</summary>
    public ValueTask<DocumentOcrLifecycleResult> AcceptLegacyCleanupAsync(DocumentOcrLegacyRecoveryBinding binding,
        IDocumentOcrMaintenanceVerifier verifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(verifier);
        binding.Handle.Validate();
        if (binding.RecoveryInstanceId == Guid.Empty || binding.ParentJobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(binding.CapacityOwnerKey) || binding.ResultDigest is not { Length: 32 })
            throw new ArgumentException("document-ocr-maintenance-binding-invalid");
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, binding.Handle, binding.MiniTaskId, ct).ConfigureAwait(false);
            if (rows is null || rows.Request.ParentJobId != binding.ParentJobId || rows.Dispatch.OwnerKey != binding.CapacityOwnerKey ||
                !ValidStoredResult(rows.Request, binding.ResultDigest)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            if (request.ExecutionInstanceId is not null)
                return request.ExecutionInstanceId == binding.RecoveryInstanceId && request.CleanupProofKind == (int)DocumentOcrCleanupKind.StoppedMaintenance &&
                    HasCleanupProof(request, binding.Handle, binding.MiniTaskId)
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-maintenance-cleanup-confirmed", ToLease(request, binding.Handle))
                    : Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!OwnsSlot(rows) || rows.Slot.State != (int)GpuCapacitySlotState.Uncertain ||
                rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.DeliveryUncertain)
                return Refused(DocumentOcrLifecycleDisposition.LegacyOwnerUnproven);

            // Keep the same admission/mutation lock across fresh physical observation and proof.
            // This path is never authorised by a caller-supplied evidence-class row or bare hash.
            var proof = await verifier.VerifyStoppedAndCleanAsync(ct).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (proof is null || !IsCanonicalSha256(proof.EvidenceSha256) || proof.ObservedAtUtc.Offset != TimeSpan.Zero ||
                proof.ObservedAtUtc > now.AddSeconds(5) || proof.ObservedAtUtc < now.AddSeconds(-30))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            proof.Verifier.Validate();
            request.ExecutionInstanceId = binding.RecoveryInstanceId;
            request.ExecutionDispatchId = binding.Handle.DispatchId; request.ExecutionBatchId = binding.Handle.BatchId;
            request.ExecutionAdmissionGeneration = binding.Handle.AdmissionGeneration;
            request.ExecutionMachineFingerprint = proof.Verifier.Process.MachineFingerprint;
            request.SupervisorInstanceId = proof.Verifier.InstanceId; request.SupervisorProcessId = proof.Verifier.Process.ProcessId;
            request.SupervisorProcessStartedAtUtc = proof.Verifier.Process.StartedAtUtc;
            request.ExecutionClaimedAtUtc = proof.ObservedAtUtc; request.ExecutionDeadlineAtUtc = proof.ObservedAtUtc.AddMinutes(20);
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.CleanupConfirmed;
            request.NativeCleanupConfirmedAtUtc = proof.ObservedAtUtc;
            request.CleanupProofKind = (int)DocumentOcrCleanupKind.StoppedMaintenance;
            request.PhysicalCleanupEvidenceSha256 = proof.EvidenceSha256;
            request.RecoveryReason = "document-ocr-stopped-maintenance-cleanup"; request.RecoveryUpdatedAtUtc = now;
            context.GpuExecutorEvidence.Add(new GpuExecutorEvidenceEntity
            {
                OperationId = PhaseOperation(binding.Handle, binding.RecoveryInstanceId, "cleanup"),
                DispatchId = binding.Handle.DispatchId, BatchId = binding.Handle.BatchId,
                CapacitySlotKey = binding.Handle.CapacitySlotKey, ExecutorKey = binding.Handle.ExecutorKey,
                AdmissionGeneration = binding.Handle.AdmissionGeneration, EvidenceClass = (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed,
                VerifierKey = "ocr-stopped-maintenance-v1", ObservedAtUtc = proof.ObservedAtUtc,
                RequestFingerprint = Fingerprint($"{binding.RecoveryInstanceId:N}|{proof.EvidenceSha256}"), CreatedAtUtc = now
            });
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-maintenance-cleanup-confirmed", ToLease(request, binding.Handle));
        }, ct);
    }
}
