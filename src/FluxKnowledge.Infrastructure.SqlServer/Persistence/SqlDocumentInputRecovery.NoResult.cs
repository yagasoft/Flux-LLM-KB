using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static partial class SqlDocumentInputRecovery
{
    private const string NoResultReason = "document-ocr-settled-without-result";

    private static bool LeaseIsQuiescent(string? owner, DateTimeOffset? expires) =>
        owner is null && expires is null || owner is not null && expires is not null && expires <= DateTimeOffset.UtcNow;

    private static async Task<SettledNoResultBinding?> ReadSettledNoResultAsync(FluxKnowledgeDbContext db,
        DocumentOcrRequestEntity request, JobEntity job, PipelineRecordEntity record, Guid childId, bool transition, CancellationToken token)
    {
        if (request.PipelineRecordId != record.Id || request.SourceRevision != record.Revision || request.RetainedSourceRevisionId != childId ||
            request.ContentSha256 != record.ContentHash || request.State != (int)DocumentOcrRequestState.Pending ||
            request.ResultJson is not null || request.ResultDigest is not null || request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Settled ||
            request.RecoveryReason != "document-ocr-source-withdrawn" || request.CleanupProofKind != (int)DocumentOcrCleanupKind.OwnedJobEmpty ||
            request.ExecutionInstanceId is not { } execution || request.NativeProcessId is not > 0 || request.NativeProcessStartedAtUtc is null ||
            request.ModelRuntimeKey != PaddleOcrVlmRuntimeContract.ModelRuntimeKey || request.SettingsFingerprint != PaddleOcrVlmRuntimeContract.SettingsFingerprint)
            return null;
        var deliveries = await db.OutboxMessages.Where(value => value.JobId == job.Id).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (deliveries.Length != 1) return null;
        var delivery = deliveries[0];
        if (delivery.PipelineRecordId != record.Id || delivery.SourceRevision != record.Revision || delivery.Stage != job.Stage ||
            delivery.Operation != job.Operation || delivery.CompletedArtifactId is not null || job.LeaseGeneration == long.MaxValue ||
            delivery.LeaseGeneration == long.MaxValue || (transition ? !LeaseIsQuiescent(delivery.LeaseOwner, delivery.LeaseExpiresAtUtc) :
                delivery.DispatchedAtUtc is null || delivery.LeaseOwner is not null || delivery.LeaseExpiresAtUtc is not null)) return null;
        var task = await db.GpuMiniTasks.SingleOrDefaultAsync(value => value.Id == request.MiniTaskId, token).ConfigureAwait(false);
        if (task is null || task.ParentJobId != job.Id || task.SourceRevision != record.Revision || task.BatchId is not { } batchId ||
            task.ExecutionState != (int)GpuMiniTaskExecutionState.OutcomeUncertain || task.AdmissionGeneration <= 0 ||
            task.PriorityLane != (int)GpuPriorityLane.DocumentIndexing || task.RequiredExecutorKey is not null && task.RequiredExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey ||
            task.ModelRuntimeKey != request.ModelRuntimeKey || task.SettingsFingerprint != request.SettingsFingerprint ||
            await db.GpuMiniTasks.AnyAsync(value => value.ParentJobId == job.Id && value.Id != task.Id, token).ConfigureAwait(false)) return null;
        var batch = await db.GpuBatches.SingleOrDefaultAsync(value => value.Id == batchId, token).ConfigureAwait(false);
        var dispatches = await db.GpuExecutorDispatches.Where(value => value.BatchId == batchId).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (batch is null || batch.State != (int)GpuBatchState.Released || batch.AdmissionGeneration != task.AdmissionGeneration ||
            batch.ItemCount != 1 || batch.ModelRuntimeKey != task.ModelRuntimeKey || batch.SettingsFingerprint != task.SettingsFingerprint ||
            dispatches.Length != 1 || await db.GpuMiniTasks.CountAsync(value => value.BatchId == batchId, token).ConfigureAwait(false) != 1 ||
            await db.GpuCapacitySlots.AnyAsync(value => value.ActiveBatchId == batchId, token).ConfigureAwait(false)) return null;
        var dispatch = dispatches[0];
        var handle = new GpuExecutorBatchHandle(batchId, dispatch.CapacitySlotKey, dispatch.ExecutorKey, task.AdmissionGeneration, dispatch.DispatchId);
        if (dispatch.State != (int)GpuExecutorDispatchState.Terminal || dispatch.ExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey ||
            dispatch.AdmissionGeneration != task.AdmissionGeneration || dispatch.OwnerKey != batch.OwnerKey || dispatch.CapacitySlotKey != batch.CapacitySlotKey ||
            !SqlDocumentOcrStore.HasCleanupProof(request, handle, task.Id) ||
            await db.NativeWorkerInstances.AnyAsync(value => value.ActiveDispatchId == dispatch.DispatchId && value.ExitedAtUtc == null, token).ConfigureAwait(false) ||
            await db.GpuExecutorResultReceipts.AnyAsync(value => value.MiniTaskId == task.Id || value.DispatchId == dispatch.DispatchId, token).ConfigureAwait(false)) return null;
        var cleanupId = SqlDocumentOcrStore.PhaseOperation(handle, execution, "cleanup");
        var cleanup = await db.GpuExecutorEvidence.SingleOrDefaultAsync(value => value.OperationId == cleanupId, token).ConfigureAwait(false);
        if (cleanup is null || cleanup.DispatchId != dispatch.DispatchId || cleanup.BatchId != batchId || cleanup.CapacitySlotKey != batch.CapacitySlotKey ||
            cleanup.ExecutorKey != dispatch.ExecutorKey || cleanup.AdmissionGeneration != task.AdmissionGeneration ||
            cleanup.EvidenceClass != (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed || cleanup.VerifierKey != "ocr-owned-job-v1" ||
            cleanup.ObservedAtUtc != request.NativeCleanupConfirmedAtUtc) return null;
        return new(task.Id, batchId, task.AdmissionGeneration, dispatch.DispatchId, execution, cleanup.OperationId, cleanup.RequestFingerprint,
            Convert.ToHexString(request.RowVersion), Convert.ToHexString(task.RowVersion), Convert.ToHexString(batch.RowVersion), Convert.ToHexString(dispatch.RowVersion),
            checked(job.LeaseGeneration + (transition ? 1 : 0)), delivery.Id, checked(delivery.LeaseGeneration + (transition ? 1 : 0)));
    }

    private static async Task TerminaliseNoResultAsync(FluxKnowledgeDbContext db, FailureBinding binding, CancellationToken token)
    {
        var job = await db.Jobs.SingleAsync(value => value.Id == binding.FailedJobId, token).ConfigureAwait(false);
        if (job.PublicState == (int)PublicJobState.Failed && job.Reason == NoResultReason) return;
        var settled = binding.NoResult ?? throw new InvalidOperationException("The settled no-result proof is missing.");
        var delivery = await db.OutboxMessages.SingleAsync(value => value.Id == settled.DeliveryId, token).ConfigureAwait(false);
        var activity = await db.SourceActivities.SingleAsync(value => value.Id == binding.FailedChildActivityId, token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        job.PublicState = (int)PublicJobState.Failed; job.Reason = NoResultReason;
        job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.LeaseGeneration = settled.JobLeaseGeneration;
        delivery.DispatchedAtUtc ??= now; delivery.LeaseOwner = null; delivery.LeaseExpiresAtUtc = null;
        delivery.LeaseGeneration = settled.DeliveryLeaseGeneration;
        activity.State = (int)SourceActivityState.FailedTerminal; activity.Reason = NoResultReason; activity.UpdatedAtUtc = now;
    }

    // Native rows remain untouched. The generations describe the terminal state
    // committed atomically with the successor, so replay cannot invalidate itself.
    internal sealed record SettledNoResultBinding(Guid MiniTaskId, Guid BatchId, long AdmissionGeneration, Guid DispatchId,
        Guid ExecutionInstanceId, Guid CleanupOperationId, string CleanupFingerprint, string RequestVersion, string TaskVersion,
        string BatchVersion, string DispatchVersion, long JobLeaseGeneration, Guid DeliveryId, long DeliveryLeaseGeneration);
}
