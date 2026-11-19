using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlMediaTranscriptionGpuStore
{
    public async ValueTask<MediaTranscriptionGpuClaim> TryClaimExecutionAsync(GpuExecutorBatchHandle handle, Guid executionId,
        DocumentOcrSupervisorIdentity supervisor, CancellationToken ct)
    {
        handle.Validate();
        supervisor.Validate();
        if (executionId == Guid.Empty) throw new ArgumentException("media-transcription-execution-invalid", nameof(executionId));
        return await MutateAsync<MediaTranscriptionGpuClaim>(async db =>
        {
            var rows = await ReadExecutionAsync(db, handle, ct).ConfigureAwait(false);
            if (rows is null || !OwnsReservation(rows) ||
                rows.Slot.State != (int)GpuCapacitySlotState.Reserved ||
                rows.Batch.State != (int)GpuBatchState.Active || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active)
                return new(Stale());
            var request = rows.Request;
            var now = clock.GetUtcNow();
            var resident = await db.GpuModelResidencies.AsNoTracking().SingleOrDefaultAsync(row =>
                row.SlotKey == handle.CapacitySlotKey && row.ReleasedAtUtc == null, ct).ConfigureAwait(false);
            if (resident is not null)
            {
                var fence = SqlGpuModelResidencyStore.ReadFence(resident);
                if (resident.State != (int)GpuModelResidencyState.Borrowed ||
                    resident.BorrowedDispatchId != handle.DispatchId || resident.BorrowedExecutionInstanceId != executionId ||
                    resident.RuntimeKey != request.ModelRuntimeKey || resident.SettingsFingerprint != request.SettingsFingerprint ||
                    resident.ModelKey != "general" || fence.Owner != supervisor.Process ||
                    fence.Helper is not { } child || child.JobId != fence.ResidencyId || child.ProcessId <= 0 ||
                    child.StartedAtUtc < fence.Owner.StartedAtUtc || child.StartedAtUtc > now)
                    return new(Stale());
            }

            var available = await SourceIsEnabledAsync(db, request, ct).ConfigureAwait(false);
            if (request.ExecutionInstanceId is { } existing)
            {
                if (existing != executionId || !MatchesHandle(request, handle) || !MatchesSupervisor(request, supervisor) ||
                    request.ExecutionPhase is not ((int)DocumentOcrExecutionPhase.Claimed or (int)DocumentOcrExecutionPhase.NativeStartPermitted))
                    return new(Stale());
                return new(Accepted("claim", Lease(request, handle), replayed: true), new SourceRevisionId(request.RetainedSourceRevisionId),
                    request.ContentSha256, available);
            }
            if (rows.Dispatch.State != (int)GpuExecutorDispatchState.PendingDelivery ||
                !await Jobs(db, request).AnyAsync(j => j.PublicState == (int)PublicJobState.GpuProcessing, ct).ConfigureAwait(false))
                return new(Stale());

            RecordClaim(rows, handle, executionId, supervisor, now);
            return new(Accepted("claim", Lease(request, handle)), new SourceRevisionId(request.RetainedSourceRevisionId),
                request.ContentSha256, available);
        }, ct).ConfigureAwait(false);
    }

    private static void RecordClaim(ExecutionRows rows, GpuExecutorBatchHandle handle, Guid executionId,
        DocumentOcrSupervisorIdentity supervisor, DateTimeOffset now)
    {
        var request = rows.Request;
        request.ExecutionInstanceId = executionId;
        request.ExecutionBatchId = handle.BatchId;
        request.ExecutionDispatchId = handle.DispatchId;
        request.ExecutionAdmissionGeneration = handle.AdmissionGeneration;
        request.SupervisorInstanceId = supervisor.InstanceId;
        request.SupervisorProcessId = supervisor.Process.ProcessId;
        request.SupervisorProcessStartedAtUtc = supervisor.Process.StartedAtUtc;
        request.MachineFingerprint = supervisor.Process.MachineFingerprint;
        request.ClaimedAtUtc = now;
        request.DeadlineAtUtc = now.AddMinutes(20);
        request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Claimed;
        rows.Dispatch.State = (int)GpuExecutorDispatchState.Acknowledged;
        rows.Dispatch.AcknowledgedAtUtc = now;
        rows.Dispatch.UpdatedAtUtc = now;
    }

    public ValueTask<DocumentOcrLifecycleResult> PermitNativeStartAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity process, CancellationToken ct) => PermitNativeStartCoreAsync(lease, process, null, ct);

    internal ValueTask<DocumentOcrLifecycleResult> PermitResidentNativeStartAsync(DocumentOcrExecutionLease lease,
        GpuResidentModelFence borrow, DocumentOcrProcessIdentity process, CancellationToken ct)
    {
        borrow.Validate();
        return PermitNativeStartCoreAsync(lease, process, borrow, ct);
    }

    private ValueTask<DocumentOcrLifecycleResult> PermitNativeStartCoreAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity process, GpuResidentModelFence? borrow, CancellationToken ct)
    {
        process.Validate();
        return WithLeaseAsync(lease, async (db, rows) =>
        {
            var request = rows.Request;
            if (!OwnsReservation(rows) || rows.Slot.State != (int)GpuCapacitySlotState.Reserved ||
                rows.Batch.State != (int)GpuBatchState.Active || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.Acknowledged || clock.GetUtcNow() >= lease.DeadlineAtUtc ||
                process.MachineFingerprint != lease.Supervisor.Process.MachineFingerprint)
                return Stale();
            if (borrow is null
                ? process.StartedAtUtc < lease.ClaimedAtUtc ||
                  await SqlGpuModelResidencyStore.HasLiveResidencyAsync(db, lease.Handle.CapacitySlotKey, ct).ConfigureAwait(false)
                : !MatchesResidentHelper(lease, borrow, process) ||
                  !await MatchesActiveBorrowAsync(db, lease, borrow, ct).ConfigureAwait(false))
                return Stale();
            if (!await SourceIsEnabledAsync(db, request, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.SourceWithdrawn, "source-unavailable");
            if (request.ExecutionPhase == (int)DocumentOcrExecutionPhase.NativeStartPermitted && MatchesProcess(request, process))
                return Accepted("start-permitted", lease, replayed: true);
            if (request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Claimed) return Stale();
            request.NativeProcessId = process.ProcessId;
            request.NativeProcessStartedAtUtc = process.StartedAtUtc;
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.NativeStartPermitted;
            return Accepted("start-permitted", lease);
        }, ct);
    }

    public ValueTask<DocumentOcrLifecycleResult> HeartbeatAsync(DocumentOcrExecutionLease lease, CancellationToken ct) =>
        WithLeaseAsync(lease, async (db, rows) =>
        {
            var now = clock.GetUtcNow();
            if (!OwnsReservation(rows) || now >= lease.DeadlineAtUtc ||
                rows.Request.ExecutionPhase is not ((int)DocumentOcrExecutionPhase.Claimed or (int)DocumentOcrExecutionPhase.NativeStartPermitted) ||
                rows.Slot.State != (int)GpuCapacitySlotState.Reserved || rows.Batch.State != (int)GpuBatchState.Active ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.Acknowledged)
                return Stale();
            var request = rows.Request;
            if (!await BindingIsAvailableAsync(db, request.PipelineRecordId, request.SourceRevision,
                    request.RetainedSourceRevisionId, request.ContentSha256, false, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.SourceWithdrawn, "source-unavailable");
            rows.Slot.LastHeartbeatAtUtc = now;
            rows.Slot.UpdatedAtUtc = now;
            rows.Batch.LastHeartbeatAtUtc = now;
            rows.Batch.UpdatedAtUtc = now;
            return Accepted("heartbeat", lease);
        }, ct);

    public ValueTask<DocumentOcrLifecycleResult> ConfirmCleanupAsync(DocumentOcrExecutionLease lease,
        DocumentOcrCleanupProof proof, CancellationToken ct) =>
        WithLeaseAsync(lease, async (db, rows) =>
        {
            var request = rows.Request;
            var now = clock.GetUtcNow();
            var matchesKind = proof.Kind switch
            {
                DocumentOcrCleanupKind.VerifiedUnstarted => proof.Process is null && request.NativeProcessId is null,
                DocumentOcrCleanupKind.OwnedJobEmpty => proof.Process is not null && MatchesProcess(request, proof.Process),
                _ => false
            };
            if (proof.ExecutionInstanceId != lease.ExecutionInstanceId || !matchesKind || proof.MaintenanceReceiptSha256 is not null ||
                proof.ObservedAtUtc.Offset != TimeSpan.Zero || proof.ObservedAtUtc < lease.ClaimedAtUtc || proof.ObservedAtUtc > now.AddSeconds(5))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "cleanup-unproven");
            if (request.CleanupConfirmedAtUtc is not null && request.CleanupKind == (int)proof.Kind)
                return Accepted("cleanup-confirmed", lease, replayed: true);
            var requiredPhase = proof.Kind == DocumentOcrCleanupKind.VerifiedUnstarted
                ? DocumentOcrExecutionPhase.Claimed : DocumentOcrExecutionPhase.NativeStartPermitted;
            if (!OwnsReservation(rows) || request.ExecutionPhase != (int)requiredPhase) return Stale();
            if (await SqlGpuModelResidencyStore.HasLiveResidencyAsync(db, lease.Handle.CapacitySlotKey, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-native-capacity-held");
            return RecordCleanup(db, request, lease, proof.Kind, proof.ObservedAtUtc, now, "media-transcription-owned-job-v1");
        }, ct);

    private static DocumentOcrLifecycleResult RecordCleanup(FluxKnowledgeDbContext db, MediaTranscriptionGpuRequestEntity request,
        DocumentOcrExecutionLease lease, DocumentOcrCleanupKind kind, DateTimeOffset observed, DateTimeOffset now,
        string verifier, string? fingerprint = null)
    {
        request.CleanupConfirmedAtUtc = observed;
        request.CleanupKind = (int)kind;
        request.ExecutionPhase = (int)DocumentOcrExecutionPhase.CleanupConfirmed;
        var phaseKey = OperationFingerprint("cleanup", lease.Handle.DispatchId, lease.ExecutionInstanceId, lease.Handle.AdmissionGeneration);
        db.GpuExecutorEvidence.Add(new()
        {
            OperationId = OperationId(phaseKey), BatchId = lease.Handle.BatchId, DispatchId = lease.Handle.DispatchId,
            CapacitySlotKey = lease.Handle.CapacitySlotKey, ExecutorKey = lease.Handle.ExecutorKey,
            AdmissionGeneration = lease.Handle.AdmissionGeneration,
            EvidenceClass = (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed,
            VerifierKey = verifier, ObservedAtUtc = observed,
            RequestFingerprint = fingerprint ?? phaseKey, CreatedAtUtc = now
        });
        return Accepted("cleanup-confirmed", lease);
    }

    private ValueTask<DocumentOcrLifecycleResult> WithLeaseAsync(DocumentOcrExecutionLease lease,
        Func<FluxKnowledgeDbContext, ExecutionRows, Task<DocumentOcrLifecycleResult>> action, CancellationToken ct)
    {
        lease.Validate();
        return MutateAsync(async db =>
        {
            var rows = await ReadExecutionAsync(db, lease.Handle, ct).ConfigureAwait(false);
            return rows is null || rows.Request.ExecutionInstanceId is null || !MatchesHandle(rows.Request, lease.Handle) ||
                Lease(rows.Request, lease.Handle) != lease
                ? Stale() : await action(db, rows).ConfigureAwait(false);
        }, ct);
    }

    private async Task<ExecutionRows?> ReadExecutionAsync(FluxKnowledgeDbContext db, GpuExecutorBatchHandle handle, CancellationToken ct)
    {
        if (handle.ExecutorKey != runtime.ExecutorKey || handle.CapacitySlotKey != PaddleOcrVlmRuntimeContract.CapacitySlotKey)
            return null;
        return await (from dispatch in db.GpuExecutorDispatches
                      join batch in db.GpuBatches on dispatch.BatchId equals batch.Id
                      join task in db.GpuMiniTasks on batch.Id equals task.BatchId
                      join request in db.MediaTranscriptionGpuRequests on task.Id equals request.MiniTaskId
                      join slot in db.GpuCapacitySlots on dispatch.CapacitySlotKey equals slot.SlotKey
                      where dispatch.DispatchId == handle.DispatchId && dispatch.ExecutorKey == handle.ExecutorKey &&
                        dispatch.CapacitySlotKey == handle.CapacitySlotKey && batch.Id == handle.BatchId &&
                        dispatch.AdmissionGeneration == handle.AdmissionGeneration && batch.AdmissionGeneration == handle.AdmissionGeneration &&
                        task.AdmissionGeneration == handle.AdmissionGeneration && batch.ItemCount == 1 &&
                        task.PriorityLane == (int)MediaTranscriptionGpuRuntime.PriorityLane &&
                        task.ModelRuntimeKey == runtime.ModelRuntimeKey && task.SettingsFingerprint == runtime.SettingsFingerprint &&
                        request.ParentJobId == task.ParentJobId && request.SourceRevision == task.SourceRevision &&
                        request.ModelRuntimeKey == task.ModelRuntimeKey && request.SettingsFingerprint == task.SettingsFingerprint &&
                        batch.ModelRuntimeKey == task.ModelRuntimeKey && batch.SettingsFingerprint == task.SettingsFingerprint &&
                        batch.CapacitySlotKey == dispatch.CapacitySlotKey && batch.OwnerKey == dispatch.OwnerKey &&
                        !db.GpuMiniTasks.Any(other => other.BatchId == batch.Id && other.Id != task.Id)
                      select new ExecutionRows(request, task, batch, dispatch, slot)).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private Task<bool> SourceIsEnabledAsync(FluxKnowledgeDbContext db, MediaTranscriptionGpuRequestEntity r, CancellationToken ct) =>
        BindingIsAvailableAsync(db, r.PipelineRecordId, r.SourceRevision, r.RetainedSourceRevisionId, r.ContentSha256, true, ct);

    private static IQueryable<JobEntity> Jobs(FluxKnowledgeDbContext db, MediaTranscriptionGpuRequestEntity request) =>
        db.Jobs.Where(job => job.Id == request.ParentJobId && job.PipelineRecordId == request.PipelineRecordId &&
            job.SourceRevision == request.SourceRevision && job.Stage == (int)PipelineStage.Extract &&
            job.Operation == MediaTranscriptionGpuContracts.Operation);

    private static bool OwnsReservation(ExecutionRows rows) =>
        rows.Slot.ActiveBatchId == rows.Batch.Id && rows.Slot.OwnerKey == rows.Batch.OwnerKey &&
        rows.Slot.State is (int)GpuCapacitySlotState.Reserved or (int)GpuCapacitySlotState.Uncertain;
    private static bool MatchesHandle(MediaTranscriptionGpuRequestEntity request, GpuExecutorBatchHandle handle) =>
        request.ExecutionBatchId == handle.BatchId && request.ExecutionDispatchId == handle.DispatchId &&
        request.ExecutionAdmissionGeneration == handle.AdmissionGeneration;
    private static bool MatchesSupervisor(MediaTranscriptionGpuRequestEntity r, DocumentOcrSupervisorIdentity s) =>
        r.SupervisorInstanceId == s.InstanceId && r.SupervisorProcessId == s.Process.ProcessId &&
        r.SupervisorProcessStartedAtUtc == s.Process.StartedAtUtc && r.MachineFingerprint == s.Process.MachineFingerprint;
    private static bool MatchesProcess(MediaTranscriptionGpuRequestEntity r, DocumentOcrProcessIdentity p) =>
        r.NativeProcessId == p.ProcessId && r.NativeProcessStartedAtUtc == p.StartedAtUtc && r.MachineFingerprint == p.MachineFingerprint;
    private static DocumentOcrExecutionLease Lease(MediaTranscriptionGpuRequestEntity r, GpuExecutorBatchHandle h) =>
        new(h, r.MiniTaskId, r.ExecutionInstanceId!.Value, new(r.SupervisorInstanceId!.Value,
            new(r.SupervisorProcessId!.Value, r.SupervisorProcessStartedAtUtc!.Value, r.MachineFingerprint!)),
            r.ClaimedAtUtc!.Value, r.DeadlineAtUtc!.Value);
    private static Guid OperationId(string fingerprint) => new(Convert.FromHexString(fingerprint).AsSpan(0, 16));
    private static DocumentOcrLifecycleResult Accepted(string code, DocumentOcrExecutionLease? lease = null, bool replayed = false) =>
        new(replayed ? DocumentOcrLifecycleDisposition.AlreadyApplied : DocumentOcrLifecycleDisposition.Applied,
            "media-transcription-" + code, lease);
    private static DocumentOcrLifecycleResult Stale() => Refused(DocumentOcrLifecycleDisposition.StaleOwner, "stale-owner");
    private static DocumentOcrLifecycleResult Refused(DocumentOcrLifecycleDisposition disposition, string code) =>
        new(disposition, "media-transcription-" + code);
    private sealed record ExecutionRows(MediaTranscriptionGpuRequestEntity Request, GpuMiniTaskEntity Task, GpuBatchEntity Batch,
        GpuExecutorDispatchEntity Dispatch, GpuCapacitySlotEntity Slot);
}
