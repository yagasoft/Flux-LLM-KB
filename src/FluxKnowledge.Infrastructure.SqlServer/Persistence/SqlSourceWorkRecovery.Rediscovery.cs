using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceWorkRecovery
{
    private const string Removed = "source-removed";
    private const int RediscoveryPageSize = 16;
    private static readonly TimeSpan RediscoveryFallbackInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RediscoveryRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions DiscoveryJson = new(JsonSerializerDefaults.Web);

    private async Task ResumeRediscoveredExtractionAsync(CancellationToken token)
    {
        if (!await _progress.RediscoveryOwner.WaitAsync(0, token).ConfigureAwait(false)) return;
        var restored = false;
        try
        {
            var now = clock.GetUtcNow();
            if (now < _progress.RediscoveryRetryAt || admission.ReadAdmittedRoots().Count == 0) return;
            var sweep = _progress.ActiveRediscovery ??= _progress.StartRediscovery(now);
            if (sweep is null) return;

            await using var lookup = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
            var query = RediscoveryCandidates(lookup);
            if (sweep.RootId is { } rootId)
                query = query.Where(candidate => candidate.Source.SourceRootId == rootId);
            if (sweep.After is { } after)
                query = query.Where(candidate => candidate.Job.Id.CompareTo(after) > 0);
            var jobs = await query.TagWith("source-rediscovery-candidate-page").OrderBy(candidate => candidate.Job.Id)
                .Select(candidate => candidate.Job.Id).Take(RediscoveryPageSize).ToArrayAsync(token).ConfigureAwait(false);
            foreach (var id in jobs)
            {
                try
                {
                    restored |= await lookup.Database.CreateExecutionStrategy()
                        .ExecuteAsync(() => ResumeRediscoveredExtractionOnceAsync(id, token)).ConfigureAwait(false);
                }
                finally
                {
                    // Let later candidates progress after an individual failure. The
                    // failed row still has its durable proofs and is revisited on fallback.
                    if (!token.IsCancellationRequested) sweep.After = id;
                }
            }
            if (jobs.Length == RediscoveryPageSize) return;
            if (sweep.RootId is null)
                _progress.NextRediscoveryFallback = clock.GetUtcNow() + RediscoveryFallbackInterval;
            _progress.ActiveRediscovery = null;
        }
        catch when (!token.IsCancellationRequested)
        {
            _progress.RediscoveryRetryAt = clock.GetUtcNow() + RediscoveryRetryDelay;
            throw;
        }
        finally
        {
            _progress.RediscoveryOwner.Release();
            if (restored)
            {
                outboxWake?.Notify();
                if (statusEvents is not null)
                    await statusEvents.PublishAsync(new StatusChanged(null, "sources", clock.GetUtcNow()), CancellationToken.None)
                        .ConfigureAwait(false);
            }
        }
    }

    private IQueryable<RediscoveredExtraction> RediscoveryCandidates(FluxKnowledgeDbContext db) =>
        from job in db.Jobs
        join record in SqlSourceWorkEligibility.Records(db, admission, checkAllPending: true)
            on job.PipelineRecordId equals record.Id
        join source in db.SourceRevisions on record.SourceRevisionId equals source.Id
        join root in db.SourceRootConfigurations on source.SourceRootId equals root.Id
        where job.PublicState == (int)PublicJobState.Failed && job.Reason == Removed && job.ErrorDetails == null &&
              job.Stage == (int)PipelineStage.Extract && job.Operation == PipelineOperations.ExtractUtf8 &&
              job.AttemptCount == 0 && job.LeaseGeneration == 0 && job.LeaseOwner == null && job.LeaseExpiresAtUtc == null &&
              record.Revision == job.SourceRevision && record.CurrentStage == job.Stage && !record.CompletionCriteriaMet &&
              record.RepositoryRecoveryBindingJson == null &&
              source.OriginKind == 0 && source.Classification == "AcceptedUtf8Text" &&
              source.SuppressedAtUtc == null && source.CurrentDiscoveryEvidenceJson != null &&
              record.ContentHash == source.ContentSha256 && root.State == (int)SourceRootState.Enabled &&
              root.CrawlMode == (int)SourceDiscoveryMode.Filesystem && !root.FollowLinks
        select new RediscoveredExtraction { Job = job, Record = record, Source = source, Root = root };

    private async Task<bool> ResumeRediscoveredExtractionOnceAsync(Guid jobId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
        await SqlPublishedPassageSelection.AcquireFenceAsync(db, token).ConfigureAwait(false);
        var candidate = await RediscoveryCandidates(db).SingleOrDefaultAsync(entry => entry.Job.Id == jobId, token).ConfigureAwait(false);
        if (candidate is null || await db.IndexState.AnyAsync(state => state.Id == 1 && state.CorpusRebuildOperationId != null, token)
                .ConfigureAwait(false)) return false;
        var job = candidate.Job;
        var record = candidate.Record;
        var source = candidate.Source;
        var root = candidate.Root;
        var activities = await db.SourceActivities.Where(activity => activity.ResultingPipelineRecordId == record.Id &&
                activity.ResultingPipelineRecordRevision == record.Revision)
            .Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (activities.Length != 1 || activities[0] is not { AttemptCount: 0, LastAttemptAtUtc: null,
                AttemptEvidenceJson: null, RequiredCapability: null, Reason: Removed,
                State: (int)SourceActivityState.CancelledSuperseded, ActivityKind: (int)SourceActivityKind.TextExtraction,
                ExecutionClass: (int)ExecutionClass.InProcess, ProcessorVersion: SourceScanWorker.TextProcessorVersion } activity ||
            activity.SourceRevisionId != source.Id || activity.InputFingerprint != source.ContentSha256 ||
            activity.DescriptorFingerprint != SourceActivityEntity.LegacyDescriptorFingerprint) return false;

        var now = clock.GetUtcNow();
        var configuration = SqlSourceRemovalReconciler.Restore(root);
        var capture = ReadCapture(source.CurrentDiscoveryEvidenceJson!);
        var relative = Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath);
        if (capture is null || capture.Kind != "verified-physical-capture" || capture.SourceRevisionId != source.Id ||
            capture.ConfigurationRevision != root.ConfigurationRevision || configuration.PhysicalIdentityFingerprint is null ||
            capture.RootIdentityFingerprint != configuration.PhysicalIdentityFingerprint || capture.ScanRequestId == Guid.Empty ||
            capture.LeaseGeneration <= 0 || capture.ObservedAt <= activity.UpdatedAtUtc || capture.ObservedAt > now ||
            capture.ContentSha256 != source.ContentSha256 || capture.ByteLength != source.ByteLength ||
            capture.StableSourceIdentity is not { Length: > 0 } || capture.LastWriteAtUtc <= DateTimeOffset.FromFileTime(0) ||
            source.ByteLength < 0 || source.ByteLength > Math.Min(configuration.MaximumFileBytes, SourcePolicyDefaults.MaximumTextBytes) ||
            (!configuration.Recursive && relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) ||
            !LocalSourceEnumerator.MatchesPolicy(relative, configuration.IncludePatterns, configuration.ExcludePatterns) ||
            configuration.AllowedClassifications.Count != 0 &&
                !configuration.AllowedClassifications.Contains("text/plain", StringComparer.OrdinalIgnoreCase) ||
            !await db.SourceScanJobs.AnyAsync(scan => scan.SourceScanRequestId == capture.ScanRequestId &&
                scan.LeaseGeneration == capture.LeaseGeneration && scan.SourceScanRequest.SourceRootId == root.Id, token).ConfigureAwait(false))
            return false;

        var artifact = await db.SourceArtifacts.AsNoTracking().SingleOrDefaultAsync(row => row.SourceRevisionId == source.Id, token)
            .ConfigureAwait(false);
        var hash = source.ContentSha256;
        if (artifact is null || hash.Length != 64 || artifact.ContentSha256 != hash || artifact.ByteLength != source.ByteLength ||
            artifact.ChecksumVerifiedAtUtc == default || artifact.ReferenceCount <= 0 ||
            !string.Equals(artifact.StoreRelativePath, Path.Combine("sha256", hash[..2], $"{hash}.bin"), StringComparison.OrdinalIgnoreCase) ||
            await HasConflictingRediscoveryEvidenceAsync(db, candidate, token).ConfigureAwait(false)) return false;

        var deliveries = await db.OutboxMessages.Where(delivery => delivery.JobId == job.Id ||
                delivery.PipelineRecordId == record.Id && delivery.SourceRevision == record.Revision)
            .Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (deliveries.Length != 1 || deliveries[0] is not { DispatchedAtUtc: null, LeaseOwner: null, LeaseExpiresAtUtc: null,
                LeaseGeneration: 0, DispatchGeneration: 0 } delivery ||
            delivery.JobId != job.Id || delivery.PipelineRecordId != record.Id || delivery.SourceRevision != record.Revision ||
            delivery.Stage != job.Stage || delivery.Operation != job.Operation || string.IsNullOrWhiteSpace(delivery.IdempotencyKey))
            return false;

        // Validate only this candidate against its retained capture. The
        // source handle supplies identity, size and write time without reading bytes.
        try
        {
            using var observation = observations.Open(configuration, relative);
            var current = observation.Observation;
            if (current.LinkCount != 1 || current.Identity != capture.StableSourceIdentity ||
                current.ByteLength != capture.ByteLength || current.LastWriteTicks != capture.LastWriteAtUtc.UtcDateTime.ToFileTimeUtc())
                return false;
            job.PublicState = (int)PublicJobState.WorkerQueued;
            job.Reason = null;
            job.DueAtUtc = now;
            delivery.DueAtUtc = now;
            activity.State = (int)SourceActivityState.Pending;
            activity.Reason = null;
            activity.UpdatedAtUtc = now;
            OperatorEventAppender.Add(db, new OperatorEventDraft("pipeline.rediscovered_extraction_queued", "pipeline",
                "information", "source-reconciliation", now, PipelineRecordId: record.Id,
                SourceRootId: root.Id, SourceRevisionId: source.Id, SourceActivityId: activity.Id,
                Details: new { jobId, deliveryId = delivery.Id, capture.ScanRequestId, capture.LeaseGeneration }));
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            observation.ValidateCurrent();
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> HasConflictingRediscoveryEvidenceAsync(FluxKnowledgeDbContext db,
        RediscoveredExtraction candidate, CancellationToken token)
    {
        var job = candidate.Job;
        var record = candidate.Record;
        var source = candidate.Source;
        var root = candidate.Root;
        return await db.SourceRevisions.AnyAsync(revision => revision.SourceRootId == root.Id && revision.Id != source.Id &&
                revision.OriginKind == 0 && revision.SuppressedAtUtc == null &&
                (revision.StableSourceIdentity == source.StableSourceIdentity ||
                 EF.Functions.Collate(revision.CanonicalPath, "Latin1_General_100_CI_AS") == source.CanonicalPath), token).ConfigureAwait(false) ||
            await db.Jobs.AnyAsync(other => other.PipelineRecordId == record.Id && other.Id != job.Id, token).ConfigureAwait(false) ||
            await db.JobAttempts.AnyAsync(attempt => attempt.JobId == job.Id, token).ConfigureAwait(false) ||
            await db.GpuMiniTasks.AnyAsync(task => task.ParentJobId == job.Id, token).ConfigureAwait(false) ||
            await db.EmbeddingGpuRequests.AnyAsync(request => request.ParentJobId == job.Id || request.PipelineRecordId == record.Id, token).ConfigureAwait(false) ||
            await db.DocumentOcrRequests.AnyAsync(request => request.ParentJobId == job.Id || request.PipelineRecordId == record.Id ||
                request.RetainedSourceRevisionId == source.Id, token).ConfigureAwait(false) ||
            await db.MediaTranscriptionGpuRequests.AnyAsync(request => request.ParentJobId == job.Id || request.PipelineRecordId == record.Id ||
                request.RetainedSourceRevisionId == source.Id, token).ConfigureAwait(false) ||
            await db.Artifacts.AnyAsync(artifact => artifact.PipelineRecordId == record.Id, token).ConfigureAwait(false) ||
            await db.CorpusRebuildSupersededJobs.AnyAsync(superseded => superseded.JobId == job.Id, token).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlySet<Guid>> ReadOwnersRequiringCaptureAsync(FluxKnowledgeDbContext db,
        SourceRootConfiguration root, CancellationToken token)
    {
        // Recovery is deliberately held while a scan still owns pending changes. Capture
        // refresh runs inside that scan, so candidate discovery here omits the pending fence.
        var rows = await (from job in db.Jobs.AsNoTracking()
            join record in db.PipelineRecords.AsNoTracking() on job.PipelineRecordId equals record.Id
            join source in db.SourceRevisions.AsNoTracking() on record.SourceRevisionId equals source.Id
            where source.SourceRootId == root.Id.Value && source.OriginKind == 0 && source.SuppressedAtUtc == null &&
                source.Classification == "AcceptedUtf8Text" && source.CurrentDiscoveryEvidenceJson != null &&
                !record.IsDeleted && !record.CompletionCriteriaMet && record.RepositoryRecoveryBindingJson == null &&
                record.Revision == job.SourceRevision && record.CurrentStage == job.Stage &&
                record.ContentHash == source.ContentSha256 && job.Stage == (int)PipelineStage.Extract &&
                job.Operation == PipelineOperations.ExtractUtf8 && job.PublicState == (int)PublicJobState.Failed &&
                job.Reason == Removed && job.AttemptCount == 0 && job.LeaseGeneration == 0 &&
                job.LeaseOwner == null && job.LeaseExpiresAtUtc == null && job.ErrorDetails == null
            select new { source.Id, Evidence = source.CurrentDiscoveryEvidenceJson! })
            .ToArrayAsync(token).ConfigureAwait(false);
        return rows.Where(row => ReadCapture(row.Evidence) is { Kind: "verified-physical-capture" } capture &&
                capture.SourceRevisionId == row.Id && capture.ConfigurationRevision < root.ConfigurationRevision)
            .Select(row => row.Id).ToHashSet();
    }

    private static PhysicalCapture? ReadCapture(string evidence)
    {
        try { return JsonSerializer.Deserialize<PhysicalCapture>(evidence, DiscoveryJson); }
        catch (JsonException) { return null; }
    }

    private sealed class RediscoveredExtraction
    {
        public required JobEntity Job { get; init; }
        public required PipelineRecordEntity Record { get; init; }
        public required SourceRevisionEntity Source { get; init; }
        public required SourceRootConfigurationEntity Root { get; init; }
    }
    private sealed record PhysicalCapture(string Kind, Guid SourceRevisionId, long ConfigurationRevision, Guid ScanRequestId,
        long LeaseGeneration, string RootIdentityFingerprint, string StableSourceIdentity, string ContentSha256,
        long ByteLength, DateTimeOffset LastWriteAtUtc, DateTimeOffset ObservedAt);
}
