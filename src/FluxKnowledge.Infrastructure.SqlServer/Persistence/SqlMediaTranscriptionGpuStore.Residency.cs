using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlMediaTranscriptionGpuStore
{
    private const string ResidentCleanupVerifier = "media-transcription-resident-job-empty-v1";

    internal async ValueTask<GpuResidentModelFence?> ReadResidentBorrowAsync(DocumentOcrExecutionLease lease, CancellationToken ct)
    {
        var handle = lease.Handle;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.GpuModelResidencies.AsNoTracking().SingleOrDefaultAsync(value =>
            value.SlotKey == handle.CapacitySlotKey && value.BorrowedDispatchId == handle.DispatchId &&
            value.BorrowedExecutionInstanceId == lease.ExecutionInstanceId &&
            value.State == (int)GpuModelResidencyState.Borrowed && value.ReleasedAtUtc == null, ct).ConfigureAwait(false);
        return row is { ModelKey: "general" } && row.RuntimeKey == runtime.ModelRuntimeKey && row.SettingsFingerprint == runtime.SettingsFingerprint
            ? SqlGpuModelResidencyStore.ReadFence(row) : null;
    }

    internal async ValueTask<IReadOnlyList<(GpuExecutorBatchHandle Handle, GpuResidentModelFence Fence)>> ReadUnclaimedResidentsAsync(
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await (from resident in db.GpuModelResidencies.AsNoTracking()
                          join dispatch in db.GpuExecutorDispatches.AsNoTracking() on resident.BorrowedDispatchId equals dispatch.DispatchId
                          join task in db.GpuMiniTasks.AsNoTracking() on dispatch.BatchId equals task.BatchId
                          join request in db.MediaTranscriptionGpuRequests.AsNoTracking() on task.Id equals request.MiniTaskId
                          where resident.State == (int)GpuModelResidencyState.Borrowed && resident.ModelKey == "general" &&
                              resident.BorrowedExecutionInstanceId == dispatch.DispatchId && resident.SlotKey == dispatch.CapacitySlotKey &&
                              resident.RuntimeKey == runtime.ModelRuntimeKey && resident.SettingsFingerprint == runtime.SettingsFingerprint &&
                              dispatch.ExecutorKey == runtime.ExecutorKey && request.ExecutionInstanceId == null
                          orderby request.CreatedAtUtc, dispatch.DispatchId
                          select new { resident, dispatch }).Take(32).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(row => (new GpuExecutorBatchHandle(row.dispatch.BatchId, row.dispatch.CapacitySlotKey,
            row.dispatch.ExecutorKey, row.dispatch.AdmissionGeneration, row.dispatch.DispatchId),
            SqlGpuModelResidencyStore.ReadFence(row.resident))).ToArray();
    }

    internal ValueTask<DocumentOcrLifecycleResult> ClaimAbandonedResidentAsync(GpuExecutorBatchHandle handle,
        GpuResidentModelFence borrow, DocumentOcrSupervisorIdentity supervisor, bool ownerExited, CancellationToken ct)
    {
        if (!ownerExited) return ValueTask.FromResult(Stale());
        handle.Validate();
        borrow.Validate();
        supervisor.Validate();
        return MutateAsync(async db =>
        {
            var rows = await ReadExecutionAsync(db, handle, ct).ConfigureAwait(false);
            if (rows is null || rows.Request.ExecutionInstanceId is not null || !OwnsReservation(rows) ||
                rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                rows.Dispatch.State is not ((int)GpuExecutorDispatchState.PendingDelivery or (int)GpuExecutorDispatchState.DeliveryUncertain) ||
                borrow.Helper is null || borrow.SlotKey != handle.CapacitySlotKey || borrow.Owner == supervisor.Process ||
                borrow.Owner.MachineFingerprint != supervisor.Process.MachineFingerprint)
                return Stale();

            var now = clock.GetUtcNow();
            var proposed = new DocumentOcrExecutionLease(handle, rows.Request.MiniTaskId, handle.DispatchId, supervisor,
                now, now.AddMinutes(20));
            if (!await MatchesActiveBorrowAsync(db, proposed, borrow, ct).ConfigureAwait(false)) return Stale();
            // The claim's supervisor differs from the exited resident owner,
            // which makes native start impossible for this recovery lease.
            RecordClaim(rows, handle, handle.DispatchId, supervisor, now);
            return Accepted("abandoned-resident-claimed", Lease(rows.Request, handle));
        }, ct);
    }

    internal ValueTask<DocumentOcrDetachedSettlement> SettleDetachedCompletionAsync(DocumentOcrExecutionLease lease,
        MediaTranscriptionResult result, GpuResidentModelFence borrow, DocumentOcrResidentIdleProof idle,
        bool useNativeResult, CancellationToken ct)
    {
        lease.Validate();
        borrow.Validate();
        idle.Process.Validate();
        if (idle.ResidencyId != borrow.ResidencyId || idle.ExecutionInstanceId != lease.ExecutionInstanceId ||
            !MatchesResidentHelper(lease, borrow, idle.Process) || idle.ObservedAtUtc >= lease.DeadlineAtUtc)
            return ValueTask.FromResult(new DocumentOcrDetachedSettlement(
                Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-idle-unproven"), null));

        if (!MatchesResultModel(result, idle.ModelKey) ||
            EncodeResult(lease.ExecutionInstanceId, result, useNativeResult, idle.ModelKey) is not { } encoded)
            return ValueTask.FromResult(new DocumentOcrDetachedSettlement(
                Refused(DocumentOcrLifecycleDisposition.InvalidResult, "resident-result-invalid"), null));

        var (json, digest) = encoded;
        var handle = lease.Handle;
        var model = new GpuResidentModelKey(runtime.ModelRuntimeKey, runtime.SettingsFingerprint, idle.ModelKey);
        return MutateAsync<DocumentOcrDetachedSettlement>(async db =>
        {
            var rows = await ReadExecutionAsync(db, handle, ct).ConfigureAwait(false);
            if (rows is null || rows.Request.ExecutionInstanceId is null || !MatchesHandle(rows.Request, handle) ||
                Lease(rows.Request, handle) != lease || !MatchesProcess(rows.Request, idle.Process))
                return new(Stale(), null);

            var request = rows.Request;
            var now = clock.GetUtcNow();
            if (!ValidResidentObservation(lease, idle.Process, idle.ObservedAtUtc, now))
                return new(Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-idle-unproven"), null);
            if (request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled)
            {
                var receipt = await ReadDetachmentAsync(db, rows, handle, ct).ConfigureAwait(false);
                if (receipt is null || receipt.ResidencyId != borrow.ResidencyId || receipt.Version - 1 != borrow.Version)
                    return new(Stale(), null);
                if (request.ResultJson != json || request.ResultDigest is not { } stored ||
                    !CryptographicOperations.FixedTimeEquals(digest, stored))
                    return new(Refused(DocumentOcrLifecycleDisposition.InvalidResult, "result-conflict"), null);

                var knownFence = borrow with { Version = receipt.Version };
                var resident = await db.GpuModelResidencies.SingleAsync(row => row.ResidencyId == receipt.ResidencyId, ct)
                    .ConfigureAwait(false);
                var currentIdle = resident.State == (int)GpuModelResidencyState.Idle &&
                    SqlGpuModelResidencyStore.ReadFence(resident) == knownFence ? knownFence : null;
                return new(Accepted("settled", lease, replayed: true), currentIdle);
            }

            if (!OwnsReservation(rows) || rows.Slot.State != (int)GpuCapacitySlotState.Reserved ||
                rows.Batch.State != (int)GpuBatchState.Active || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.Acknowledged || now >= lease.DeadlineAtUtc ||
                request.ExecutionPhase != (int)DocumentOcrExecutionPhase.NativeStartPermitted || request.ResultDigest is not null ||
                !await MatchesActiveBorrowAsync(db, lease, borrow, ct).ConfigureAwait(false))
                return new(Stale(), null);
            if (!await BindingIsAvailableAsync(db, request.PipelineRecordId, request.SourceRevision,
                    request.RetainedSourceRevisionId, request.ContentSha256, false, ct).ConfigureAwait(false))
                return new(Refused(DocumentOcrLifecycleDisposition.SourceWithdrawn, "source-unavailable"), null);

            request.ResultJson = json;
            request.ResultDigest = digest;
            CompleteRequest(db, rows, handle, digest, now);
            var parked = await SqlGpuModelResidencyStore.ParkAfterTerminalAsync(db, borrow, handle,
                lease.ExecutionInstanceId, model, now, ct).ConfigureAwait(false);
            if (parked is null) throw new InvalidOperationException("media-transcription-resident-settlement-conflict");
            return new(Accepted("settled", lease), parked);
        }, ct);
    }

    internal ValueTask<DocumentOcrLifecycleResult> ConfirmResidentCleanupAsync(DocumentOcrExecutionLease lease,
        GpuResidentModelFence borrow, DocumentOcrResidentReleaseProof proof, CancellationToken ct)
    {
        borrow.Validate();
        proof.Process.Validate();
        if (proof.ResidencyId != borrow.ResidencyId || proof.ExecutionInstanceId != lease.ExecutionInstanceId ||
            !MatchesResidentHelper(lease, borrow, proof.Process, cleanupOnly: true))
            return ValueTask.FromResult(Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-cleanup-unproven"));

        var handle = lease.Handle;
        var fingerprint = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(FormattableString.Invariant(
            $"media-transcription-resident-cleanup|{handle.DispatchId:N}|{lease.ExecutionInstanceId:N}|{borrow.ResidencyId:N}|{borrow.Version}|{proof.Process.ProcessId}|{proof.Process.StartedAtUtc:O}"))));
        return WithLeaseAsync(lease, async (db, rows) =>
        {
            var now = clock.GetUtcNow();
            var request = rows.Request;
            if (!ValidResidentObservation(lease, proof.Process, proof.ObservedAtUtc, now) ||
                request.NativeProcessId is not null &&
                    (borrow.Owner != lease.Supervisor.Process || !MatchesProcess(request, proof.Process)))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-cleanup-unproven");

            if (HasCleanup(request))
            {
                var operation = OperationId(OperationFingerprint("cleanup", handle.DispatchId, lease.ExecutionInstanceId, handle.AdmissionGeneration));
                return await db.GpuExecutorEvidence.AnyAsync(row => row.OperationId == operation &&
                        row.VerifierKey == ResidentCleanupVerifier && row.RequestFingerprint == fingerprint, ct).ConfigureAwait(false)
                    ? Accepted("cleanup-confirmed", lease, replayed: true)
                    : Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-cleanup-unproven");
            }

            if (!OwnsReservation(rows) || request.ExecutionPhase is not ((int)DocumentOcrExecutionPhase.Claimed or
                    (int)DocumentOcrExecutionPhase.NativeStartPermitted) ||
                !await MatchesActiveBorrowAsync(db, lease, borrow, ct).ConfigureAwait(false) ||
                !await SqlGpuModelResidencyStore.ReleaseBorrowedAsync(db, borrow, handle, lease.ExecutionInstanceId,
                    GpuResidentReleaseKind.ContainedJobEmpty, proof.ObservedAtUtc, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-cleanup-unproven");

            var kind = request.NativeProcessId is null ? DocumentOcrCleanupKind.VerifiedUnstarted : DocumentOcrCleanupKind.OwnedJobEmpty;
            return RecordCleanup(db, request, lease, kind, proof.ObservedAtUtc, now, ResidentCleanupVerifier, fingerprint);
        }, ct);
    }

    private async Task<bool> MatchesActiveBorrowAsync(FluxKnowledgeDbContext db, DocumentOcrExecutionLease lease,
        GpuResidentModelFence borrow, CancellationToken ct)
    {
        var row = await db.GpuModelResidencies.SingleOrDefaultAsync(value => value.ResidencyId == borrow.ResidencyId, ct)
            .ConfigureAwait(false);
        return row is { State: (int)GpuModelResidencyState.Borrowed, ModelKey: "general", ReleasedAtUtc: null } &&
            row.BorrowedDispatchId == lease.Handle.DispatchId && row.BorrowedExecutionInstanceId == lease.ExecutionInstanceId &&
            row.RuntimeKey == runtime.ModelRuntimeKey && row.SettingsFingerprint == runtime.SettingsFingerprint &&
            SqlGpuModelResidencyStore.ReadFence(row) == borrow;
    }

    private static bool MatchesResidentHelper(DocumentOcrExecutionLease lease, GpuResidentModelFence borrow,
        DocumentOcrProcessIdentity process, bool cleanupOnly = false) =>
        (borrow.Owner == lease.Supervisor.Process || cleanupOnly && borrow.Owner.MachineFingerprint == lease.Supervisor.Process.MachineFingerprint) &&
        borrow.SlotKey == lease.Handle.CapacitySlotKey &&
        borrow.Helper == new GpuResidentHelperIdentity(borrow.ResidencyId, process.ProcessId, process.StartedAtUtc) &&
        process.MachineFingerprint == borrow.Owner.MachineFingerprint;

    private static bool ValidResidentObservation(DocumentOcrExecutionLease lease, DocumentOcrProcessIdentity process,
        DateTimeOffset observed, DateTimeOffset now) => observed.Offset == TimeSpan.Zero &&
        observed >= lease.ClaimedAtUtc && observed >= process.StartedAtUtc && observed <= now.AddSeconds(5);

    private static async Task<GpuModelDetachmentReceiptEntity?> ReadDetachmentAsync(FluxKnowledgeDbContext db,
        ExecutionRows rows, GpuExecutorBatchHandle handle, CancellationToken ct)
    {
        var request = rows.Request;
        if (request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Settled || request.CleanupConfirmedAtUtc is not null ||
            request.CleanupKind is not null || request.NativeProcessId is null || request.NativeProcessStartedAtUtc is null ||
            rows.Batch.State != (int)GpuBatchState.Completed || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed ||
            rows.Dispatch.State != (int)GpuExecutorDispatchState.Terminal || request.ExecutionInstanceId is not { } executionId ||
            !MatchesHandle(request, handle) || ReadResult(request) is not { Result.Succeeded: true })
            return null;
        return await (from receipt in db.GpuModelDetachmentReceipts
                      join resident in db.GpuModelResidencies on receipt.ResidencyId equals resident.ResidencyId
                      where receipt.DispatchId == handle.DispatchId && receipt.BatchId == handle.BatchId &&
                          receipt.ExecutorKey == handle.ExecutorKey && receipt.AdmissionGeneration == handle.AdmissionGeneration &&
                          receipt.ExecutionInstanceId == executionId && resident.Version >= receipt.Version &&
                          resident.SlotKey == handle.CapacitySlotKey && resident.RuntimeKey == request.ModelRuntimeKey &&
                          resident.SettingsFingerprint == request.SettingsFingerprint && resident.ChildJobId == receipt.ResidencyId &&
                          resident.ChildProcessId == request.NativeProcessId && resident.ChildStartedAtUtc == request.NativeProcessStartedAtUtc &&
                          resident.MachineFingerprint == request.MachineFingerprint
                      select receipt).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private static bool MatchesResultModel(MediaTranscriptionResult result, string model)
    {
        if (!result.Succeeded || result.ReasonCode != "transcription-complete" || result.MetadataJson is null ||
            model is not ("general" or "egyptian-ar-en"))
            return false;
        try
        {
            using var document = JsonDocument.Parse(result.MetadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.String &&
                profile.GetString() == model;
        }
        catch (JsonException) { return false; }
    }
}
