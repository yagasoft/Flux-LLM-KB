using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal sealed class SqlDeletedFileJobRetention
{
    private const int BatchSize = 32;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _owner = new(1);
    private Guid? _after;
    private DateTimeOffset _nextSweep;

    // The cursor only spreads read/probe work across ticks. Every deletion commits
    // atomically and is discovered again from SQL after a restart.
    internal async Task<int> AdvanceAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        ISourceObservationReader observations, DateTimeOffset now, CancellationToken ct)
    {
        if (!await _owner.WaitAsync(0, ct).ConfigureAwait(false)) return 0;
        try
        {
            if (now < _nextSweep) return 0;
            await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var candidates = Candidates(lookup, now - Retention);
            if (_after is { } after) candidates = candidates.Where(x => x.JobId.CompareTo(after) > 0);
            var page = await candidates.OrderBy(x => x.JobId).Take(BatchSize).ToArrayAsync(ct).ConfigureAwait(false);
            var purged = 0;
            foreach (var group in page.GroupBy(x => x.PhysicalId))
            {
                var ids = group.Select(x => x.JobId).ToArray();
                purged += await lookup.Database.CreateExecutionStrategy()
                    .ExecuteAsync(() => PurgeAsync(factory, observations, group.Key, ids, now, ct)).ConfigureAwait(false);
            }
            _after = page.Length == BatchSize ? page[^1].JobId : null;
            if (_after is null) _nextSweep = now + SweepInterval;
            return purged;
        }
        finally { _owner.Release(); }
    }

    private static async Task<int> PurgeAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        ISourceObservationReader observations, Guid physicalId, Guid[] ids, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var physical = await db.SourceRevisions.AsNoTracking().Include(x => x.SourceRoot)
            .SingleOrDefaultAsync(x => x.Id == physicalId, ct).ConfigureAwait(false);
        if (physical is null || physical.SourceRoot.State != (int)SourceRootState.Enabled) return 0;
        var root = SqlSourceRemovalReconciler.Restore(physical.SourceRoot);
        var relative = Path.GetRelativePath(root.CanonicalPath, physical.CanonicalPath);
        if (!MatchesWitness(physical, root, relative)) return 0;

        try
        {
            // Query hints, a missing source row and a file API's boolean existence
            // check cannot distinguish deletion from inaccessible storage. Probe
            // the namespace before taking the shared publication/scheduler fences.
            using var absence = observations.OpenAbsence(root, relative);
            if (absence is null) return 0;
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
            await SqlGpuSchedulerStore.AcquireMutationLockAsync(db, transaction.GetDbTransaction(), ct).ConfigureAwait(false);
            if (await db.IndexState.AnyAsync(x => x.Id == 1 && x.CorpusRebuildOperationId != null, ct).ConfigureAwait(false) ||
                !await SnapshotStillCurrentAsync(db, physical, root, ct).ConfigureAwait(false)) return 0;
            var candidates = await Candidates(db, now - Retention).Where(x => x.PhysicalId == physicalId && ids.Contains(x.JobId))
                .Select(x => x.JobId).ToArrayAsync(ct).ConfigureAwait(false);
            if (candidates.Length == 0) return 0;
            var jobIds = await RetirableJobs(db, candidates, physical, now).ToArrayAsync(ct).ConfigureAwait(false);
            if (jobIds.Length == 0) return 0;
            await DeleteDependantsAsync(db, jobIds, now, ct).ConfigureAwait(false);
            var purged = await db.Jobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            absence.ValidateCurrent();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return purged;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A returned path or a loss of namespace authority rolls back the whole unit.
            return 0;
        }
    }

    private static async Task<bool> SnapshotStillCurrentAsync(FluxKnowledgeDbContext db,
        SourceRevisionEntity physical, SourceRootConfiguration root, CancellationToken ct)
    {
        var current = await db.SourceRootConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == root.Id.Value, ct)
            .ConfigureAwait(false);
        return current is not null && current.State == (int)SourceRootState.Enabled &&
            current.ConfigurationRevision == root.ConfigurationRevision && current.CanonicalPath == root.CanonicalPath &&
            SqlSourceScanStore.ParseAdmissionIdentityFingerprint(current.HealthEvidenceJson) == root.PhysicalIdentityFingerprint &&
            await db.SourceRevisions.AnyAsync(x => x.Id == physical.Id && x.RowVersion == physical.RowVersion, ct).ConfigureAwait(false) &&
            !await db.SourceRevisions.AnyAsync(other => other.SourceRootId == physical.SourceRootId && other.Id != physical.Id &&
                other.OriginKind == 0 && (other.SuppressedAtUtc == null || other.DiscoveredAtUtc >= physical.SuppressedAtUtc) &&
                (other.StableSourceIdentity == physical.StableSourceIdentity ||
                    EF.Functions.Collate(other.CanonicalPath, "Latin1_General_100_CI_AS") == physical.CanonicalPath), ct).ConfigureAwait(false);
    }

    private sealed class JobCandidate
    {
        public Guid JobId { get; set; }
        public Guid PhysicalId { get; set; }
    }

    private static IQueryable<JobCandidate> Candidates(FluxKnowledgeDbContext db, DateTimeOffset cutoff) =>
        db.Database.SqlQueryRaw<JobCandidate>(CandidateSql, new Microsoft.Data.SqlClient.SqlParameter("@cutoff", cutoff));

    private static readonly string CandidateSql = $$"""
        SELECT job.Id AS JobId, physical.Id AS PhysicalId
        FROM Jobs job
        INNER JOIN PipelineRecords record ON record.Id = job.PipelineRecordId AND record.Revision = job.SourceRevision
        INNER JOIN SourceRevisions input ON input.Id = record.SourceRevisionId
        LEFT JOIN SourceRevisions parent ON parent.Id = input.ParentSourceRevisionId AND parent.SourceRootId = input.SourceRootId AND input.OriginKind <> 0
        LEFT JOIN SourceRevisions archive ON archive.Id = parent.ParentSourceRevisionId AND archive.SourceRootId = input.SourceRootId AND parent.OriginKind = 1
        INNER JOIN SourceRevisions physical ON physical.Id = CASE
            WHEN input.OriginKind = 0 THEN input.Id
            WHEN input.OriginKind IN (1,2,3) AND parent.OriginKind = 0 THEN parent.Id
            WHEN input.OriginKind IN (2,3) AND parent.OriginKind = 1 AND archive.OriginKind = 0 THEN archive.Id
            END
        INNER JOIN SourceRootConfigurations root ON root.Id = physical.SourceRootId
        WHERE root.State = {{(int)SourceRootState.Enabled}} AND physical.SuppressedAtUtc <= @cutoff
          AND job.PublicState IN ({{(int)PublicJobState.Completed}}, {{(int)PublicJobState.Failed}})
          AND job.LeaseOwner IS NULL AND job.LeaseExpiresAtUtc IS NULL
          AND {{SqlRemovedSourceWork.RemovalWitness("physical", includeLegacy: false)}}
        """;

    private static bool MatchesWitness(SourceRevisionEntity physical, SourceRootConfiguration root, string relative)
    {
        if (root.PhysicalIdentityFingerprint is null || physical.RetentionEvidenceJson is null) return false;
        try
        {
            using var witness = JsonDocument.Parse(physical.RetentionEvidenceJson);
            var proof = witness.RootElement;
            if (proof.ValueKind != JsonValueKind.Object ||
                !proof.TryGetProperty("RelativePath", out var path) || path.ValueKind != JsonValueKind.String ||
                !string.Equals(path.GetString(), relative, StringComparison.OrdinalIgnoreCase) ||
                !proof.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var format) ||
                !proof.TryGetProperty("ConfigurationRevision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
                !revision.TryGetInt64(out var generation) || generation > root.ConfigurationRevision) return false;
            // Guarded witnesses pin the physical root across configuration changes.
            // Older typed scan witnesses have no physical identity of their own.
            return format == 2
                ? proof.TryGetProperty("PhysicalIdentityFingerprint", out var identity) && identity.ValueKind == JsonValueKind.String &&
                    identity.GetString() == root.PhysicalIdentityFingerprint
                : format == 1 && generation == root.ConfigurationRevision;
        }
        catch (JsonException) { return false; }
    }

    private static IQueryable<Guid> RetirableJobs(FluxKnowledgeDbContext db, Guid[] candidates,
        SourceRevisionEntity physical, DateTimeOffset now)
    {
        var unconfirmed = SqlRemovedSourceWork.UnconfirmedJobIds(db);
        var recoveryWindow = physical.RetainUntilUtc is null || physical.RetainUntilUtc > now;
        return db.Jobs.Where(job => candidates.Contains(job.Id) &&
            // Git selection has a retained revision deadline. Filesystem capture
            // can reactivate an exact revision afterwards and reuse its record.
            (!recoveryWindow || (physical.SourceRoot.CrawlMode != (int)SourceDiscoveryMode.GitTracked &&
                job.PipelineRecord.RepositoryRecoveryBindingJson == null)) &&
            (job.PipelineRecord.CompletionCriteriaMet || job.PipelineRecord.IsDeleted ||
                !db.SourceActivities.Any(activity => activity.ResultingPipelineRecordId == job.PipelineRecordId &&
                    activity.ResultingPipelineRecordRevision == job.SourceRevision)) &&
            !db.Jobs.Any(peer => peer.PipelineRecordId == job.PipelineRecordId && peer.SourceRevision == job.SourceRevision &&
                (peer.PublicState != (int)PublicJobState.Completed && peer.PublicState != (int)PublicJobState.Failed ||
                    peer.LeaseOwner != null || peer.LeaseExpiresAtUtc != null || unconfirmed.Contains(peer.Id))) &&
            !db.OutboxMessages.Any(delivery => delivery.JobId == job.Id &&
                (delivery.LeaseOwner != null || delivery.LeaseExpiresAtUtc != null ||
                    delivery.PipelineRecordId != job.PipelineRecordId || delivery.SourceRevision != job.SourceRevision ||
                    delivery.Stage != job.Stage || EF.Functions.Collate(delivery.Operation, SchemaConfiguration.SchedulerFenceCollation) !=
                        EF.Functions.Collate(job.Operation, SchemaConfiguration.SchedulerFenceCollation))) &&
            !db.GpuMiniTasks.Any(task => task.ParentJobId == job.Id &&
                (task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed &&
                    task.ExecutionState != (int)GpuMiniTaskExecutionState.Cancelled ||
                    db.GpuExecutorDispatches.Any(dispatch => dispatch.BatchId == task.BatchId &&
                        (db.NativeWorkerInstances.Any(worker => worker.ActiveDispatchId == dispatch.DispatchId) ||
                         db.GpuModelResidencies.Any(model => model.BorrowedDispatchId == dispatch.DispatchId))) ||
                    db.GpuExecutorResultReceipts.Any(receipt => receipt.MiniTaskId == task.Id &&
                        (receipt.BatchId != task.BatchId || receipt.AdmissionGeneration != task.AdmissionGeneration ||
                         receipt.Dispatch.BatchId != receipt.BatchId || receipt.Dispatch.AdmissionGeneration != receipt.AdmissionGeneration ||
                         receipt.Dispatch.ExecutorKey != receipt.ExecutorKey)))) &&
            !db.IndexGenerations.Any(draft => draft.EmbeddingJobId == job.Id &&
                (db.IndexState.Any(state => state.ActiveIndexGenerationId == draft.Id) ||
                    draft.RetiredAtUtc == null && (job.Stage != (int)PipelineStage.Embed || draft.IndexPath != "" ||
                        draft.FormatVersion != 1 || draft.ValidatedAtUtc != null) ||
                    db.IndexPublicationCandidates.Any(candidate => candidate.DraftGenerationId == draft.Id && candidate.State < 2) ||
                    // A later source purge locates a draft by its job or its
                    // retained Embed artifact. Keep its owner if it is the only anchor.
                    !db.Artifacts.Any(artifact => artifact.PipelineRecordId == job.PipelineRecordId &&
                        artifact.SourceRevision == job.SourceRevision && artifact.Stage == (int)PipelineStage.Embed &&
                        artifact.ContentType == EmbedDraftDefaults.ArtifactContentType &&
                        EF.Functions.Collate(artifact.SearchText, "Latin1_General_100_CI_AS") == draft.Id.ToString()))) &&
            !db.IndexPublicationCandidates.Any(candidate => candidate.OwnerJobId == job.Id && candidate.State < 2) &&
            !db.EmbeddingGpuRequests.Any(request =>
                (request.ParentJobId == job.Id || db.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && task.ParentJobId == job.Id)) &&
                (request.ParentJobId != job.Id || request.PipelineRecordId != job.PipelineRecordId || request.SourceRevision != job.SourceRevision ||
                    !db.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && task.ParentJobId == job.Id))) &&
            !db.DocumentOcrRequests.Any(request =>
                (request.ParentJobId == job.Id || db.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && task.ParentJobId == job.Id)) &&
                (request.ParentJobId != job.Id || request.PipelineRecordId != job.PipelineRecordId || request.SourceRevision != job.SourceRevision ||
                    request.RetainedSourceRevisionId != job.PipelineRecord.SourceRevisionId ||
                    !db.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && task.ParentJobId == job.Id))) &&
            !db.MediaTranscriptionGpuRequests.Any(request =>
                (request.ParentJobId == job.Id || db.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && task.ParentJobId == job.Id)) &&
                (request.ParentJobId != job.Id || request.PipelineRecordId != job.PipelineRecordId || request.SourceRevision != job.SourceRevision ||
                    request.RetainedSourceRevisionId != job.PipelineRecord.SourceRevisionId ||
                    !db.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId && task.ParentJobId == job.Id))))
            .Select(job => job.Id);
    }

    private static async Task DeleteDependantsAsync(FluxKnowledgeDbContext db, Guid[] jobs, DateTimeOffset now, CancellationToken ct)
    {
        var tasks = db.GpuMiniTasks.Where(task => task.ParentJobId.HasValue && jobs.Contains(task.ParentJobId.Value));
        var taskIds = tasks.Select(task => task.Id);
        var touchedBatches = await tasks.Where(task => task.BatchId.HasValue).Select(task => task.BatchId!.Value)
            .Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        // The scheduler's capacity, dispatch and batch-wide evidence can be
        // shared. Remove settled task rows first, then reclaim exclusive batches.
        await db.GpuExecutorResultReceipts.Where(x => taskIds.Contains(x.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.EmbeddingGpuRequests.Where(x => taskIds.Contains(x.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.DocumentOcrRequests.Where(x => taskIds.Contains(x.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.MediaTranscriptionGpuRequests.Where(x => taskIds.Contains(x.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await tasks.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (touchedBatches.Length > 0)
        {
            var empty = await UnreferencedBatches(db, touchedBatches).ToArrayAsync(ct).ConfigureAwait(false);
            await SqlSourceDeletionStore.DeleteEmptyExecutionBatchesAsync(db, empty, ct).ConfigureAwait(false);
        }

        var deliveries = db.OutboxMessages.Where(x => x.JobId.HasValue && jobs.Contains(x.JobId.Value));
        await deliveries.Where(x => x.DispatchedAtUtc == null).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await deliveries.Where(x => x.DispatchedAtUtc != null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.JobId, (Guid?)null), ct).ConfigureAwait(false);
        await db.IndexPublicationCandidates.Where(x => x.OwnerJobId.HasValue && jobs.Contains(x.OwnerJobId.Value))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RetiredOwnerJobId, x => x.OwnerJobId)
                .SetProperty(x => x.OwnerJobId, (Guid?)null), ct).ConfigureAwait(false);
        var drafts = db.IndexGenerations.Where(x => x.EmbeddingJobId.HasValue && jobs.Contains(x.EmbeddingJobId.Value));
        var draftIds = drafts.Select(x => x.Id);
        await db.IndexPublicationCandidates.Where(x => x.DraftGenerationId.HasValue && draftIds.Contains(x.DraftGenerationId.Value))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RetiredDraftGenerationId, x => x.DraftGenerationId)
                .SetProperty(x => x.DraftGenerationId, (Guid?)null), ct).ConfigureAwait(false);
        await drafts.ExecuteUpdateAsync(set => set.SetProperty(x => x.RetiredAtUtc, x => x.RetiredAtUtc ?? now)
            .SetProperty(x => x.EmbeddingJobId, (Guid?)null), ct).ConfigureAwait(false);
        // Completed attempts cascade with their exact job. Retained content,
        // pipeline records and independent operation receipts have their own lifecycle.
    }

    private static IQueryable<Guid> UnreferencedBatches(FluxKnowledgeDbContext db, Guid[] touched) =>
        db.GpuBatches.Where(batch => touched.Contains(batch.Id) &&
            !db.GpuMiniTasks.Any(task => task.BatchId == batch.Id) &&
            !db.GpuCapacitySlots.Any(slot => slot.ActiveBatchId == batch.Id) &&
            !db.GpuModelDetachmentReceipts.Any(receipt => receipt.BatchId == batch.Id) &&
            !db.DocumentOcrRequests.Any(request => request.ExecutionBatchId == batch.Id) &&
            !db.MediaTranscriptionGpuRequests.Any(request => request.ExecutionBatchId == batch.Id) &&
            !db.GpuExecutorResultReceipts.Any(result => result.BatchId == batch.Id || result.Dispatch.BatchId == batch.Id) &&
            !db.GpuExecutorEvidence.Any(evidence => evidence.BatchId == batch.Id && evidence.Dispatch.BatchId != batch.Id) &&
            !db.GpuExecutorDispatches.Any(dispatch => dispatch.BatchId == batch.Id &&
                (db.GpuExecutorEvidence.Any(evidence => evidence.DispatchId == dispatch.DispatchId && evidence.BatchId != batch.Id) ||
                 db.EmbeddingGpuRequests.Any(request => request.DispatchId == dispatch.DispatchId ||
                     request.ResidentDetachmentDispatchId == dispatch.DispatchId) ||
                 db.DocumentOcrRequests.Any(request => request.ExecutionDispatchId == dispatch.DispatchId) ||
                 db.MediaTranscriptionGpuRequests.Any(request => request.ExecutionDispatchId == dispatch.DispatchId) ||
                 db.GpuModelDetachmentReceipts.Any(receipt => receipt.DispatchId == dispatch.DispatchId))))
            .Select(batch => batch.Id);
}
