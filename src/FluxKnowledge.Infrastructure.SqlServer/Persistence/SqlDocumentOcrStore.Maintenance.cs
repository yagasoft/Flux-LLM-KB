using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlDocumentOcrStore
{
    /// <summary>Fresh stopped-maintenance proof only. Preserves the uncertain task and never requeues source work.</summary>
    public ValueTask<DocumentOcrLifecycleResult> AcceptTerminalCleanupAsync(DocumentOcrTerminalCleanupBinding binding,
        IDocumentOcrMaintenanceVerifier verifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(verifier);
        binding.Handle.Validate();
        if (binding.RecoveryInstanceId == Guid.Empty || binding.MiniTaskId == Guid.Empty || binding.ParentJobId == Guid.Empty ||
            binding.PipelineRecordId == Guid.Empty || binding.SourceRootId == Guid.Empty || binding.RootConfigurationRevision < 1 ||
            string.IsNullOrWhiteSpace(binding.CapacityOwnerKey) || binding.RequestRowVersion is not {Length:8} ||
            binding.TaskRowVersion is not {Length:8} || binding.JobRowVersion is not {Length:8})
            throw new ArgumentException("document-ocr-maintenance-binding-invalid");
        binding=binding with {RequestRowVersion=binding.RequestRowVersion.ToArray(),TaskRowVersion=binding.TaskRowVersion.ToArray(),JobRowVersion=binding.JobRowVersion.ToArray()};
        return MutateLifecycleAsync(async context =>
        {
            var rows=await ReadLifecycleAsync(context,binding.Handle,binding.MiniTaskId,ct).ConfigureAwait(false);
            if(rows is null || rows.Request.ParentJobId!=binding.ParentJobId || rows.Request.PipelineRecordId!=binding.PipelineRecordId ||
                rows.Task.ModelRuntimeKey!=rows.Request.ModelRuntimeKey || rows.Task.SettingsFingerprint!=rows.Request.SettingsFingerprint ||
                rows.Batch.ModelRuntimeKey!=rows.Task.ModelRuntimeKey || rows.Batch.SettingsFingerprint!=rows.Task.SettingsFingerprint ||
                rows.Batch.CapacitySlotKey!=binding.Handle.CapacitySlotKey || rows.Batch.OwnerKey!=binding.CapacityOwnerKey ||
                rows.Dispatch.OwnerKey!=binding.CapacityOwnerKey || rows.Task.ExecutionState!=(int)GpuMiniTaskExecutionState.OutcomeUncertain ||
                rows.Batch.State!=(int)GpuBatchState.Released || rows.Dispatch.State!=(int)GpuExecutorDispatchState.DeliveryUncertain ||
                rows.Slot.State!=(int)GpuCapacitySlotState.Available || rows.Slot.ActiveBatchId!=null || rows.Slot.OwnerKey!=null ||
                !rows.Task.RowVersion.SequenceEqual(binding.TaskRowVersion)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var job=await context.Jobs.SingleOrDefaultAsync(value=>value.Id==binding.ParentJobId,ct).ConfigureAwait(false);
            if(job is null || job.PipelineRecordId!=binding.PipelineRecordId || job.SourceRevision!=rows.Request.SourceRevision ||
                !job.RowVersion.SequenceEqual(binding.JobRowVersion) ||
                !await context.PipelineRecords.AnyAsync(record=>record.Id==binding.PipelineRecordId && record.Revision==rows.Request.SourceRevision &&
                    record.SourceRevisionId==rows.Request.RetainedSourceRevisionId && context.SourceRevisions.Any(revision=>revision.Id==record.SourceRevisionId &&
                        revision.SourceRootId==binding.SourceRootId),ct).ConfigureAwait(false) ||
                !await context.SourceRootConfigurations.AnyAsync(root=>root.Id==binding.SourceRootId && root.State==(int)SourceRootState.Deleting &&
                    root.ConfigurationRevision==binding.RootConfigurationRevision,ct).ConfigureAwait(false)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request=rows.Request;
            if(request.ExecutionInstanceId is not null)
                return request.ExecutionInstanceId==binding.RecoveryInstanceId && request.RecoveryReason=="document-ocr-stopped-cleanup-only" &&
                    request.CleanupProofKind==(int)DocumentOcrCleanupKind.StoppedMaintenance && HasCleanupProof(request,binding.Handle,binding.MiniTaskId) &&
                    await context.GpuExecutorEvidence.AnyAsync(e=>e.OperationId==PhaseOperation(binding.Handle,binding.RecoveryInstanceId,"cleanup") &&
                        e.DispatchId==binding.Handle.DispatchId && e.BatchId==binding.Handle.BatchId && e.CapacitySlotKey==binding.Handle.CapacitySlotKey &&
                        e.ExecutorKey==binding.Handle.ExecutorKey && e.AdmissionGeneration==binding.Handle.AdmissionGeneration &&
                        e.EvidenceClass==(int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed &&
                        e.VerifierKey=="ocr-stopped-maintenance-cleanup-only-v2" && e.ObservedAtUtc==request.NativeCleanupConfirmedAtUtc &&
                        e.RequestFingerprint==TerminalCleanupFingerprint(binding,request.PhysicalCleanupEvidenceSha256!),ct).ConfigureAwait(false)
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied,"document-ocr-maintenance-cleanup-confirmed",ToLease(request,binding.Handle))
                    : Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if(!request.RowVersion.SequenceEqual(binding.RequestRowVersion) || request.ExecutionPhase!=(int)DocumentOcrExecutionPhase.Unclaimed ||
                request.ExecutionDispatchId!=null || request.ExecutionBatchId!=null || request.ExecutionAdmissionGeneration!=null ||
                request.ExecutionMachineFingerprint!=null || request.SupervisorInstanceId!=null || request.SupervisorProcessId!=null ||
                request.SupervisorProcessStartedAtUtc!=null || request.ExecutionClaimedAtUtc!=null || request.ExecutionDeadlineAtUtc!=null ||
                request.NativeProcessId!=null || request.NativeProcessStartedAtUtc!=null || request.NativeCleanupConfirmedAtUtc!=null ||
                request.CleanupProofKind!=null || request.PhysicalCleanupEvidenceSha256!=null || request.RecoveryStartedAtUtc!=null ||
                request.RecoveryReason!=null || request.RecoveryUpdatedAtUtc!=null) return Refused(DocumentOcrLifecycleDisposition.LegacyOwnerUnproven);
            var proof=await verifier.VerifyStoppedAndCleanAsync(ct).ConfigureAwait(false);
            var now=_timeProvider.GetUtcNow();
            if(proof is null || !IsCanonicalSha256(proof.EvidenceSha256) || proof.ObservedAtUtc.Offset!=TimeSpan.Zero ||
                proof.ObservedAtUtc>now.AddSeconds(5) || proof.ObservedAtUtc<now.AddSeconds(-30)) return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            proof.Verifier.Validate();
            RecordMaintenanceProof(context,request,binding.Handle,binding.RecoveryInstanceId,proof,now,"document-ocr-stopped-cleanup-only");
            var evidence=context.GpuExecutorEvidence.Local.Single(value=>value.OperationId==PhaseOperation(binding.Handle,binding.RecoveryInstanceId,"cleanup"));
            evidence.RequestFingerprint=TerminalCleanupFingerprint(binding,proof.EvidenceSha256);
            evidence.VerifierKey="ocr-stopped-maintenance-cleanup-only-v2";
            return new(DocumentOcrLifecycleDisposition.Applied,"document-ocr-maintenance-cleanup-confirmed",ToLease(request,binding.Handle));
        },ct);
    }

    private static string TerminalCleanupFingerprint(DocumentOcrTerminalCleanupBinding binding,string evidenceSha256) =>
        Fingerprint($"cleanup-only-v2|{JsonSerializer.Serialize(binding)}|{evidenceSha256}");

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
            RecordMaintenanceProof(context,request,binding.Handle,binding.RecoveryInstanceId,proof,now,"document-ocr-stopped-maintenance-cleanup");
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-maintenance-cleanup-confirmed", ToLease(request, binding.Handle));
        }, ct);
    }

    private static void RecordMaintenanceProof(FluxKnowledgeDbContext context,DocumentOcrRequestEntity request,
        GpuExecutorBatchHandle handle,Guid recoveryInstanceId,DocumentOcrMaintenanceVerification proof,DateTimeOffset now,string reason)
    {
            request.ExecutionInstanceId = recoveryInstanceId;
            request.ExecutionDispatchId = handle.DispatchId; request.ExecutionBatchId = handle.BatchId;
            request.ExecutionAdmissionGeneration = handle.AdmissionGeneration;
            request.ExecutionMachineFingerprint = proof.Verifier.Process.MachineFingerprint;
            request.SupervisorInstanceId = proof.Verifier.InstanceId; request.SupervisorProcessId = proof.Verifier.Process.ProcessId;
            request.SupervisorProcessStartedAtUtc = proof.Verifier.Process.StartedAtUtc;
            request.ExecutionClaimedAtUtc = proof.ObservedAtUtc; request.ExecutionDeadlineAtUtc = proof.ObservedAtUtc.AddMinutes(20);
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.CleanupConfirmed;
            request.NativeCleanupConfirmedAtUtc = proof.ObservedAtUtc;
            request.CleanupProofKind = (int)DocumentOcrCleanupKind.StoppedMaintenance;
            request.PhysicalCleanupEvidenceSha256 = proof.EvidenceSha256;
            request.RecoveryReason = reason; request.RecoveryUpdatedAtUtc = now;
            context.GpuExecutorEvidence.Add(new GpuExecutorEvidenceEntity
            {
                OperationId = PhaseOperation(handle, recoveryInstanceId, "cleanup"),
                DispatchId = handle.DispatchId, BatchId = handle.BatchId,
                CapacitySlotKey = handle.CapacitySlotKey, ExecutorKey = handle.ExecutorKey,
                AdmissionGeneration = handle.AdmissionGeneration, EvidenceClass = (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed,
                VerifierKey = "ocr-stopped-maintenance-v1", ObservedAtUtc = proof.ObservedAtUtc,
                RequestFingerprint = Fingerprint($"{recoveryInstanceId:N}|{proof.EvidenceSha256}"), CreatedAtUtc = now
            });
    }
}
