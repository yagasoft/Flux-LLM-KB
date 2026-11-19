using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlDocumentOcrStore
{
    private const string DetachedSettlementKind = "ocr-detached-settlement";
    private const string ResidentCleanupVerifier = "ocr-resident-job-empty-v1";
    internal static readonly GpuResidentModelKey ResidentModel = new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
        PaddleOcrVlmRuntimeContract.SettingsFingerprint, "ocr");

    internal ValueTask<GpuResidentModelFence?> ReadResidentBorrowAsync(GpuExecutorBatchHandle handle, CancellationToken ct) =>
        new SqlGpuModelResidencyStore(contextFactory, _timeProvider).ReadBorrowAsync(handle, ct);

    internal ValueTask<DocumentOcrLifecycleResult> ClaimAbandonedResidentAsync(GpuExecutorBatchHandle handle,
        GpuResidentModelFence borrow, DocumentOcrSupervisorIdentity supervisor, bool ownerExited, CancellationToken ct)
    {
        handle.Validate();
        borrow.Validate();
        supervisor.Validate();
        if (borrow.SlotKey != handle.CapacitySlotKey || borrow.Helper is null ||
            borrow.Owner.MachineFingerprint != supervisor.Process.MachineFingerprint ||
            !ownerExited && borrow.Owner != supervisor.Process)
            return ValueTask.FromResult(Refused(DocumentOcrLifecycleDisposition.StaleOwner));

        return MutateLifecycleAsync(async context =>
        {
            var taskId = await context.GpuMiniTasks.Where(task => task.BatchId == handle.BatchId &&
                    task.AdmissionGeneration == handle.AdmissionGeneration)
                .Select(task => (Guid?)task.Id).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var rows = taskId is { } id
                ? await ReadLifecycleAsync(context, handle, id, ct).ConfigureAwait(false) : null;
            if (rows is null || rows.Request.ExecutionInstanceId is not null || !OwnsSlot(rows) ||
                rows.Slot.State != (int)GpuCapacitySlotState.Reserved || rows.Dispatch.State != (int)GpuExecutorDispatchState.PendingDelivery ||
                rows.Batch.State != (int)GpuBatchState.Active || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active)
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!ownerExited && await SourceIsEnabledAsync(context, rows.Request, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var now = _timeProvider.GetUtcNow();
            var lease = new DocumentOcrExecutionLease(handle, rows.Task.Id, handle.DispatchId, supervisor, now, now.AddMinutes(20));
            if (!await MatchesActiveBorrowAsync(context, borrow, lease, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.StaleOwner);

            // This claim fences redelivery while the exact preassigned helper is stopped.
            // Its Blocked phase provides no native start permission.
            RecordExecutionClaim(rows, handle, lease.ExecutionInstanceId, supervisor, now);
            rows.Request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Blocked;
            rows.Request.RecoveryReason = ownerExited ? "document-ocr-resident-owner-exited" : "document-ocr-resident-source-withdrawn";
            rows.Request.RecoveryStartedAtUtc = now;
            rows.Request.RecoveryUpdatedAtUtc = now;
            return new(DocumentOcrLifecycleDisposition.Applied, rows.Request.RecoveryReason, lease);
        }, ct);
    }

    internal ValueTask<DocumentOcrDetachedSettlement> SettleDetachedCompletionAsync(DocumentOcrExecutionLease lease,
        DocumentOcrExecutionResult result, GpuResidentModelFence borrow, DocumentOcrResidentIdleProof idle, CancellationToken ct)
    {
        lease.Validate();
        borrow.Validate();
        idle.Process.Validate();
        if (idle.ResidencyId != borrow.ResidencyId || idle.ExecutionInstanceId != lease.ExecutionInstanceId ||
            idle.ModelKey != ResidentModel.ModelKey || !MatchesResidentHelper(lease, borrow, idle.Process) ||
            idle.ObservedAtUtc >= lease.DeadlineAtUtc)
            return ValueTask.FromResult(DetachedRefusal(DocumentOcrLifecycleDisposition.CleanupUnproven));

        var (json, digest) = EncodeResult(result);
        var handle = lease.Handle;
        var operation = PhaseOperation(handle, lease.ExecutionInstanceId, "settle");
        var fingerprint = Fingerprint($"{lease.MiniTaskId:N}|{Convert.ToHexString(digest)}");
        return MutateLifecycleAsync<DocumentOcrDetachedSettlement>(async context =>
        {
            var rows = await ReadLifecycleAsync(context, handle, lease.MiniTaskId, ct).ConfigureAwait(false);
            if (rows is null || !MatchesLease(rows.Request, lease) || !MatchesChild(rows.Request, idle.Process))
                return DetachedRefusal(DocumentOcrLifecycleDisposition.StaleOwner);
            var now = _timeProvider.GetUtcNow();
            if (!ValidResidentObservation(lease, idle.Process, idle.ObservedAtUtc, now))
                return DetachedRefusal(DocumentOcrLifecycleDisposition.CleanupUnproven);

            var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(row => row.OperationId == operation, ct)
                .ConfigureAwait(false);
            if (receipt is not null)
            {
                if (receipt.OperationKind != DetachedSettlementKind || !receipt.Accepted || !receipt.Committed ||
                    receipt.RequestFingerprint != fingerprint)
                    return DetachedRefusal(DocumentOcrLifecycleDisposition.InvalidResult);
                var detached = await ReadDetachmentReceiptAsync(context, rows, handle, ct).ConfigureAwait(false);
                if (detached is null || detached.ResidencyId != borrow.ResidencyId || detached.Version - 1 != borrow.Version)
                    return DetachedRefusal(DocumentOcrLifecycleDisposition.StaleOwner);

                var expectedIdle = borrow with { Version = detached.Version };
                var resident = await context.GpuModelResidencies.SingleAsync(row => row.ResidencyId == borrow.ResidencyId, ct)
                    .ConfigureAwait(false);
                var currentIdle = resident.State == (int)GpuModelResidencyState.Idle &&
                    SqlGpuModelResidencyStore.ReadFence(resident) == expectedIdle ? expectedIdle : null;
                return new(new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-settled", lease), currentIdle);
            }

            if (!ActiveResidentReservation(rows) || now >= lease.DeadlineAtUtc ||
                rows.Request.ExecutionPhase != (int)DocumentOcrExecutionPhase.NativeStartPermitted ||
                rows.Request.NativeCleanupConfirmedAtUtc is not null ||
                !await MatchesActiveBorrowAsync(context, borrow, lease, ct).ConfigureAwait(false))
                return DetachedRefusal(DocumentOcrLifecycleDisposition.StaleOwner);
            if (!await HasDurableSourceBindingAsync(context, rows.Request, ct).ConfigureAwait(false))
                return DetachedRefusal(DocumentOcrLifecycleDisposition.SourceWithdrawn);
            if (!MatchesRequestedPages(rows.Request, result) ||
                rows.Request.State != (int)DocumentOcrRequestState.Pending && !ValidStoredResult(rows.Request, digest) ||
                !await EnsureResultReceiptAsync(context, rows, handle, lease.ExecutionInstanceId, digest, now, ct).ConfigureAwait(false))
                return DetachedRefusal(DocumentOcrLifecycleDisposition.InvalidResult);

            rows.Request.ResultJson = json;
            rows.Request.ResultDigest = digest;
            rows.Request.State = (int)DocumentOcrRequestState.ResultStored;
            rows.Request.UpdatedAtUtc = now;
            CompleteOcrRows(rows, now);
            var parked = await SqlGpuModelResidencyStore.ParkAfterTerminalAsync(context, borrow, handle,
                lease.ExecutionInstanceId, ResidentModel, now, ct).ConfigureAwait(false);
            if (parked is null)
                throw new InvalidOperationException("document-ocr-resident-settlement-conflict");

            RecordSettlementReceipt(context, rows, handle, operation, DetachedSettlementKind, fingerprint,
                GpuSchedulerWakeReason.Reconciliation, now);
            return new(new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-settled", lease), parked);
        }, static outcome => outcome.Settlement, ct);
    }

    internal ValueTask<DocumentOcrLifecycleResult> ConfirmResidentCleanupAsync(DocumentOcrExecutionLease lease,
        GpuResidentModelFence borrow, DocumentOcrResidentReleaseProof proof, CancellationToken ct)
    {
        lease.Validate();
        borrow.Validate();
        proof.Process.Validate();
        if (proof.ResidencyId != borrow.ResidencyId || proof.ExecutionInstanceId != lease.ExecutionInstanceId ||
            !MatchesResidentProcess(lease, borrow, proof.Process))
            return ValueTask.FromResult(Refused(DocumentOcrLifecycleDisposition.CleanupUnproven));

        var handle = lease.Handle;
        var fingerprint = Fingerprint($"{lease.ExecutionInstanceId:N}|{borrow.ResidencyId:N}|{borrow.Version}|{proof.Process.ProcessId}|{proof.Process.StartedAtUtc:O}");
        return MutateLifecycleAsync(async context =>
        {
            var rows = await ReadLifecycleAsync(context, handle, lease.MiniTaskId, ct).ConfigureAwait(false);
            if (rows is null || !MatchesLease(rows.Request, lease)) return Refused(DocumentOcrLifecycleDisposition.StaleOwner);
            var request = rows.Request;
            var now = _timeProvider.GetUtcNow();
            if (!ValidResidentObservation(lease, proof.Process, proof.ObservedAtUtc, now) ||
                request.NativeProcessId is not null && !MatchesChild(request, proof.Process))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);

            if (HasCleanupProof(request, handle, lease.MiniTaskId))
            {
                var operation = PhaseOperation(handle, lease.ExecutionInstanceId, "cleanup");
                return await context.GpuExecutorEvidence.AnyAsync(row => row.OperationId == operation &&
                        row.VerifierKey == ResidentCleanupVerifier && row.RequestFingerprint == fingerprint, ct).ConfigureAwait(false)
                    ? new(DocumentOcrLifecycleDisposition.AlreadyApplied, "document-ocr-cleanup-confirmed", lease)
                    : Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);
            }

            if (!OwnsSlot(rows) || !IsAbandonedResidentClaim(request) && borrow.Owner != lease.Supervisor.Process ||
                request.ExecutionPhase is not ((int)DocumentOcrExecutionPhase.Claimed or
                    (int)DocumentOcrExecutionPhase.NativeStartPermitted or (int)DocumentOcrExecutionPhase.Blocked) ||
                !await MatchesActiveBorrowAsync(context, borrow, lease, ct).ConfigureAwait(false) ||
                !await SqlGpuModelResidencyStore.ReleaseBorrowedAsync(context, borrow, handle, lease.ExecutionInstanceId,
                    GpuResidentReleaseKind.ContainedJobEmpty, proof.ObservedAtUtc, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven);

            request.NativeCleanupConfirmedAtUtc = proof.ObservedAtUtc;
            request.CleanupProofKind = request.NativeProcessId is null
                ? (int)DocumentOcrCleanupKind.VerifiedUnstarted : (int)DocumentOcrCleanupKind.OwnedJobEmpty;
            request.ExecutionPhase = (int)DocumentOcrExecutionPhase.CleanupConfirmed;
            RecordCleanupEvidence(context, lease, ResidentCleanupVerifier, fingerprint, proof.ObservedAtUtc);
            return new(DocumentOcrLifecycleDisposition.Applied, "document-ocr-cleanup-confirmed", lease);
        }, ct);
    }

    private static bool IsAbandonedResidentClaim(DocumentOcrRequestEntity request) =>
        request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Blocked && request.NativeProcessId is null &&
        request.RecoveryReason is "document-ocr-resident-source-withdrawn" or "document-ocr-resident-owner-exited";

    private static bool ActiveResidentReservation(OcrLifecycleRows rows) => OwnsSlot(rows) &&
        rows.Slot.State == (int)GpuCapacitySlotState.Reserved && rows.Batch.State == (int)GpuBatchState.Active &&
        rows.Task.ExecutionState == (int)GpuMiniTaskExecutionState.Active && rows.Dispatch.State == (int)GpuExecutorDispatchState.Acknowledged &&
        rows.Task.ModelRuntimeKey == ResidentModel.RuntimeKey && rows.Task.SettingsFingerprint == ResidentModel.SettingsFingerprint &&
        rows.Batch.ModelRuntimeKey == ResidentModel.RuntimeKey && rows.Batch.SettingsFingerprint == ResidentModel.SettingsFingerprint;

    private static bool MatchesResidentHelper(DocumentOcrExecutionLease lease, GpuResidentModelFence borrow,
        DocumentOcrProcessIdentity process) =>
        borrow.Owner == lease.Supervisor.Process && MatchesResidentProcess(lease, borrow, process);

    private static bool MatchesResidentProcess(DocumentOcrExecutionLease lease, GpuResidentModelFence borrow,
        DocumentOcrProcessIdentity process) =>
        borrow.SlotKey == lease.Handle.CapacitySlotKey && borrow.Owner.MachineFingerprint == lease.Supervisor.Process.MachineFingerprint &&
        borrow.Helper == new GpuResidentHelperIdentity(borrow.ResidencyId, process.ProcessId, process.StartedAtUtc) &&
        process.MachineFingerprint == borrow.Owner.MachineFingerprint;

    private static bool ValidResidentObservation(DocumentOcrExecutionLease lease, DocumentOcrProcessIdentity process,
        DateTimeOffset observed, DateTimeOffset now) =>
        observed.Offset == TimeSpan.Zero && observed >= lease.ClaimedAtUtc && observed >= process.StartedAtUtc &&
        observed <= now.AddSeconds(5);

    private static async Task<bool> MatchesActiveBorrowAsync(FluxKnowledgeDbContext context, GpuResidentModelFence borrow,
        DocumentOcrExecutionLease lease, CancellationToken ct)
    {
        var resident = await context.GpuModelResidencies.SingleOrDefaultAsync(row => row.ResidencyId == borrow.ResidencyId, ct)
            .ConfigureAwait(false);
        return resident is { State: (int)GpuModelResidencyState.Borrowed } &&
            resident.BorrowedDispatchId == lease.Handle.DispatchId && resident.BorrowedExecutionInstanceId == lease.ExecutionInstanceId &&
            SqlGpuModelResidencyStore.ReadFence(resident) == borrow && SqlGpuModelResidencyStore.ReadModel(resident) == ResidentModel;
    }

    private static bool MatchesDetachmentReceipt(GpuModelDetachmentReceiptEntity receipt, DocumentOcrRequestEntity request,
        GpuExecutorBatchHandle handle) =>
        request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled && request.ExecutionInstanceId == receipt.ExecutionInstanceId &&
        receipt.DispatchId == handle.DispatchId && receipt.BatchId == handle.BatchId &&
        receipt.AdmissionGeneration == handle.AdmissionGeneration && receipt.ExecutorKey == handle.ExecutorKey &&
        request.ExecutionDispatchId == receipt.DispatchId && request.ExecutionBatchId == receipt.BatchId &&
        request.ExecutionAdmissionGeneration == receipt.AdmissionGeneration;

    private static async Task<GpuModelDetachmentReceiptEntity?> ReadDetachmentReceiptAsync(FluxKnowledgeDbContext context,
        OcrLifecycleRows rows, GpuExecutorBatchHandle handle, CancellationToken ct)
    {
        if (rows.Batch.State != (int)GpuBatchState.Completed || rows.Dispatch.State != (int)GpuExecutorDispatchState.Terminal ||
            rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed)
            return null;
        var receipt = await context.GpuModelDetachmentReceipts.SingleOrDefaultAsync(row => row.DispatchId == handle.DispatchId, ct)
            .ConfigureAwait(false);
        return receipt is not null && MatchesDetachmentReceipt(receipt, rows.Request, handle) ? receipt : null;
    }

    private static bool MatchesRequestedPages(DocumentOcrRequestEntity request, DocumentOcrExecutionResult result)
    {
        if (!result.Succeeded) return result.Pages.Count == 0;
        if (result.ReasonCode != "document-ocr-complete" || result.Pages.Count == 0) return false;
        try
        {
            return JsonSerializer.Deserialize<int[]>(request.RequestedPageIndexesJson, JsonOptions) is { } requested &&
                requested.SequenceEqual(result.Pages.Select(page => page.PageIndex).Order());
        }
        catch (JsonException) { return false; }
    }

    private static (string Json, byte[] Digest) EncodeResult(DocumentOcrExecutionResult result)
    {
        ValidateResultShape(result);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (StrictUtf8.GetByteCount(json) > DocumentOcrProvenance.MaximumMetadataUtf8Bytes)
            throw new InvalidOperationException("document-ocr-result-too-large");
        return (json, SHA256.HashData(StrictUtf8.GetBytes(json)));
    }

    private static async Task<bool> EnsureResultReceiptAsync(FluxKnowledgeDbContext context, OcrLifecycleRows rows,
        GpuExecutorBatchHandle handle, Guid executionId, byte[] digest, DateTimeOffset now, CancellationToken ct)
    {
        var miniTaskId = rows.Request.MiniTaskId;
        var receipt = await context.GpuExecutorResultReceipts.SingleOrDefaultAsync(row =>
            row.DispatchId == handle.DispatchId && row.MiniTaskId == miniTaskId, ct).ConfigureAwait(false);
        if (receipt is not null)
            return receipt.OpaqueResultDigest is not null && CryptographicOperations.FixedTimeEquals(receipt.OpaqueResultDigest, digest);

        var operation = PhaseOperation(handle, executionId, "result");
        var fingerprint = SqlGpuSchedulerStore.CreateExecutorResultReceiptFingerprint(new(operation, handle,
            miniTaskId, GpuMiniTaskBoundaryDisposition.Completed, digest, GpuExecutorEvidenceClass.TaskOutcomeConfirmed));
        context.GpuExecutorResultReceipts.Add(new GpuExecutorResultReceiptEntity
        {
            OperationId = operation, DispatchId = handle.DispatchId, BatchId = handle.BatchId, MiniTaskId = miniTaskId,
            ExecutorKey = handle.ExecutorKey, AdmissionGeneration = handle.AdmissionGeneration,
            Disposition = (int)GpuMiniTaskBoundaryDisposition.Completed, EvidenceClass = (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed,
            OpaqueResultDigest = digest.ToArray(), RequestFingerprint = fingerprint, CreatedAtUtc = now
        });
        RecordSettlementReceipt(context, rows, handle, operation, "executor-result-receipt", fingerprint, 0, now);
        return true;
    }

    private static void CompleteOcrRows(OcrLifecycleRows rows, DateTimeOffset now)
    {
        rows.Task.ExecutionState = (int)GpuMiniTaskExecutionState.Completed;
        rows.Batch.State = (int)GpuBatchState.Completed; rows.Batch.UpdatedAtUtc = now;
        rows.Dispatch.State = (int)GpuExecutorDispatchState.Terminal; rows.Dispatch.UpdatedAtUtc = now;
        rows.Request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Settled;
        rows.Request.RecoveryReason = null; rows.Request.RecoveryUpdatedAtUtc = now;
    }

    private static void RecordSettlementReceipt(FluxKnowledgeDbContext context, OcrLifecycleRows rows,
        GpuExecutorBatchHandle handle, Guid operation, string kind, string fingerprint, GpuSchedulerWakeReason reasons, DateTimeOffset now) =>
        context.GpuSchedulerOperationReceipts.Add(new GpuSchedulerOperationReceiptEntity
        {
            OperationId = operation, OperationKind = kind, RequestFingerprint = fingerprint,
            BatchId = handle.BatchId, CapacitySlotKey = handle.CapacitySlotKey, OwnerKey = rows.Dispatch.OwnerKey,
            AdmissionGeneration = handle.AdmissionGeneration, Accepted = true, Committed = true,
            WakeReasons = (int)reasons, CreatedAtUtc = now
        });

    private void RecordCleanupEvidence(FluxKnowledgeDbContext context, DocumentOcrExecutionLease lease,
        string verifier, string fingerprint, DateTimeOffset observed) =>
        context.GpuExecutorEvidence.Add(new GpuExecutorEvidenceEntity
        {
            OperationId = PhaseOperation(lease.Handle, lease.ExecutionInstanceId, "cleanup"),
            DispatchId = lease.Handle.DispatchId, BatchId = lease.Handle.BatchId,
            CapacitySlotKey = lease.Handle.CapacitySlotKey, ExecutorKey = lease.Handle.ExecutorKey,
            AdmissionGeneration = lease.Handle.AdmissionGeneration, EvidenceClass = (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed,
            VerifierKey = verifier, ObservedAtUtc = observed, RequestFingerprint = fingerprint, CreatedAtUtc = _timeProvider.GetUtcNow()
        });

    private static DocumentOcrDetachedSettlement DetachedRefusal(DocumentOcrLifecycleDisposition disposition) =>
        new(Refused(disposition), null);
}

internal sealed record DocumentOcrResidentIdleProof(Guid ResidencyId, Guid ExecutionInstanceId,
    DocumentOcrProcessIdentity Process, string ModelKey, DateTimeOffset ObservedAtUtc);

internal sealed record DocumentOcrResidentReleaseProof(Guid ResidencyId, Guid ExecutionInstanceId,
    DocumentOcrProcessIdentity Process, DateTimeOffset ObservedAtUtc);

internal sealed record DocumentOcrDetachedSettlement(DocumentOcrLifecycleResult Settlement, GpuResidentModelFence? Idle);
