using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Both sides of request/task ownership, including historical work, under the caller's publication fence.</summary>
internal static class SqlSourceDeletionOwnership
{
    internal sealed record Snapshot(bool IsOwned, Guid[] TaskIds, Guid[] EmbeddingTaskIds, Guid[] OcrTaskIds);

    internal static IQueryable<Guid> RevisionIds(FluxKnowledgeDbContext context, Guid rootId) =>
        context.SourceRevisions.Where(value => value.SourceRootId == rootId).Select(value => value.Id);

    internal static IQueryable<Guid> RecordIds(FluxKnowledgeDbContext context, Guid rootId) =>
        context.PipelineRecords.Where(value => value.SourceRevisionId.HasValue && value.SourceRevision!.SourceRootId == rootId)
            .Select(value => value.Id);

    // Fresh execution/cleanup fence for pruning and payload purge. This never
    // replaces ReadAsync's full historical ownership proof or native-file gates.
    internal static async Task<bool> HasUnconfirmedWorkAsync(FluxKnowledgeDbContext context, Guid rootId, CancellationToken ct)
    {
        var revisionIds = RevisionIds(context, rootId);
        var recordIds = RecordIds(context, rootId);
        var jobIds = context.Jobs.Where(job => recordIds.Contains(job.PipelineRecordId)).Select(job => job.Id);
        var parentTaskIds = context.GpuMiniTasks.Where(task => task.ParentJobId.HasValue && jobIds.Contains(task.ParentJobId.Value))
            .Select(task => task.Id);
        // Cover both ownership directions, including a foreign-parent task whose
        // request points into this source and the reverse contradictory binding.
        var embeddingRequests = context.EmbeddingGpuRequests.Where(request => recordIds.Contains(request.PipelineRecordId) ||
            jobIds.Contains(request.ParentJobId) || parentTaskIds.Contains(request.MiniTaskId));
        var ocrRequests = context.DocumentOcrRequests.Where(request => recordIds.Contains(request.PipelineRecordId) ||
            revisionIds.Contains(request.RetainedSourceRevisionId) || jobIds.Contains(request.ParentJobId) || parentTaskIds.Contains(request.MiniTaskId));
        var settledOcrIds = ocrRequests.Where(request => request.ExecutionInstanceId != null && request.NativeCleanupConfirmedAtUtc != null &&
            (request.ExecutionPhase == (int)DocumentOcrExecutionPhase.CleanupConfirmed || request.ExecutionPhase == (int)DocumentOcrExecutionPhase.Settled) &&
            ((request.CleanupProofKind == (int)DocumentOcrCleanupKind.VerifiedUnstarted && request.NativeProcessId == null) ||
             (request.CleanupProofKind == (int)DocumentOcrCleanupKind.OwnedJobEmpty && request.NativeProcessId != null && request.NativeProcessStartedAtUtc != null) ||
             (request.CleanupProofKind == (int)DocumentOcrCleanupKind.StoppedMaintenance && request.PhysicalCleanupEvidenceSha256 != null &&
              request.PhysicalCleanupEvidenceSha256.Length == 64 &&
              !EF.Functions.Like(EF.Functions.Collate(request.PhysicalCleanupEvidenceSha256, SchemaConfiguration.SchedulerFenceCollation), "%[^0-9a-f]%"))))
            .Select(request => request.MiniTaskId);
        if (await context.GpuMiniTasks.AnyAsync(task =>
            (task.ExecutionState == (int)Domain.Gpu.GpuMiniTaskExecutionState.Active ||
             (task.ExecutionState == (int)Domain.Gpu.GpuMiniTaskExecutionState.OutcomeUncertain && !settledOcrIds.Contains(task.Id) &&
              !embeddingRequests.Any(request => request.MiniTaskId == task.Id && request.NativeCleanupConfirmed))) &&
            (parentTaskIds.Contains(task.Id) || embeddingRequests.Any(request => request.MiniTaskId == task.Id) ||
             ocrRequests.Any(request => request.MiniTaskId == task.Id)), ct).ConfigureAwait(false)) return true;
        if (await embeddingRequests.AnyAsync(request => request.ExecutorInstanceId != null && !request.NativeCleanupConfirmed, ct)
            .ConfigureAwait(false)) return true;

        // Partial claims are unresolved, even without an instance. A historical
        // OutcomeUncertain task with settled native cleanup is safe for this
        // metadata-only phase. Match the structural HasCleanupProof contract.
        return await ocrRequests.Where(request => request.ExecutionPhase != (int)DocumentOcrExecutionPhase.Unclaimed ||
            request.ExecutionInstanceId != null || request.ExecutionDispatchId != null || request.ExecutionBatchId != null ||
            request.ExecutionAdmissionGeneration != null || request.ExecutionMachineFingerprint != null ||
            request.SupervisorInstanceId != null || request.SupervisorProcessId != null || request.SupervisorProcessStartedAtUtc != null ||
            request.ExecutionClaimedAtUtc != null || request.ExecutionDeadlineAtUtc != null ||
            request.NativeProcessId != null || request.NativeProcessStartedAtUtc != null || request.NativeCleanupConfirmedAtUtc != null ||
            request.CleanupProofKind != null || request.PhysicalCleanupEvidenceSha256 != null)
            .AnyAsync(request => !settledOcrIds.Contains(request.MiniTaskId), ct)
            .ConfigureAwait(false);
    }

    internal static async Task<Snapshot> ReadAsync(FluxKnowledgeDbContext context, Guid rootId, EmbeddingGpuRuntime? runtime, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("Source deletion ownership requires a transaction.");
        var revisionIds = RevisionIds(context, rootId);
        var recordIds = RecordIds(context, rootId);
        var jobIds = context.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).Select(value => value.Id);
        var taskIds = await context.GpuMiniTasks.Where(value => value.ParentJobId.HasValue && jobIds.Contains(value.ParentJobId.Value))
            .Select(value => value.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var embeddingIds = runtime is null ? [] : await SqlEmbeddingGpuRequestStore.OwnedLocalTasks(context, runtime)
            .Where(value => value.ParentJobId.HasValue && jobIds.Contains(value.ParentJobId.Value))
            .Select(value => value.Id).ToArrayAsync(ct).ConfigureAwait(false);
        if (await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(context, recordIds, embeddingIds, ct).ConfigureAwait(false))
            return new(false, taskIds, embeddingIds, []);
        var embeddingSet = embeddingIds.ToHashSet();
        var ocrIds = new HashSet<Guid>();
        var candidates=taskIds.Where(id=>!embeddingSet.Contains(id)).ToArray();
        if(candidates.Length>0)
        {
            var proven = await context.DocumentOcrRequests.Where(request => candidates.Contains(request.MiniTaskId) &&
                request.ModelRuntimeKey == PaddleOcrVlmRuntimeContract.ModelRuntimeKey && revisionIds.Contains(request.RetainedSourceRevisionId) &&
                context.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && request.ParentJobId == task.ParentJobId &&
                    request.SourceRevision == task.SourceRevision && request.ModelRuntimeKey == task.ModelRuntimeKey &&
                    request.SettingsFingerprint == task.SettingsFingerprint &&
                    context.Jobs.Any(job => job.Id == task.ParentJobId && job.PipelineRecordId == request.PipelineRecordId &&
                        job.SourceRevision == request.SourceRevision)))
                .Select(request => request.MiniTaskId).ToArrayAsync(ct).ConfigureAwait(false);
            ocrIds.UnionWith(proven);
            if (candidates.Any(id => !ocrIds.Contains(id))) return new(false, taskIds, embeddingIds, ocrIds.ToArray());
        }
        // Parent inventory alone cannot see a request attached to another root's
        // task. Orphan requests without a task retain owned-record cleanup.
        var requestedOcrIds = await context.DocumentOcrRequests.Where(request => recordIds.Contains(request.PipelineRecordId) &&
                context.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId))
            .Select(request => request.MiniTaskId).ToArrayAsync(ct).ConfigureAwait(false);
        return new(requestedOcrIds.All(ocrIds.Contains), taskIds, embeddingIds, ocrIds.ToArray());
    }
}
