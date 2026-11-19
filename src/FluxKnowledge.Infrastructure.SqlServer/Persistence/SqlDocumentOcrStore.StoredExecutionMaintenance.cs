using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlDocumentOcrStore
{
    /// <summary>Stopped maintenance preserves the original execution and saved result. Settlement remains a separate idempotent step.</summary>
    public ValueTask<DocumentOcrLifecycleResult> AcceptStoredExecutionCleanupAsync(DocumentOcrStoredExecutionRecoveryBinding binding,
        IDocumentOcrMaintenanceVerifier verifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(binding.Lease); ArgumentNullException.ThrowIfNull(binding.NativeProcess);
        binding.Lease.Validate(); binding.NativeProcess.Validate();
        if (binding.OperationId == Guid.Empty || binding.ParentJobId == Guid.Empty || binding.PipelineRecordId == Guid.Empty ||
            binding.RetainedSourceRevisionId == Guid.Empty || binding.SourceRootId == Guid.Empty || binding.RootConfigurationRevision < 1 ||
            string.IsNullOrWhiteSpace(binding.CapacityOwnerKey) || binding.ResultDigest is not { Length: 32 } ||
            binding.RequestRowVersion is not { Length: 8 } || binding.TaskRowVersion is not { Length: 8 } || binding.JobRowVersion is not { Length: 8 })
            throw new ArgumentException("document-ocr-maintenance-binding-invalid");
        binding = binding with { ResultDigest = binding.ResultDigest.ToArray(), RequestRowVersion = binding.RequestRowVersion.ToArray(),
            TaskRowVersion = binding.TaskRowVersion.ToArray(), JobRowVersion = binding.JobRowVersion.ToArray() };
        return MutateLifecycleAsync(async context =>
        {
            var lease = binding.Lease;
            var rows = await ReadLifecycleAsync(context, lease.Handle, lease.MiniTaskId, ct).ConfigureAwait(false);
            if (rows is null || !MatchesLease(rows.Request, lease) || !MatchesChild(rows.Request, binding.NativeProcess) ||
                rows.Request.ParentJobId != binding.ParentJobId || rows.Request.PipelineRecordId != binding.PipelineRecordId ||
                rows.Request.RetainedSourceRevisionId != binding.RetainedSourceRevisionId ||
                rows.Task.ModelRuntimeKey != rows.Request.ModelRuntimeKey || rows.Task.SettingsFingerprint != rows.Request.SettingsFingerprint ||
                rows.Batch.ModelRuntimeKey != rows.Task.ModelRuntimeKey || rows.Batch.SettingsFingerprint != rows.Task.SettingsFingerprint ||
                rows.Batch.OwnerKey != binding.CapacityOwnerKey || rows.Dispatch.OwnerKey != binding.CapacityOwnerKey ||
                !ValidStoredResult(rows.Request, binding.ResultDigest)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            var operation = PhaseOperation(lease.Handle, lease.ExecutionInstanceId, "cleanup");
            var evidence = await context.GpuExecutorEvidence.SingleOrDefaultAsync(value => value.OperationId == operation, ct).ConfigureAwait(false);
            // A committed proof can be read back after settlement changes request/task versions and clears capacity.
            // It must still bind this exact original maintenance operation and immutable execution/result.
            if (HasCleanupProof(request, lease.Handle, lease.MiniTaskId))
                return request.CleanupProofKind == (int)DocumentOcrCleanupKind.StoppedMaintenance &&
                    IsCanonicalSha256(request.PhysicalCleanupEvidenceSha256 ?? "") && evidence is not null &&
                    evidence.DispatchId == lease.Handle.DispatchId && evidence.BatchId == lease.Handle.BatchId &&
                    evidence.CapacitySlotKey == lease.Handle.CapacitySlotKey && evidence.ExecutorKey == lease.Handle.ExecutorKey &&
                    evidence.AdmissionGeneration == lease.Handle.AdmissionGeneration &&
                    evidence.EvidenceClass == (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed &&
                    evidence.VerifierKey == "ocr-stopped-stored-execution-v3" && evidence.ObservedAtUtc == request.NativeCleanupConfirmedAtUtc &&
                    evidence.RequestFingerprint == StoredExecutionCleanupFingerprint(binding, request.PhysicalCleanupEvidenceSha256!)
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-stored-execution-cleanup-confirmed", lease)
                    : Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (evidence is not null || request.NativeCleanupConfirmedAtUtc is not null || request.CleanupProofKind is not null ||
                request.PhysicalCleanupEvidenceSha256 is not null || !OwnsSlot(rows) ||
                rows.Slot.State != (int)GpuCapacitySlotState.Uncertain || rows.Batch.State != (int)GpuBatchState.CapacityUncertain ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.DeliveryUncertain || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                request.ExecutionPhase is not ((int)DocumentOcrExecutionPhase.NativeStartPermitted or (int)DocumentOcrExecutionPhase.Blocked) ||
                !request.RowVersion.SequenceEqual(binding.RequestRowVersion) || !rows.Task.RowVersion.SequenceEqual(binding.TaskRowVersion) ||
                await context.GpuExecutorEvidence.AnyAsync(value => value.DispatchId == lease.Handle.DispatchId &&
                    value.EvidenceClass == (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (await SqlGpuModelResidencyStore.HasLiveResidencyAsync(context, lease.Handle.CapacitySlotKey, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            var job = await context.Jobs.SingleOrDefaultAsync(value => value.Id == binding.ParentJobId, ct).ConfigureAwait(false);
            if (job is null || job.PipelineRecordId != binding.PipelineRecordId || job.SourceRevision != request.SourceRevision ||
                !job.RowVersion.SequenceEqual(binding.JobRowVersion) ||
                !await context.PipelineRecords.AnyAsync(record => record.Id == binding.PipelineRecordId && !record.IsDeleted &&
                    record.Revision == request.SourceRevision && record.SourceRevisionId == binding.RetainedSourceRevisionId, ct).ConfigureAwait(false) ||
                !await context.SourceRevisions.AnyAsync(source => source.Id == binding.RetainedSourceRevisionId &&
                    source.SourceRootId == binding.SourceRootId && source.SuppressedAtUtc == null && source.ContentSha256 == request.ContentSha256, ct).ConfigureAwait(false) ||
                !await context.SourceRootConfigurations.AnyAsync(root => root.Id == binding.SourceRootId &&
                    root.State == (int)SourceRootState.Enabled && root.ConfigurationRevision == binding.RootConfigurationRevision, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var proof = await verifier.VerifyStoppedAndCleanAsync(ct).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (proof is null || !IsCanonicalSha256(proof.EvidenceSha256) || proof.ObservedAtUtc.Offset != TimeSpan.Zero ||
                proof.ObservedAtUtc < now.AddSeconds(-30) || proof.ObservedAtUtc > now.AddSeconds(5) ||
                proof.ObservedAtUtc < binding.NativeProcess.StartedAtUtc ||
                proof.Verifier.Process.MachineFingerprint != lease.Supervisor.Process.MachineFingerprint)
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            proof.Verifier.Validate();
            request.NativeCleanupConfirmedAtUtc = proof.ObservedAtUtc;
            request.CleanupProofKind = (int)DocumentOcrCleanupKind.StoppedMaintenance;
            request.PhysicalCleanupEvidenceSha256 = proof.EvidenceSha256;
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.CleanupConfirmed;
            request.RecoveryStartedAtUtc ??= now;
            request.RecoveryReason = "document-ocr-stopped-stored-execution-cleanup"; request.RecoveryUpdatedAtUtc = now;
            context.GpuExecutorEvidence.Add(new GpuExecutorEvidenceEntity
            {
                OperationId = operation, DispatchId = lease.Handle.DispatchId, BatchId = lease.Handle.BatchId,
                CapacitySlotKey = lease.Handle.CapacitySlotKey, ExecutorKey = lease.Handle.ExecutorKey,
                AdmissionGeneration = lease.Handle.AdmissionGeneration, EvidenceClass = (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed,
                VerifierKey = "ocr-stopped-stored-execution-v3", ObservedAtUtc = proof.ObservedAtUtc,
                RequestFingerprint = StoredExecutionCleanupFingerprint(binding, proof.EvidenceSha256), CreatedAtUtc = now
            });
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-stored-execution-cleanup-confirmed", lease);
        }, ct);
    }

    private static string StoredExecutionCleanupFingerprint(DocumentOcrStoredExecutionRecoveryBinding binding, string evidenceSha256) =>
        Fingerprint($"stored-execution-v3|{JsonSerializer.Serialize(binding)}|{evidenceSha256}");
}
