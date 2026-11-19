using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlMediaTranscriptionGpuStore
{
    public ValueTask<DocumentOcrLifecycleResult> StoreResultAsync(DocumentOcrExecutionLease lease,
        MediaTranscriptionResult result, CancellationToken ct, bool useNativeResult = false)
    {
        if (EncodeResult(lease.ExecutionInstanceId, result, useNativeResult) is not { } encoded)
            return ValueTask.FromResult(Refused(DocumentOcrLifecycleDisposition.InvalidResult, "result-invalid"));
        var (json, digest) = encoded;
        return WithLeaseAsync(lease, async (db, rows) =>
        {
            var request = rows.Request;
            if (request.ResultDigest is { } saved)
                return CryptographicOperations.FixedTimeEquals(digest, saved) && request.ResultJson == json
                    ? Accepted("result-stored", lease, replayed: true)
                    : Refused(DocumentOcrLifecycleDisposition.InvalidResult, "result-conflict");
            if (!HasCleanup(request) || request.ExecutionPhase != (int)DocumentOcrExecutionPhase.CleanupConfirmed ||
                !OwnsReservation(rows))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "cleanup-unproven");
            if (result.Succeeded && request.CleanupKind != (int)DocumentOcrCleanupKind.OwnedJobEmpty)
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "native-work-unproven");
            if (result.Succeeded && !await BindingIsAvailableAsync(db, request.PipelineRecordId, request.SourceRevision,
                    request.RetainedSourceRevisionId, request.ContentSha256, false, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.SourceWithdrawn, "source-unavailable");
            request.ResultJson = json;
            request.ResultDigest = digest;
            return Accepted("result-stored", lease);
        }, ct);
    }

    public async ValueTask<MediaTranscriptionGpuCompletion?> ReadPendingCompletionAsync(GpuExecutorBatchHandle handle,
        CancellationToken ct)
    {
        handle.Validate();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var execution = await ReadExecutionAsync(db, handle, ct).ConfigureAwait(false);
        if (execution?.Request is not { ExecutionInstanceId: not null, ResultDigest: { Length: 32 } digest } request ||
            !MatchesHandle(request, handle))
            return null;
        return new(request.MiniTaskId, digest.ToArray(), HasCleanup(request));
    }

    public ValueTask<DocumentOcrLifecycleResult> SettleAsync(GpuExecutorBatchHandle handle, Guid miniTaskId,
        byte[] resultDigest, CancellationToken ct)
    {
        handle.Validate();
        if (miniTaskId == Guid.Empty || resultDigest is not { Length: 32 })
            return ValueTask.FromResult(Refused(DocumentOcrLifecycleDisposition.InvalidResult, "completion-invalid"));
        return MutateAsync(async db =>
        {
            var rows = await ReadExecutionAsync(db, handle, ct).ConfigureAwait(false);
            if (rows is null || rows.Request.MiniTaskId != miniTaskId || !MatchesHandle(rows.Request, handle))
                return Stale();
            var request = rows.Request;
            if (request.ResultDigest is null || !CryptographicOperations.FixedTimeEquals(resultDigest, request.ResultDigest) ||
                ReadResult(request) is null)
                return Refused(DocumentOcrLifecycleDisposition.InvalidResult, "result-invalid");
            if (!HasCleanup(request))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "cleanup-unproven");
            if (request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled &&
                rows.Dispatch.State == (int)GpuExecutorDispatchState.Terminal &&
                rows.Task.ExecutionState == (int)GpuMiniTaskExecutionState.Completed)
                return Accepted("settled", replayed: true);
            if (!OwnsReservation(rows) || rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Active ||
                rows.Dispatch.State is not ((int)GpuExecutorDispatchState.Acknowledged or (int)GpuExecutorDispatchState.DeliveryUncertain) ||
                rows.Batch.State is not ((int)GpuBatchState.Active or (int)GpuBatchState.CapacityUncertain))
                return Stale();

            if (await SqlGpuModelResidencyStore.HasLiveResidencyAsync(db, handle.CapacitySlotKey, ct).ConfigureAwait(false))
                return Refused(DocumentOcrLifecycleDisposition.CleanupUnproven, "resident-native-capacity-held");

            var now = clock.GetUtcNow();
            CompleteRequest(db, rows, handle, resultDigest, now);
            rows.Slot.State = (int)GpuCapacitySlotState.Available;
            rows.Slot.ActiveBatchId = null;
            rows.Slot.OwnerKey = null;
            rows.Slot.LastHeartbeatAtUtc = now;
            rows.Slot.UpdatedAtUtc = now;
            await SqlGpuSchedulerStore.RecordWakeAsync(db, GpuSchedulerWakeReason.CapacityReleased, now, ct).ConfigureAwait(false);
            return Accepted("settled");
        }, ct);
    }

    private static void CompleteRequest(FluxKnowledgeDbContext db, ExecutionRows rows, GpuExecutorBatchHandle handle,
        byte[] resultDigest, DateTimeOffset now)
    {
    var operation = OperationId(OperationFingerprint("result", handle.DispatchId, rows.Request.ExecutionInstanceId!.Value,
        handle.AdmissionGeneration));
    var receipt = new GpuExecutorResultReceipt(operation, handle, rows.Request.MiniTaskId, GpuMiniTaskBoundaryDisposition.Completed,
        resultDigest, GpuExecutorEvidenceClass.TaskOutcomeConfirmed);
    var fingerprint = SqlGpuSchedulerStore.CreateExecutorResultReceiptFingerprint(receipt);
    db.GpuExecutorResultReceipts.Add(new()
    {
        OperationId = operation, DispatchId = handle.DispatchId, BatchId = handle.BatchId, MiniTaskId = rows.Request.MiniTaskId,
        ExecutorKey = handle.ExecutorKey, AdmissionGeneration = handle.AdmissionGeneration,
        Disposition = (int)receipt.Disposition, EvidenceClass = (int)receipt.EvidenceClass,
        OpaqueResultDigest = resultDigest.ToArray(), RequestFingerprint = fingerprint, CreatedAtUtc = now
    });
    db.GpuSchedulerOperationReceipts.Add(new()
    {
        OperationId = operation, OperationKind = "executor-result-receipt", RequestFingerprint = fingerprint,
        BatchId = handle.BatchId, CapacitySlotKey = handle.CapacitySlotKey, OwnerKey = rows.Dispatch.OwnerKey,
        AdmissionGeneration = handle.AdmissionGeneration, Accepted = true, Committed = true, CreatedAtUtc = now
    });
    rows.Task.ExecutionState = (int)GpuMiniTaskExecutionState.Completed;
    rows.Batch.State = (int)GpuBatchState.Completed;
    rows.Batch.UpdatedAtUtc = now;
    rows.Dispatch.State = (int)GpuExecutorDispatchState.Terminal;
    rows.Dispatch.UpdatedAtUtc = now;
        rows.Request.ExecutionPhase = (int)DocumentOcrExecutionPhase.Settled;
    }

    private static (string Json, byte[] Digest)? EncodeResult(Guid executionId, MediaTranscriptionResult result,
        bool useNativeResult, string? residentModel = null)
    {
        if (!IsValidResult(result) || useNativeResult && !result.Succeeded) return null;
        var json = result.Succeeded
            ? JsonSerializer.Serialize(new TranscriptResultReceipt(1, executionId, result.ReasonCode,
                Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(result.Transcript!))),
                StrictUtf8.GetByteCount(result.Transcript!), useNativeResult, residentModel), JsonOptions)
            : JsonSerializer.Serialize(result, JsonOptions);
        return Encoding.Unicode.GetByteCount(json) <= MediaTranscriptionGpuContracts.MaximumResultBytes
            ? (json, SHA256.HashData(StrictUtf8.GetBytes(json))) : null;
    }

    public ValueTask<bool> RequeueSettledAsync(GpuExecutorBatchHandle handle, Guid miniTaskId, CancellationToken ct)
    {
        handle.Validate();
        return MutateAsync(async db =>
        {
            var rows = await ReadExecutionAsync(db, handle, ct).ConfigureAwait(false);
            if (rows is null || rows.Request.MiniTaskId != miniTaskId || !MatchesHandle(rows.Request, handle) ||
                rows.Request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Settled || rows.Request.RequeuedAtUtc is not null ||
                rows.Task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed ||
                rows.Dispatch.State != (int)GpuExecutorDispatchState.Terminal ||
                !HasCleanup(rows.Request) && await ReadDetachmentAsync(db, rows, handle, ct).ConfigureAwait(false) is null ||
                !await SourceIsEnabledAsync(db, rows.Request, ct).ConfigureAwait(false))
                return false;

            var parent = await Jobs(db, rows.Request).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (parent is null || parent.PublicState != (int)PublicJobState.GpuProcessing) return false;
            var outbox = await db.OutboxMessages.SingleOrDefaultAsync(m => m.JobId == parent.Id &&
                m.PipelineRecordId == parent.PipelineRecordId && m.SourceRevision == parent.SourceRevision &&
                m.Stage == parent.Stage && m.Operation == parent.Operation && m.DispatchedAtUtc == null, ct).ConfigureAwait(false);
            if (outbox is null) return false;

            var now = clock.GetUtcNow();
            parent.PublicState = (int)PublicJobState.WorkerQueued;
            parent.LeaseOwner = null;
            parent.LeaseExpiresAtUtc = null;
            parent.Reason = null;
            parent.ErrorDetails = null;
            outbox.LeaseOwner = null;
            outbox.LeaseExpiresAtUtc = null;
            outbox.DueAtUtc = now;
            rows.Request.RequeuedAtUtc = now;
            return true;
        }, ct);
    }

    public async ValueTask<IReadOnlyList<MediaTranscriptionGpuRecovery>> ReadRecoveryAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var eligible = BoundRecords(db, approved: true);
        var rows = await (from r in db.MediaTranscriptionGpuRequests.AsNoTracking()
                          join d in db.GpuExecutorDispatches on r.ExecutionDispatchId equals d.DispatchId
                          join s in db.GpuCapacitySlots on d.CapacitySlotKey equals s.SlotKey
                          where r.ExecutionInstanceId != null && r.ModelRuntimeKey == runtime.ModelRuntimeKey &&
                            r.SettingsFingerprint == runtime.SettingsFingerprint && d.ExecutorKey == runtime.ExecutorKey &&
                            d.CapacitySlotKey == PaddleOcrVlmRuntimeContract.CapacitySlotKey && d.BatchId == r.ExecutionBatchId &&
                            d.AdmissionGeneration == r.ExecutionAdmissionGeneration &&
                            (s.ActiveBatchId == d.BatchId ||
                                r.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled && r.RequeuedAtUtc == null &&
                                eligible.Any(p => p.Id == r.PipelineRecordId && p.Revision == r.SourceRevision &&
                                    p.SourceRevisionId == r.RetainedSourceRevisionId &&
                                    p.SourceRevision!.SourceRoot.State == (int)SourceRootState.Enabled))
                          orderby s.ActiveBatchId == d.BatchId ? 0 : 1, r.CreatedAtUtc, r.MiniTaskId
                          select new
                          {
                              r.MiniTaskId, r.ExecutionInstanceId, r.SupervisorInstanceId, r.SupervisorProcessId,
                              r.SupervisorProcessStartedAtUtc, r.MachineFingerprint, r.ClaimedAtUtc, r.DeadlineAtUtc,
                              r.NativeProcessId, r.NativeProcessStartedAtUtc, r.CleanupConfirmedAtUtc,
                              HasResult = r.ResultDigest != null,
                              d.DispatchId, d.BatchId, d.CapacitySlotKey, d.ExecutorKey, d.AdmissionGeneration
                          }).Take(32).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new MediaTranscriptionGpuRecovery(
            new(new(r.BatchId, r.CapacitySlotKey, r.ExecutorKey, r.AdmissionGeneration, r.DispatchId), r.MiniTaskId,
                r.ExecutionInstanceId!.Value, new(r.SupervisorInstanceId!.Value,
                    new(r.SupervisorProcessId!.Value, r.SupervisorProcessStartedAtUtc!.Value, r.MachineFingerprint!)),
                r.ClaimedAtUtc!.Value, r.DeadlineAtUtc!.Value),
            r.NativeProcessId is { } pid ? new(pid, r.NativeProcessStartedAtUtc!.Value, r.MachineFingerprint!) : null,
            r.HasResult, r.CleanupConfirmedAtUtc is not null)).ToArray();
    }

    private static bool HasCleanup(MediaTranscriptionGpuRequestEntity r) =>
        r.CleanupConfirmedAtUtc is not null && r.ExecutionInstanceId is not null &&
        (r.CleanupKind == (int)DocumentOcrCleanupKind.VerifiedUnstarted && r.NativeProcessId is null && r.NativeProcessStartedAtUtc is null ||
         r.CleanupKind == (int)DocumentOcrCleanupKind.OwnedJobEmpty && r.NativeProcessId is not null && r.NativeProcessStartedAtUtc is not null);

    private sealed record TranscriptResultReceipt(int Version, Guid ExecutionId, string ReasonCode,
        string TranscriptSha256, int TranscriptByteLength, bool UseNativeResult,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResidentModel = null);

    private static MediaTranscriptionCompletedResult? ReadResult(MediaTranscriptionGpuRequestEntity request)
    {
        if (request.ResultJson is not { } json || request.ResultDigest is not { Length: 32 } digest)
            return null;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(StrictUtf8.GetBytes(json)), digest))
                return null;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("version", out _))
            {
                var receipt = document.RootElement.Deserialize<TranscriptResultReceipt>(JsonOptions);
                return receipt is { Version: 1, TranscriptByteLength: > 0 and <= MediaTranscriptionGpuContracts.MaximumResultBytes } &&
                    receipt.ExecutionId == request.ExecutionInstanceId && receipt.ExecutionId != Guid.Empty &&
                    ReasonIsValid(receipt.ReasonCode) &&
                    receipt.TranscriptSha256 is { Length: 64 } && receipt.TranscriptSha256.All(char.IsAsciiHexDigit)
                    ? new(new(true, receipt.ReasonCode, null, null), receipt.ExecutionId, receipt.TranscriptSha256, receipt.TranscriptByteLength, receipt.UseNativeResult)
                    : null;
            }

            var result = document.RootElement.Deserialize<MediaTranscriptionResult>(JsonOptions);
            return IsValidResult(result) ? new(result!) : null;
        }
        catch (Exception error) when (error is JsonException or EncoderFallbackException or InvalidOperationException) { return null; }
    }

    private static bool ReasonIsValid(string? reason) => reason is { Length: > 0 and <= 128 } &&
        reason.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static bool IsValidResult(MediaTranscriptionResult? result)
    {
        if (result is null || !ReasonIsValid(result.ReasonCode))
            return false;
        if (!result.Succeeded) return result.Transcript is null && result.MetadataJson is null;
        if (string.IsNullOrWhiteSpace(result.Transcript) || string.IsNullOrWhiteSpace(result.MetadataJson) ||
            result.Transcript.Length + (long)result.MetadataJson.Length > MediaTranscriptionGpuContracts.MaximumResultBytes / sizeof(char))
            return false;
        try
        {
            _ = StrictUtf8.GetByteCount(result.Transcript);
            _ = StrictUtf8.GetByteCount(result.MetadataJson);
            using var metadata = JsonDocument.Parse(result.MetadataJson);
            return metadata.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception error) when (error is JsonException or EncoderFallbackException) { return false; }
    }
}
