using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Registers exact, approved retained physical media for the native ASR stage.</summary>
public sealed class SqlMediaTranscriptionRegistrationStore(
    IDbContextFactory<FluxKnowledgeDbContext> factory,
    ISourceWorkAdmission admission,
    ISourceObservationReader observations,
    IRetainedSourceReader retainedSources,
    TimeProvider clock)
{
    private const string RuntimeUnavailable = "media-transcription-runtime-unavailable";
    private const string OwnershipUnconfirmed = "media-source-current-ownership-unconfirmed";
    private const string ArtifactUnavailable = "media-retained-input-unavailable";
    private const string SignatureMismatch = "media-transcription-format-unverified";
    private const string SidecarUnverified = "media-adjacent-transcript-unverified";
    private const string WorkConflict = "media-transcription-existing-work-conflict";
    private const string RetainedSourceKind = "retained local source";
    private const int SignaturePrefixLength = 4096;
    private static readonly TimeSpan HoldRetryDelay = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] Extensions = [".mp4", ".mov", ".m4v", ".mp3", ".wav"];

    public async ValueTask<MediaTranscriptionDiscoveryRun> RunOnceAsync(
        MediaTranscriptionDiscoveryOptions options, bool runtimeReady, CancellationToken cancellationToken)
    {
        options.Validate();
        if (options.SourceRootIds.Count == 0) return new(0, 0, 0, 0);

        var now = clock.GetUtcNow();
        await using var probe = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var explicitIds = options.CatchUpRevisionIds.ToArray();
        var candidates = await Candidates(probe, options)
            .Where(source => !probe.SourceActivities.Any(activity =>
                activity.SourceRevisionId == source.Id &&
                activity.ActivityKind == (int)SourceActivityKind.MediaTranscription &&
                activity.ProcessorVersion == MediaTranscriptionDiscoveryOptions.ProcessorVersion &&
                activity.DescriptorFingerprint == MediaTranscriptionDiscoveryOptions.DescriptorFingerprint &&
                activity.InputFingerprint == EF.Functions.Collate(source.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                (activity.ResultingPipelineRecordId != null ||
                 activity.State == (int)SourceActivityState.DeferredPolicy &&
                 (activity.Reason == RuntimeUnavailable ? !runtimeReady : activity.UpdatedAtUtc > now - HoldRetryDelay))))
            .OrderByDescending(source => explicitIds.Contains(source.Id))
            .ThenBy(source => source.DiscoveredAtUtc)
            .ThenBy(source => source.Id)
            .Select(source => source.Id)
            .Take(MediaTranscriptionDiscoveryOptions.MaximumBatchSize)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        var queued = 0;
        var held = 0;
        var existing = 0;
        var strategy = probe.Database.CreateExecutionStrategy();
        foreach (var id in candidates)
        {
            switch (await strategy.ExecuteAsync(() => RegisterOnceAsync(options, id, runtimeReady, cancellationToken)).ConfigureAwait(false))
            {
                case Disposition.Queued: queued++; break;
                case Disposition.Held: held++; break;
                case Disposition.Existing: existing++; break;
            }
        }
        return new(candidates.Length, queued, held, existing);
    }

    private IQueryable<SourceRevisionEntity> Candidates(FluxKnowledgeDbContext db, MediaTranscriptionDiscoveryOptions options)
    {
        var approvedRoots = options.SourceRootIds.ToArray();
        var explicitIds = options.CatchUpRevisionIds.ToArray();
        var sql = "SELECT [mediaSource].* FROM [SourceRevisions] AS [mediaSource] WHERE " +
            SqlSourceWorkEligibility.Source("[mediaSource].[Id]", checkAllPending: true);
        return from source in db.SourceRevisions.FromSqlRaw(sql, SqlSourceWorkEligibility.AdmissionParameter(admission))
               join root in db.SourceRootConfigurations on source.SourceRootId equals root.Id
               where approvedRoots.Contains(root.Id) &&
                   root.State == (int)SourceRootState.Enabled &&
                   root.CrawlMode == (int)SourceDiscoveryMode.Filesystem && !root.FollowLinks &&
                   !db.OutlookCaptureProfiles.Any(profile => profile.SourceRootId == root.Id ||
                       profile.WatchedSourceRootId == root.Id) &&
                   source.OriginKind == 0 &&
                   source.Classification == nameof(SourceClassification.DeferredCapability) &&
                   Extensions.Contains(source.Extension.ToLower()) &&
                   (source.DiscoveredAtUtc >= options.AfterUtc || explicitIds.Contains(source.Id))
               select source;
    }

    private async Task<Disposition> RegisterOnceAsync(MediaTranscriptionDiscoveryOptions options, Guid id, bool runtimeReady,
        CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
        await SqlPublishedPassageSelection.AcquireFenceAsync(db, token).ConfigureAwait(false);
        var source = await Candidates(db, options).SingleOrDefaultAsync(candidate => candidate.Id == id, token).ConfigureAwait(false);
        if (source is null || await db.IndexState.AnyAsync(state => state.Id == 1 && state.CorpusRebuildOperationId != null, token)
                .ConfigureAwait(false)) return Disposition.None;

        var activity = await db.SourceActivities.SingleOrDefaultAsync(value =>
            value.SourceRevisionId == source.Id && value.ActivityKind == (int)SourceActivityKind.MediaTranscription &&
            value.ProcessorVersion == MediaTranscriptionDiscoveryOptions.ProcessorVersion &&
            value.DescriptorFingerprint == MediaTranscriptionDiscoveryOptions.DescriptorFingerprint &&
            value.InputFingerprint == source.ContentSha256, token).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (activity is { State: (int)SourceActivityState.DeferredPolicy } &&
            (activity.Reason == RuntimeUnavailable ? !runtimeReady : activity.UpdatedAtUtc > now - HoldRetryDelay))
            return Disposition.None;

        var linkedRecord = await db.PipelineRecords
            .FromSqlInterpolated($"SELECT * FROM [PipelineRecords] WITH (UPDLOCK,HOLDLOCK,INDEX([IX_PipelineRecords_SourceRevisionId])) WHERE [SourceRevisionId] = {source.Id}")
            .SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (linkedRecord is not null)
        {
            if (!linkedRecord.IsDeleted && activity?.ResultingPipelineRecordId == linkedRecord.Id &&
                activity.ResultingPipelineRecordRevision == linkedRecord.Revision &&
                linkedRecord.ContentHash == source.ContentSha256 &&
                await db.Jobs.AnyAsync(job => job.PipelineRecordId == linkedRecord.Id &&
                    job.SourceRevision == linkedRecord.Revision && job.Stage == (int)PipelineStage.Extract &&
                    job.Operation == MediaTranscriptionDiscoveryOptions.Operation, token).ConfigureAwait(false))
                return Disposition.Existing;
            return activity?.ResultingPipelineRecordId is null
                ? await HoldAsync(db, transaction, source, activity, WorkConflict, now, token).ConfigureAwait(false)
                : Disposition.None;
        }
        if (activity is not null && (activity.ResultingPipelineRecordId is not null ||
            activity.State != (int)SourceActivityState.DeferredPolicy || activity.AttemptCount != 0 ||
            activity.LastAttemptAtUtc is not null || activity.AttemptEvidenceJson is not null ||
            activity.ExecutionClass != (int)ExecutionClass.InProcess ||
            activity.RequiredCapability != MediaTranscriptionDiscoveryOptions.Capability)) return Disposition.None;

        var root = await db.SourceRootConfigurations.SingleAsync(root => root.Id == source.SourceRootId, token).ConfigureAwait(false);
        var configuration = SqlSourceRemovalReconciler.Restore(root);
        var relative = Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath);
        var capture = ReadCapture(source.CurrentDiscoveryEvidenceJson);
        if (capture is null || capture.Kind != "verified-physical-capture" ||
            capture.SourceRevisionId != source.Id || capture.ConfigurationRevision != root.ConfigurationRevision ||
            configuration.PhysicalIdentityFingerprint is null || capture.RootIdentityFingerprint != configuration.PhysicalIdentityFingerprint ||
            capture.ScanRequestId == Guid.Empty || capture.LeaseGeneration <= 0 || capture.ObservedAt == default || capture.ObservedAt > now ||
            string.IsNullOrWhiteSpace(capture.StableSourceIdentity) || capture.ContentSha256 != source.ContentSha256 ||
            capture.ByteLength != source.ByteLength ||
            capture.LastWriteAtUtc <= DateTimeOffset.FromFileTime(0) ||
            source.ByteLength <= 0 ||
            !IsAllowedPath(configuration, relative) ||
            !await db.SourceScanJobs.AnyAsync(scan => scan.SourceScanRequestId == capture.ScanRequestId &&
                scan.LeaseGeneration == capture.LeaseGeneration && scan.SourceScanRequest.SourceRootId == root.Id, token).ConfigureAwait(false) ||
            await db.SourceRevisions.AnyAsync(other => other.Id != source.Id && other.SourceRootId == root.Id &&
                other.OriginKind == 0 && other.SuppressedAtUtc == null &&
                (EF.Functions.Collate(other.CanonicalPath, "Latin1_General_100_CI_AS") == source.CanonicalPath ||
                 other.StableSourceIdentity == source.StableSourceIdentity || other.StableSourceIdentity == capture.StableSourceIdentity), token).ConfigureAwait(false))
            return await HoldAsync(db, transaction, source, activity, OwnershipUnconfirmed, now, token).ConfigureAwait(false);

        var artifact = await db.SourceArtifacts.SingleOrDefaultAsync(artifact => artifact.SourceRevisionId == source.Id, token).ConfigureAwait(false);
        var hash = source.ContentSha256;
        if (artifact is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) ||
            artifact.ContentSha256 != hash || artifact.ByteLength != source.ByteLength ||
            artifact.ReferenceCount <= 0 || artifact.ChecksumVerifiedAtUtc == default ||
            !string.Equals(artifact.StoreRelativePath, Path.Combine("sha256", hash[..2], $"{hash}.bin"), StringComparison.OrdinalIgnoreCase))
            return await HoldAsync(db, transaction, source, activity, ArtifactUnavailable, now, token).ConfigureAwait(false);

        using var physical = TryOpenPhysical(configuration, relative);
        if (physical is null || physical.Observation.LinkCount != 1 ||
            physical.Observation.Identity != capture.StableSourceIdentity ||
            physical.Observation.ByteLength != capture.ByteLength ||
            physical.Observation.LastWriteTicks != capture.LastWriteAtUtc.UtcDateTime.ToFileTimeUtc())
            return await HoldAsync(db, transaction, source, activity, OwnershipUnconfirmed, now, token).ConfigureAwait(false);

        if (source.ByteLength > MediaTranscriptionDiscoveryOptions.MaximumMediaBytes)
            return await HoldAsync(db, transaction, source, activity, MediaTranscriptionDiscoveryOptions.InputTooLargeReason,
                now, token).ConfigureAwait(false);
        var formatFailure = await InspectFormatAsync(source, token).ConfigureAwait(false);
        if (formatFailure is not null)
            return await HoldAsync(db, transaction, source, activity, formatFailure, now, token).ConfigureAwait(false);
        using var vtt = TryOpenAbsence(configuration, Path.ChangeExtension(relative, ".vtt"));
        using var srt = TryOpenAbsence(configuration, Path.ChangeExtension(relative, ".srt"));
        using var text = TryOpenAbsence(configuration, Path.ChangeExtension(relative, ".txt"));
        using var transcript = TryOpenAbsence(configuration, relative + MediaTranscriptionGpuContracts.SidecarSuffix);
        if (vtt is null || srt is null || text is null || transcript is null)
            return await HoldAsync(db, transaction, source, activity, SidecarUnverified, now, token).ConfigureAwait(false);
        if (!runtimeReady)
            return await HoldAsync(db, transaction, source, activity, RuntimeUnavailable, now, token).ConfigureAwait(false);

        await QueueAsync(db, source, activity, now, token).ConfigureAwait(false);
        try
        {
            physical.ValidateCurrent();
            vtt.ValidateCurrent();
            srt.ValidateCurrent();
            text.ValidateCurrent();
            transcript.ValidateCurrent();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The uncommitted job and outbox roll back together. The next pass sees the new observation.
            return Disposition.None;
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Disposition.Queued;
    }

    private static bool IsAllowedPath(SourceRootConfiguration configuration, string relative) =>
        relative is not "." and not ".." && !Path.IsPathRooted(relative) &&
        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        (configuration.Recursive || relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0) &&
        LocalSourceEnumerator.MatchesPolicy(relative, configuration.IncludePatterns, configuration.ExcludePatterns);

    private async Task<string?> InspectFormatAsync(SourceRevisionEntity source, CancellationToken token)
    {
        try
        {
            await using var retained = await retainedSources.OpenReadAsync(new SourceRevisionId(source.Id), MediaTranscriptionDiscoveryOptions.MaximumMediaBytes, token).ConfigureAwait(false);
            if (retained.SourceRevisionId.Value != source.Id || retained.ContentSha256 != source.ContentSha256 ||
                retained.ByteLength != source.ByteLength) return ArtifactUnavailable;
            var prefix = new byte[(int)Math.Min(SignaturePrefixLength, retained.ByteLength)];
            await retained.Stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
            return MediaMetadataSignature.TryGetFormatForExtension(source.Extension, out var expected) &&
                MediaMetadataSignature.TryDetect(prefix, token, out var actual) && expected == actual ? null : SignatureMismatch;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ArtifactUnavailable;
        }
    }

    private ISourceObservationLease? TryOpenPhysical(SourceRootConfiguration root, string relative)
    {
        try { return observations.Open(root, relative); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private ISourceAbsenceLease? TryOpenAbsence(SourceRootConfiguration root, string relative)
    {
        try { return observations.OpenAbsence(root, relative); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private async Task<Disposition> HoldAsync(FluxKnowledgeDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        SourceRevisionEntity source, SourceActivityEntity? activity, string reason, DateTimeOffset now, CancellationToken token)
    {
        if (activity is { ResultingPipelineRecordId: not null } || activity is not null &&
            (activity.State != (int)SourceActivityState.DeferredPolicy || activity.AttemptCount != 0))
            return Disposition.None;
        if (activity is null)
            await WriteActivityAsync(db, source, SourceActivityState.DeferredPolicy, reason, token).ConfigureAwait(false);
        else
        {
            activity.Reason = reason;
            activity.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Disposition.Held;
    }

    private async Task QueueAsync(FluxKnowledgeDbContext db, SourceRevisionEntity source, SourceActivityEntity? activity, DateTimeOffset now,
        CancellationToken token)
    {
        var identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.StableSourceIdentity)));
        var stableKey = $"retained:{source.SourceRootId:N}:{identityHash}";
        var identity = await db.SourceIdentities
            .FromSqlInterpolated($"SELECT [Id], [SourceKind], [StableKey], [CreatedAtUtc] FROM [SourceIdentities] WITH (UPDLOCK,HOLDLOCK) WHERE [SourceKind] = {RetainedSourceKind} AND [StableKey] = {stableKey}")
            .SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (identity is null)
        {
            identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = RetainedSourceKind, StableKey = stableKey, CreatedAtUtc = now };
            db.SourceIdentities.Add(identity);
        }
        var latest = await db.PipelineRecords.Where(record => record.SourceIdentityId == identity.Id)
            .OrderByDescending(record => record.Revision).FirstOrDefaultAsync(token).ConfigureAwait(false);
        var recordId = Guid.NewGuid();
        var revision = (latest?.Revision ?? 0) + 1;
        db.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = identity.Id,
            SourceRevisionId = source.Id,
            Revision = revision,
            ContentHash = source.ContentSha256,
            RootLineageRecordId = latest?.RootLineageRecordId ?? recordId,
            ParentRevisionRecordId = latest?.Id,
            CurrentStage = (int)PipelineStage.Extract,
            RegisteredAtUtc = now
        });
        activity ??= await WriteActivityAsync(db, source, SourceActivityState.Pending, null, token).ConfigureAwait(false);
        activity.State = (int)SourceActivityState.Pending;
        activity.Reason = null;
        activity.ResultingPipelineRecordId = recordId;
        activity.ResultingPipelineRecordRevision = revision;
        activity.UpdatedAtUtc = now;
        var jobId = Guid.NewGuid();
        db.Jobs.Add(new JobEntity
        {
            Id = jobId,
            PipelineRecordId = recordId,
            SourceRevision = revision,
            Stage = (int)PipelineStage.Extract,
            Operation = MediaTranscriptionDiscoveryOptions.Operation,
            PublicState = (int)PublicJobState.WorkerQueued,
            DueAtUtc = now
        });
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            PipelineRecordId = recordId,
            SourceRevision = revision,
            Stage = (int)PipelineStage.Extract,
            Operation = MediaTranscriptionDiscoveryOptions.Operation,
            DispatchGeneration = 0,
            IdempotencyKey = SqlPipelineStore.CreateIdempotencyKey(recordId, revision, PipelineStage.Extract, 0),
            DueAtUtc = now,
            CreatedAtUtc = now
        });
        OperatorEventAppender.Add(db, new OperatorEventDraft(
            "pipeline.registered", "pipeline", "information", "native-media-discovery", now,
            PipelineRecordId: recordId, SourceRootId: source.SourceRootId, SourceRevisionId: source.Id,
            SourceActivityId: activity.Id, CorrelationId: $"source:{source.Id:N}",
            Details: new { revision, sourceActivity = true }));
        await db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private async ValueTask<SourceActivityEntity> WriteActivityAsync(FluxKnowledgeDbContext db, SourceRevisionEntity source,
        SourceActivityState state, string? reason, CancellationToken token)
    {
        var activity = await new SqlSourceActivityWriter(clock).FindOrCreateAsync(db,
            new(new SourceRevisionId(source.Id), SourceActivityKind.MediaTranscription, ExecutionClass.InProcess,
                MediaTranscriptionDiscoveryOptions.ProcessorVersion, source.ContentSha256,
                MediaTranscriptionDiscoveryOptions.Capability, reason, state,
                MediaTranscriptionDiscoveryOptions.DescriptorFingerprint), token).ConfigureAwait(false);
        return db.SourceActivities.Local.Single(value => value.Id == activity.Id.Value);
    }

    internal static PhysicalCapture? ReadCapture(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<PhysicalCapture>(json, EvidenceJson); }
        catch (JsonException) { return null; }
    }

    private enum Disposition { None, Queued, Held, Existing }

    internal sealed record PhysicalCapture(string Kind, Guid SourceRevisionId, long ConfigurationRevision, Guid ScanRequestId,
        long LeaseGeneration, string RootIdentityFingerprint, string StableSourceIdentity, string ContentSha256,
        long ByteLength, DateTimeOffset LastWriteAtUtc, DateTimeOffset ObservedAt);
}
