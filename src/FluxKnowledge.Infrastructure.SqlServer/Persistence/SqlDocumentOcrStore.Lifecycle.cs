using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlDocumentOcrStore
{
    public ValueTask<DocumentOcrLifecycleResult> TryClaimExecutionAsync(GpuExecutorBatchHandle handle,
        Guid miniTaskId, Guid executionInstanceId, DocumentOcrSupervisorIdentity supervisor, CancellationToken ct)
    {
        handle.Validate(); supervisor.Validate();
        if (executionInstanceId == Guid.Empty) throw new ArgumentException("document-ocr-execution-instance-invalid");
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, handle, miniTaskId, ct).ConfigureAwait(false);
            if (rows is null) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var (request, task, batch, dispatch, slot) = rows;
            if (request.ExecutionInstanceId is not null)
                return !IsAbandonedResidentClaim(request) && request.ExecutionInstanceId == executionInstanceId && MatchesSupervisor(request, supervisor) &&
                    request.ExecutionDispatchId == handle.DispatchId && request.ExecutionBatchId == handle.BatchId &&
                    request.ExecutionAdmissionGeneration == handle.AdmissionGeneration
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-claim-replayed", ToLease(request, handle))
                    : Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (dispatch.State != (int)GpuExecutorDispatchState.PendingDelivery || !OwnsSlot(rows) ||
                slot.State != (int)GpuCapacitySlotState.Reserved || task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                batch.State != (int)GpuBatchState.Active)
                return Refused(DocumentOcrLifecycleDisposition.LegacyOwnerUnproven);
            var resident = await context.GpuModelResidencies.SingleOrDefaultAsync(row => row.SlotKey == handle.CapacitySlotKey &&
                row.BorrowedDispatchId == handle.DispatchId && row.State == (int)GpuModelResidencyState.Borrowed, ct).ConfigureAwait(false);
            if (resident is not null && (resident.BorrowedExecutionInstanceId != executionInstanceId ||
                SqlGpuModelResidencyStore.ReadFence(resident).Owner != supervisor.Process))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!await SourceIsEnabledAsync(context, request, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.SourceWithdrawn);
            RecordExecutionClaim(rows, handle, executionInstanceId, supervisor, _timeProvider.GetUtcNow());
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-claimed", ToLease(request, handle));
        }, ct);
    }

    private static void RecordExecutionClaim(OcrLifecycleRows rows, GpuExecutorBatchHandle handle, Guid executionInstanceId,
        DocumentOcrSupervisorIdentity supervisor, DateTimeOffset now)
    {
        var request = rows.Request;
        var dispatch = rows.Dispatch;
        request.ExecutionInstanceId = executionInstanceId;
        request.ExecutionDispatchId = handle.DispatchId; request.ExecutionBatchId = handle.BatchId;
        request.ExecutionAdmissionGeneration = handle.AdmissionGeneration;
        request.ExecutionMachineFingerprint = supervisor.Process.MachineFingerprint;
        request.SupervisorInstanceId = supervisor.InstanceId; request.SupervisorProcessId = supervisor.Process.ProcessId;
        request.SupervisorProcessStartedAtUtc = supervisor.Process.StartedAtUtc;
        request.ExecutionClaimedAtUtc = now; request.ExecutionDeadlineAtUtc = now.AddMinutes(20);
        request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Claimed;
        dispatch.State = (int)GpuExecutorDispatchState.Acknowledged;
        dispatch.AcknowledgedAtUtc = now; dispatch.UpdatedAtUtc = now;
    }

    public ValueTask<DocumentOcrLifecycleResult> PermitNativeStartAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity process, CancellationToken cancellationToken) =>
        PermitNativeStartCoreAsync(lease, process, null, cancellationToken);

    internal ValueTask<DocumentOcrLifecycleResult> PermitResidentNativeStartAsync(DocumentOcrExecutionLease lease,
        GpuResidentModelFence borrow, DocumentOcrProcessIdentity process, CancellationToken ct)
    {
        borrow.Validate();
        return PermitNativeStartCoreAsync(lease, process, borrow, ct);
    }

    private ValueTask<DocumentOcrLifecycleResult> PermitNativeStartCoreAsync(DocumentOcrExecutionLease lease,
        DocumentOcrProcessIdentity process, GpuResidentModelFence? borrow, CancellationToken cancellationToken)
    {
        lease.Validate(); process.Validate();
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, lease.Handle, lease.MiniTaskId, cancellationToken).ConfigureAwait(false);
            if (rows is null || !MatchesLease(rows.Request, lease) || !OwnsSlot(rows) ||
                process.MachineFingerprint != lease.Supervisor.Process.MachineFingerprint)
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (borrow is null
                ? await SqlGpuModelResidencyStore.HasLiveResidencyAsync(context, lease.Handle.CapacitySlotKey, cancellationToken).ConfigureAwait(false)
                : !ActiveResidentReservation(rows) || !MatchesResidentHelper(lease, borrow, process) ||
                  !await MatchesActiveBorrowAsync(context, borrow, lease, cancellationToken).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            var replay = request.ExecutionPhase == (int)DocumentOcrExecutionPhase.NativeStartPermitted && MatchesChild(request, process);
            if (replay && borrow is null)
                return new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-native-start-permitted", lease);
            if ((!replay && request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Claimed) ||
                _timeProvider.GetUtcNow() >= lease.DeadlineAtUtc || rows.Slot.State != (int)GpuCapacitySlotState.Reserved)
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!await SourceIsEnabledAsync(context, request, cancellationToken).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.SourceWithdrawn);
            if (replay)
                return new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-native-start-permitted", lease);
            request.NativeProcessId = process.ProcessId; request.NativeProcessStartedAtUtc = process.StartedAtUtc;
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.NativeStartPermitted;
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-native-start-permitted", lease);
        }, cancellationToken);
    }

    public ValueTask<DocumentOcrLifecycleResult> HeartbeatAsync(DocumentOcrExecutionLease lease, CancellationToken ct)
    {
        lease.Validate();
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, lease.Handle, lease.MiniTaskId, ct).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (rows is null || !MatchesLease(rows.Request, lease) || !OwnsSlot(rows) || now >= lease.DeadlineAtUtc ||
                rows.Request.ExecutionPhase is not ((int)DocumentOcrExecutionPhase.Claimed or (int)DocumentOcrExecutionPhase.NativeStartPermitted) ||
                rows.Slot.State != (int)GpuCapacitySlotState.Reserved || rows.Batch.State != (int)GpuBatchState.Active ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.Acknowledged)
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            rows.Slot.LastHeartbeatAtUtc = now; rows.Slot.UpdatedAtUtc = now;
            rows.Batch.LastHeartbeatAtUtc = now; rows.Batch.UpdatedAtUtc = now;
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-heartbeat", lease);
        }, ct);
    }

    public ValueTask<DocumentOcrLifecycleResult> ConfirmCleanupAsync(DocumentOcrExecutionLease lease,
        DocumentOcrCleanupProof proof, CancellationToken cancellationToken)
    {
        lease.Validate();
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, lease.Handle, lease.MiniTaskId, cancellationToken).ConfigureAwait(false);
            if (rows is null || !MatchesLease(rows.Request, lease) || proof.ExecutionInstanceId != lease.ExecutionInstanceId)
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            if (proof.Kind == DocumentOcrCleanupKind.StoppedMaintenance || !Enum.IsDefined(proof.Kind) ||
                proof.ObservedAtUtc.Offset != TimeSpan.Zero || proof.ObservedAtUtc < lease.ClaimedAtUtc ||
                proof.ObservedAtUtc > _timeProvider.GetUtcNow().AddSeconds(5) || proof.MaintenanceReceiptSha256 is not null)
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            var validKind = proof.Kind switch
            {
                DocumentOcrCleanupKind.VerifiedUnstarted => request.NativeProcessId is null && proof.Process is null,
                DocumentOcrCleanupKind.OwnedJobEmpty => proof.Process is not null && MatchesChild(request, proof.Process),
                _ => false
            };
            if (!validKind) return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            if (HasCleanupProof(request, lease.Handle, lease.MiniTaskId))
                return request.CleanupProofKind == (int)proof.Kind
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-cleanup-confirmed", lease)
                    : Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            if (await SqlGpuModelResidencyStore.HasLiveResidencyAsync(context, lease.Handle.CapacitySlotKey, cancellationToken).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            if (!OwnsSlot(rows) || request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Blocked &&
                request.ExecutionPhase != (int)(proof.Kind == DocumentOcrCleanupKind.VerifiedUnstarted
                    ? DocumentOcrExecutionPhase.Claimed : DocumentOcrExecutionPhase.NativeStartPermitted))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            request.NativeCleanupConfirmedAtUtc = proof.ObservedAtUtc; request.CleanupProofKind = (int)proof.Kind;
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.CleanupConfirmed;
            RecordCleanupEvidence(context, lease, "ocr-owned-job-v1",
                Fingerprint($"{lease.ExecutionInstanceId:N}|{proof.Kind}|{proof.ObservedAtUtc:O}"), proof.ObservedAtUtc);
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-cleanup-confirmed", lease);
        }, cancellationToken);
    }

    public ValueTask<DocumentOcrLifecycleResult> SettleCompletionAsync(GpuExecutorBatchHandle handle,
        Guid miniTaskId, byte[] expectedDigest, CancellationToken ct)
    {
        handle.Validate();
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, handle, miniTaskId, ct).ConfigureAwait(false);
            if (rows is null) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            if (request.ExecutionInstanceId is not { } executionId)
                return Refused(DocumentOcrLifecycleDisposition.LegacyOwnerUnproven);
            var operation = PhaseOperation(handle, executionId, "settle");
            var fingerprint = Fingerprint($"{miniTaskId:N}|{Convert.ToHexString(expectedDigest)}");
            var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(r => r.OperationId == operation, ct).ConfigureAwait(false);
            if (receipt is not null)
            {
                if (receipt.OperationKind == DetachedSettlementKind)
                {
                    var detached = await ReadDetachmentReceiptAsync(context, rows, handle, ct).ConfigureAwait(false);
                    if (detached is null || !receipt.Committed) return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
                }
                return receipt.Accepted && receipt.RequestFingerprint == fingerprint
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-settled")
                    : Refused(DocumentOcrLifecycleDisposition.InvalidResult);
            }
            if (!HasCleanupProof(request, handle, miniTaskId) ||
                await SqlGpuModelResidencyStore.HasLiveResidencyAsync(context, handle.CapacitySlotKey, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            if (!OwnsSlot(rows) || rows.Slot.State is not ((int)GpuCapacitySlotState.Reserved or (int)GpuCapacitySlotState.Uncertain) ||
                rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active || rows.Dispatch.State == (int)GpuExecutorDispatchState.Terminal)
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!ValidStoredResult(request, expectedDigest)) return Refused(DocumentOcrLifecycleDisposition.InvalidResult);
            var now = _timeProvider.GetUtcNow();
            if (!await EnsureResultReceiptAsync(context, rows, handle, executionId, expectedDigest, now, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.InvalidResult);
            CompleteOcrRows(rows, now);
            rows.Slot.State = (int)GpuCapacitySlotState.Available; rows.Slot.ActiveBatchId = null;
            rows.Slot.OwnerKey = null; rows.Slot.LastHeartbeatAtUtc = now; rows.Slot.UpdatedAtUtc = now;
            const GpuSchedulerWakeReason reasons = GpuSchedulerWakeReason.CapacityReleased | GpuSchedulerWakeReason.Reconciliation;
            await SqlGpuSchedulerStore.RecordWakeAsync(context, reasons, now, ct).ConfigureAwait(false);
            RecordSettlementReceipt(context, rows, handle, operation, "ocr-settlement", fingerprint, reasons, now);
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-settled");
        }, ct);
    }

    internal static bool HasCleanupProof(DocumentOcrRequestEntity request, GpuExecutorBatchHandle handle, Guid miniTaskId) =>
        request.MiniTaskId == miniTaskId && request.ExecutionInstanceId is not null && request.ExecutionDispatchId == handle.DispatchId &&
        request.ExecutionBatchId == handle.BatchId && request.ExecutionAdmissionGeneration == handle.AdmissionGeneration &&
        request.NativeCleanupConfirmedAtUtc is not null && request.ExecutionPhase is
            (int)DocumentOcrExecutionPhase.CleanupConfirmed or (int)DocumentOcrExecutionPhase.Settled &&
        (request.CleanupProofKind == (int)DocumentOcrCleanupKind.VerifiedUnstarted && request.NativeProcessId is null ||
         request.CleanupProofKind == (int)DocumentOcrCleanupKind.OwnedJobEmpty && request.NativeProcessId is not null && request.NativeProcessStartedAtUtc is not null ||
         request.CleanupProofKind == (int)DocumentOcrCleanupKind.StoppedMaintenance && IsCanonicalSha256(request.PhysicalCleanupEvidenceSha256 ?? ""));

    public ValueTask<DocumentOcrLifecycleResult> RecordBlockedAsync(GpuExecutorBatchHandle handle,
        Guid miniTaskId, string reason, CancellationToken ct)
    {
        ValidateReason(reason);
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, handle, miniTaskId, ct).ConfigureAwait(false);
            if (rows is null) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            if (request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled)
                return new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-settled");
            var now = _timeProvider.GetUtcNow();
            request.RecoveryStartedAtUtc ??= now;
            if (request.RecoveryReason != reason)
            {
                request.RecoveryReason = reason; request.RecoveryUpdatedAtUtc = now;
            }
            if (request.NativeCleanupConfirmedAtUtc is null && now >= request.RecoveryStartedAtUtc.Value.AddMinutes(2) &&
                request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Blocked)
            {
                request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Blocked;
                request.RecoveryUpdatedAtUtc = now;
            }
            return new(DocumentOcrLifecycleDisposition.Blocked, reason);
        }, ct);
    }

    public async ValueTask<IReadOnlyList<DocumentOcrRecoveryCandidate>> ReadRecoveryCandidatesAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var enabled = from r in context.DocumentOcrRequests.AsNoTracking()
            join record in context.PipelineRecords.AsNoTracking() on r.PipelineRecordId equals record.Id
            join revision in context.SourceRevisions.AsNoTracking() on record.SourceRevisionId equals revision.Id
            join root in context.SourceRootConfigurations.AsNoTracking() on revision.SourceRootId equals root.Id
            where record.Revision == r.SourceRevision && !record.IsDeleted && record.SourceRevisionId == r.RetainedSourceRevisionId &&
                EF.Functions.Collate(revision.ContentSha256, "Latin1_General_100_BIN2") == r.ContentSha256 &&
                root.State == (int)FluxKnowledge.Domain.Sources.SourceRootState.Enabled
            select r.MiniTaskId;
        var rows = await (from request in context.DocumentOcrRequests.AsNoTracking()
            join task in context.GpuMiniTasks.AsNoTracking() on request.MiniTaskId equals task.Id
            join dispatch in context.GpuExecutorDispatches.AsNoTracking() on task.BatchId equals dispatch.BatchId
            join slot in context.GpuCapacitySlots.AsNoTracking() on dispatch.CapacitySlotKey equals slot.SlotKey
            where dispatch.ExecutorKey == PaddleOcrVlmRuntimeContract.ExecutorKey &&
                (slot.ActiveBatchId == dispatch.BatchId || request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled &&
                    request.State == (int)DocumentOcrRequestState.ResultStored && task.ExecutionState == (int)GpuMiniTaskExecutionState.Completed &&
                    enabled.Contains(request.MiniTaskId))
            orderby slot.ActiveBatchId == dispatch.BatchId ? 0 : 1, request.UpdatedAtUtc, request.MiniTaskId
            select new { Request = request, Dispatch = dispatch }).Take(32).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length == 0) return [];
        var dispatchIds = rows.Select(row => row.Dispatch.DispatchId).ToArray();
        var detached = await context.GpuModelDetachmentReceipts.AsNoTracking()
            .Where(receipt => dispatchIds.Contains(receipt.DispatchId))
            .ToDictionaryAsync(receipt => receipt.DispatchId, ct).ConfigureAwait(false);
        var borrowed = await context.GpuModelResidencies.AsNoTracking()
            .Where(resident => resident.State == (int)GpuModelResidencyState.Borrowed &&
                resident.BorrowedDispatchId.HasValue && dispatchIds.Contains(resident.BorrowedDispatchId.Value))
            .ToDictionaryAsync(resident => resident.BorrowedDispatchId!.Value, ct).ConfigureAwait(false);
        return rows.Select(row =>
        {
            var r = row.Request; var d = row.Dispatch;
            var handle = new GpuExecutorBatchHandle(d.BatchId, d.CapacitySlotKey, d.ExecutorKey, d.AdmissionGeneration, d.DispatchId);
            var residentBorrow = borrowed.TryGetValue(d.DispatchId, out var resident) &&
                resident.BorrowedExecutionInstanceId == (r.ExecutionInstanceId ?? d.DispatchId) && resident.SlotKey == d.CapacitySlotKey
                ? SqlGpuModelResidencyStore.ReadFence(resident) : null;
            var detachedResult = detached.TryGetValue(d.DispatchId, out var receipt) && MatchesDetachmentReceipt(receipt, r, handle);
            return new DocumentOcrRecoveryCandidate(handle, r.MiniTaskId,
                r.ExecutionInstanceId is null ? null : ToLease(r, handle),
                r.NativeProcessId is null ? null : new(r.NativeProcessId.Value, r.NativeProcessStartedAtUtc!.Value, r.ExecutionMachineFingerprint!),
                (DocumentOcrExecutionPhase)r.ExecutionPhase, r.NativeCleanupConfirmedAtUtc,
                r.State >= (int)DocumentOcrRequestState.ResultStored, r.RecoveryStartedAtUtc, r.RecoveryReason, detachedResult, residentBorrow);
        }).ToArray();
    }

    public ValueTask<DocumentOcrLifecycleResult> ReleaseWithdrawnAsync(DocumentOcrExecutionLease lease, CancellationToken ct) =>
        ReleaseWithoutResultAsync(lease, "document-ocr-source-withdrawn", requireWithdrawnSource: true, ct);

    public ValueTask<DocumentOcrLifecycleResult> ReleaseUnpublishableAsync(DocumentOcrExecutionLease lease,
        string reason, CancellationToken ct)
    {
        ValidateReason(reason);
        return ReleaseWithoutResultAsync(lease, reason, requireWithdrawnSource: false, ct);
    }

    public ValueTask<DocumentOcrLifecycleResult> ReleaseCorruptResultAsync(GpuExecutorBatchHandle handle,
        Guid miniTaskId, byte[] expectedDigest, CancellationToken ct) => MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, handle, miniTaskId, ct).ConfigureAwait(false);
            if (rows is null || !OwnsSlot(rows)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!HasCleanupProof(rows.Request, handle, miniTaskId)) return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            // A stale caller's digest is not evidence of corruption. Preserve the stored identity.
            if (rows.Request.ResultDigest is not { Length: 32 } digest || expectedDigest.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(digest, expectedDigest) || ValidStoredResult(rows.Request, digest))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            return await ReleaseProvedCapacityAsync(context, rows, "document-ocr-stored-result-invalid", ct).ConfigureAwait(false);
        }, ct);

    private ValueTask<DocumentOcrLifecycleResult> ReleaseWithoutResultAsync(DocumentOcrExecutionLease lease,
        string reason, bool requireWithdrawnSource, CancellationToken ct) =>
        MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, lease.Handle, lease.MiniTaskId, ct).ConfigureAwait(false);
            if (rows is null || !MatchesLease(rows.Request, lease)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (rows.Request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled)
                return new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-settled");
            if (!HasCleanupProof(rows.Request, lease.Handle, lease.MiniTaskId)) return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            if (!OwnsSlot(rows)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var durableSource = await HasDurableSourceBindingAsync(context, rows.Request, ct).ConfigureAwait(false);
            // A process-local admission miss or unconfirmed removal is not
            // durable withdrawal, nor grounds for the fallback no-result release.
            if (requireWithdrawnSource && durableSource || !requireWithdrawnSource && durableSource &&
                !await SourceIsEnabledAsync(context, rows.Request, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (rows.Request.ResultJson is not null || rows.Request.ResultDigest is not null ||
                rows.Request.State >= (int)DocumentOcrRequestState.ResultStored)
                return Refused(DocumentOcrLifecycleDisposition.ResultAvailable);
            return await ReleaseProvedCapacityAsync(context, rows, reason, ct).ConfigureAwait(false);
        }, ct);

    private async Task<DocumentOcrLifecycleResult> ReleaseProvedCapacityAsync(FluxKnowledgeDbContext context,
        OcrLifecycleRows rows, string reason, CancellationToken ct)
    {
            if (await SqlGpuModelResidencyStore.HasLiveResidencyAsync(context, rows.Slot.SlotKey, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            var now = _timeProvider.GetUtcNow();
            rows.Task.ExecutionState = (int)GpuMiniTaskExecutionState.OutcomeUncertain;
            rows.Batch.State = (int)GpuBatchState.Released; rows.Batch.UpdatedAtUtc = now;
            rows.Dispatch.State = (int)GpuExecutorDispatchState.Terminal; rows.Dispatch.UpdatedAtUtc = now;
            rows.Slot.State = (int)GpuCapacitySlotState.Available; rows.Slot.ActiveBatchId = null; rows.Slot.OwnerKey = null; rows.Slot.UpdatedAtUtc = now;
            rows.Request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Settled;
            rows.Request.RecoveryReason = reason; rows.Request.RecoveryUpdatedAtUtc = now;
            await SqlGpuSchedulerStore.RecordWakeAsync(context, GpuSchedulerWakeReason.CapacityReleased, now, ct).ConfigureAwait(false);
            return new(DocumentOcrLifecycleDisposition.Applied, reason);
    }

    private ValueTask<DocumentOcrLifecycleResult> MutateLifecycleAsync(
        Func<FluxKnowledgeDbContext, Task<DocumentOcrLifecycleResult>> action, CancellationToken ct) =>
        MutateLifecycleAsync(action, static result => result, ct);

    private async ValueTask<TResult> MutateLifecycleAsync<TResult>(
        Func<FluxKnowledgeDbContext, Task<TResult>> action, Func<TResult, DocumentOcrLifecycleResult> lifecycle, CancellationToken ct)
    {
        await using var execution = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, ct).ConfigureAwait(false);
            await SqlGpuSchedulerStore.AcquireMutationLockAsync(context, transaction.GetDbTransaction(), ct).ConfigureAwait(false);
            var result = await action(context).ConfigureAwait(false);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (afterLifecycleCommitted is not null) await afterLifecycleCommitted(lifecycle(result), ct).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private static async Task<OcrLifecycleRows?> ReadLifecycleAsync(FluxKnowledgeDbContext context,
        GpuExecutorBatchHandle handle, Guid miniTaskId, CancellationToken ct)
    {
        if (handle.ExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey || handle.CapacitySlotKey != PaddleOcrVlmRuntimeContract.CapacitySlotKey) return null;
        var request = await context.DocumentOcrRequests.SingleOrDefaultAsync(r => r.MiniTaskId == miniTaskId, ct).ConfigureAwait(false);
        var task = await context.GpuMiniTasks.SingleOrDefaultAsync(t => t.Id == miniTaskId && t.BatchId == handle.BatchId && t.AdmissionGeneration == handle.AdmissionGeneration, ct).ConfigureAwait(false);
        var dispatch = await context.GpuExecutorDispatches.SingleOrDefaultAsync(d => d.DispatchId == handle.DispatchId && d.BatchId == handle.BatchId &&
            d.CapacitySlotKey == handle.CapacitySlotKey && d.ExecutorKey == handle.ExecutorKey && d.AdmissionGeneration == handle.AdmissionGeneration, ct).ConfigureAwait(false);
        var batch = await context.GpuBatches.SingleOrDefaultAsync(b => b.Id == handle.BatchId && b.AdmissionGeneration == handle.AdmissionGeneration, ct).ConfigureAwait(false);
        var slot = await context.GpuCapacitySlots.SingleOrDefaultAsync(s => s.SlotKey == handle.CapacitySlotKey, ct).ConfigureAwait(false);
        return request is null || task is null || dispatch is null || batch is null || slot is null || batch.ItemCount != 1 ||
            request.ParentJobId != task.ParentJobId || request.SourceRevision != task.SourceRevision ||
            request.ModelRuntimeKey != PaddleOcrVlmRuntimeContract.ModelRuntimeKey || request.SettingsFingerprint != PaddleOcrVlmRuntimeContract.SettingsFingerprint ||
            await context.GpuMiniTasks.CountAsync(t => t.BatchId == handle.BatchId, ct).ConfigureAwait(false) != 1
            ? null : new(request, task, batch, dispatch, slot);
    }

    private static bool OwnsSlot(OcrLifecycleRows rows) => rows.Slot.ActiveBatchId == rows.Batch.Id &&
        rows.Slot.OwnerKey == rows.Dispatch.OwnerKey && rows.Batch.OwnerKey == rows.Dispatch.OwnerKey && rows.Batch.CapacitySlotKey == rows.Slot.SlotKey;
    private static bool MatchesSupervisor(DocumentOcrRequestEntity r, DocumentOcrSupervisorIdentity s) =>
        r.SupervisorInstanceId == s.InstanceId && r.SupervisorProcessId == s.Process.ProcessId &&
        r.SupervisorProcessStartedAtUtc == s.Process.StartedAtUtc && r.ExecutionMachineFingerprint == s.Process.MachineFingerprint;
    private static bool MatchesChild(DocumentOcrRequestEntity r, DocumentOcrProcessIdentity p) =>
        r.NativeProcessId == p.ProcessId && r.NativeProcessStartedAtUtc == p.StartedAtUtc && r.ExecutionMachineFingerprint == p.MachineFingerprint;
    private static bool MatchesLease(DocumentOcrRequestEntity r, DocumentOcrExecutionLease l) => r.MiniTaskId == l.MiniTaskId &&
        r.ExecutionInstanceId == l.ExecutionInstanceId && r.ExecutionDispatchId == l.Handle.DispatchId && r.ExecutionBatchId == l.Handle.BatchId &&
        r.ExecutionAdmissionGeneration == l.Handle.AdmissionGeneration && r.ExecutionClaimedAtUtc == l.ClaimedAtUtc &&
        r.ExecutionDeadlineAtUtc == l.DeadlineAtUtc && MatchesSupervisor(r, l.Supervisor);
    private static DocumentOcrExecutionLease ToLease(DocumentOcrRequestEntity r, GpuExecutorBatchHandle h) => new(h, r.MiniTaskId,
        r.ExecutionInstanceId!.Value, new(r.SupervisorInstanceId!.Value, new(r.SupervisorProcessId!.Value,
            r.SupervisorProcessStartedAtUtc!.Value, r.ExecutionMachineFingerprint!)), r.ExecutionClaimedAtUtc!.Value, r.ExecutionDeadlineAtUtc!.Value);
    private static DocumentOcrLifecycleResult Refused(DocumentOcrLifecycleDisposition disposition) => new(disposition, $"document-ocr-{disposition.ToString().ToLowerInvariant()}");
    private static string Fingerprint(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    internal static Guid PhaseOperation(GpuExecutorBatchHandle h, Guid executionId, string phase) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"ocr-lifecycle-v1|{h.DispatchId:N}|{h.BatchId:N}|{h.AdmissionGeneration}|{executionId:N}|{phase}")).AsSpan(0, 16));
    private static bool ValidStoredResult(DocumentOcrRequestEntity request, byte[] expected)
    {
        try
        {
            return request.ResultJson is not null && request.ResultDigest is { Length: 32 } && expected.Length == 32 &&
                CryptographicOperations.FixedTimeEquals(expected, request.ResultDigest) &&
                CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(StrictUtf8.GetBytes(request.ResultJson))) &&
                JsonSerializer.Deserialize<DocumentOcrExecutionResult>(request.ResultJson, JsonOptions) is { } result && IsResultShapeValid(result);
        }
        catch (Exception error) when (error is JsonException or EncoderFallbackException) { return false; }
    }
    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > 128 || reason.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new ArgumentException("document-ocr-recovery-reason-invalid");
    }
    private sealed record OcrLifecycleRows(DocumentOcrRequestEntity Request, GpuMiniTaskEntity Task,
        GpuBatchEntity Batch, GpuExecutorDispatchEntity Dispatch, GpuCapacitySlotEntity Slot);
}

public sealed record DocumentOcrRecoveryCandidate(GpuExecutorBatchHandle Handle, Guid MiniTaskId,
    DocumentOcrExecutionLease? Lease, DocumentOcrProcessIdentity? Process, DocumentOcrExecutionPhase Phase,
    DateTimeOffset? CleanupConfirmedAtUtc, bool HasStoredResult, DateTimeOffset? RecoveryStartedAtUtc, string? Reason,
    bool Detached = false, GpuResidentModelFence? ResidentBorrow = null);
