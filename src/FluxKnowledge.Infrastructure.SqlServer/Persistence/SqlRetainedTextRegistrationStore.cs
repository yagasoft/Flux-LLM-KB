using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Atomically binds one retained text activity to the established pipeline Job/outbox path.</summary>
public sealed class SqlRetainedTextRegistrationStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider,
    string? retainedArtifactRoot = null,
    PersistedOutlookSpoolRootPolicy? outlookSpoolPolicy = null) : IRetainedTextRegistrationStore, IDeferredActivityReplayStore, ISourceActivityRestartStore
{
    private const string RetainedSourceKind = "retained local source";
    private const string AcceptedUtf8Classification = "AcceptedUtf8Text";
    private const string AcceptedMimePolicy = "[\"text/plain\"]";
    private const string ExtractUtf8OutputContract = "pipeline:extract-utf8";
    private readonly string? _retainedArtifactRoot = string.IsNullOrWhiteSpace(retainedArtifactRoot)
        ? null
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(retainedArtifactRoot));

    public async ValueTask<bool> RegisterAsync(SourceActivity activity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!IsSupported(activity))
        {
            return false;
        }

        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await RegisterOnceAsync(context, activity, null, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask<int> ReplayAsync(
        RegisteredSourceCapability capability,
        Guid? rootId,
        CancellationToken cancellationToken)
    {
        var activities = await ReadReplayRequestsAsync(capability, rootId, cancellationToken).ConfigureAwait(false);
        var replayed = 0;
        foreach (var activity in activities)
        {
            replayed += await ReplayActivityAsync(activity, capability, cancellationToken).ConfigureAwait(false);
        }

        return replayed;
    }

    public async ValueTask<int> OfferUnlinkedInProcessActivitiesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await (
            from activity in context.SourceActivities.AsNoTracking()
            join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
            join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
            where activity.State == (int)SourceActivityState.Pending &&
                activity.ExecutionClass == (int)ExecutionClass.InProcess &&
                activity.ResultingPipelineRecordId == null && revision.SuppressedAtUtc == null &&
                EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation)
            orderby activity.SourceRevisionId, activity.ActivityKind, activity.ProcessorVersion, activity.InputFingerprint
            select activity).ToListAsync(cancellationToken).ConfigureAwait(false);
        var offered = 0;
        foreach (var candidate in candidates)
        {
            var activity = SourceActivity.Restore(new SourceActivityId(candidate.Id), new SourceRevisionId(candidate.SourceRevisionId),
                (SourceActivityKind)candidate.ActivityKind, (ExecutionClass)candidate.ExecutionClass, candidate.ProcessorVersion,
                candidate.InputFingerprint, candidate.RequiredCapability, (SourceActivityState)candidate.State, candidate.Reason);
            if (await RegisterAsync(activity, cancellationToken).ConfigureAwait(false))
            {
                offered++;
            }
        }

        return offered;
    }

    public async ValueTask<int> ReplayActivityAsync(
        DeferredContentReplayRequest request,
        RegisteredSourceCapability capability,
        CancellationToken cancellationToken)
    {
        if (!Matches(request, capability))
        {
            return 0;
        }

        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        var registered = await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var durable = await context.SourceActivities.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == request.SourceActivityId, cancellationToken).ConfigureAwait(false);
            if (durable is null)
            {
                return false;
            }

            var activity = SourceActivity.Restore(
                new SourceActivityId(durable.Id),
                new SourceRevisionId(durable.SourceRevisionId),
                (SourceActivityKind)durable.ActivityKind,
                (ExecutionClass)durable.ExecutionClass,
                durable.ProcessorVersion,
                durable.InputFingerprint,
                durable.RequiredCapability,
                (SourceActivityState)durable.State,
                durable.Reason,
                durable.ResultingPipelineRecordId is not null && durable.ResultingPipelineRecordRevision is not null);
            if (!string.Equals(activity.IdempotencyKey, request.ActivityIdempotencyKey, StringComparison.Ordinal))
            {
                return false;
            }
            return await RegisterOnceAsync(context, activity, capability, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return registered ? 1 : 0;
    }

    private async Task<bool> RegisterOnceAsync(
        FluxKnowledgeDbContext context,
        SourceActivity activity,
        RegisteredSourceCapability? replayCapability,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        // Retained registration follows Task 4's source lock order before it locks an activity.
        var sourceRevision = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {activity.SourceRevisionId.Value}")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var artifact = await context.SourceArtifacts
            .FromSqlInterpolated($"SELECT * FROM [SourceArtifacts] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceArtifacts_SourceRevisionId])) WHERE [SourceRevisionId] = {activity.SourceRevisionId.Value}")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sourceRevision is null || sourceRevision.SuppressedAtUtc is not null || artifact is null ||
            !string.Equals(sourceRevision.Classification, AcceptedUtf8Classification, StringComparison.Ordinal) ||
            sourceRevision.ByteLength < 0 || sourceRevision.ByteLength > 16L * 1024 * 1024 ||
            artifact.ByteLength != sourceRevision.ByteLength ||
            !string.Equals(sourceRevision.ContentSha256, activity.InputFingerprint, StringComparison.Ordinal) ||
            !string.Equals(artifact.ContentSha256, sourceRevision.ContentSha256, StringComparison.Ordinal) ||
            !string.Equals(artifact.StoreRelativePath, Path.Combine("sha256", sourceRevision.ContentSha256[..2], $"{sourceRevision.ContentSha256}.bin"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A retained text activity requires its immutable source revision and artifact.");
        }

        if (replayCapability is not null)
        {
            var capability = await context.SourceCapabilities
                .FromSqlInterpolated($"SELECT * FROM [SourceCapabilities] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {replayCapability.Id}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (capability is null || capability.ExecutionClass != (int)ExecutionClass.InProcess || !capability.IsRunnable ||
                !string.Equals(capability.ProcessorKind, replayCapability.ProcessorKind, StringComparison.Ordinal) ||
                !string.Equals(capability.ProcessorVersion, replayCapability.ProcessorVersion, StringComparison.Ordinal) ||
                !string.Equals(capability.ProcessorFingerprint, replayCapability.ProcessorFingerprint, StringComparison.Ordinal) ||
                !string.Equals(capability.AcceptedClassificationsJson, AcceptedMimePolicy, StringComparison.Ordinal) ||
                !string.Equals(capability.OutputContract, ExtractUtf8OutputContract, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var deferredCapability = replayCapability is null
            ? null
            : await context.DeferredCapabilities
                .FromSqlInterpolated($"SELECT * FROM [DeferredCapabilities] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRevisionId] = {sourceRevision.Id} AND [ArtifactFingerprint] = {artifact.ContentSha256} AND [RequiredCapability] = {activity.RequiredCapability}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (replayCapability is not null &&
            (deferredCapability is null ||
             !string.Equals(deferredCapability.ArtifactFingerprint, sourceRevision.ContentSha256, StringComparison.Ordinal) ||
             !string.Equals(deferredCapability.RequiredCapability, replayCapability.ProcessorKind, StringComparison.Ordinal) ||
             (deferredCapability.ClaimedProcessorVersion is not null &&
              !string.Equals(deferredCapability.ClaimedProcessorVersion, replayCapability.ProcessorVersion, StringComparison.Ordinal))))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var durableActivity = await context.SourceActivities
            .FromSqlInterpolated($"SELECT * FROM [SourceActivities] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {activity.Id.Value}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (durableActivity is null || !MatchesImmutable(durableActivity, activity) || durableActivity.ResultingPipelineRecordId is not null ||
            !IsReplayEligible(durableActivity, replayCapability))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (replayCapability is not null)
        {
            var reasonCode = await VerifyReplayArtifactAsync(
                context,
                sourceRevision,
                artifact,
                cancellationToken).ConfigureAwait(false);
            if (reasonCode is not null)
            {
                durableActivity.State = (int)SourceActivityState.DeferredPolicy;
                durableActivity.Reason = reasonCode;
                durableActivity.UpdatedAtUtc = timeProvider.GetUtcNow();
                OperatorEventAppender.Add(context, new OperatorEventDraft(
                    "activity.retained_artifact_blocked",
                    "activity",
                    "warning",
                    "source-reconciliation",
                    timeProvider.GetUtcNow(),
                    SourceRootId: sourceRevision.SourceRootId,
                    SourceRevisionId: sourceRevision.Id,
                    SourceActivityId: durableActivity.Id,
                    CorrelationId: $"source:{sourceRevision.Id:N}",
                    Details: new { reasonCode }));
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var linkedRecords = await context.PipelineRecords
            .FromSqlInterpolated($"SELECT * FROM [PipelineRecords] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_PipelineRecords_SourceRevisionId])) WHERE [SourceRevisionId] = {sourceRevision.Id}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existingRecord = linkedRecords.SingleOrDefault()
            ?? await context.PipelineRecords.SingleOrDefaultAsync(record => record.SourceRevisionId == sourceRevision.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existingRecord is not null)
        {
            durableActivity.ResultingPipelineRecordId = existingRecord.Id;
            durableActivity.ResultingPipelineRecordRevision = existingRecord.Revision;
            durableActivity.UpdatedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var stableKey = CreateRetainedSourceKey(sourceRevision);
        var identity = await context.SourceIdentities
            .FromSqlInterpolated($"SELECT [Id], [SourceKind], [StableKey], [CreatedAtUtc] FROM [SourceIdentities] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceKind] = {RetainedSourceKind} AND [StableKey] = {stableKey}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (identity is null)
        {
            identity = new SourceIdentityEntity
            {
                Id = Guid.NewGuid(),
                SourceKind = RetainedSourceKind,
                StableKey = stableKey,
                CreatedAtUtc = now
            };
            context.SourceIdentities.Add(identity);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var latest = await context.PipelineRecords
            .Where(record => record.SourceIdentityId == identity.Id)
            .OrderByDescending(record => record.Revision)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var recordId = Guid.NewGuid();
        var revision = latest?.Revision + 1 ?? 1;
        var record = new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = identity.Id,
            SourceRevisionId = sourceRevision.Id,
            Revision = revision,
            ContentHash = sourceRevision.ContentSha256,
            RootLineageRecordId = latest?.RootLineageRecordId ?? recordId,
            ParentRevisionRecordId = latest?.Id,
            CurrentStage = (int)PipelineStage.Extract,
            RegisteredAtUtc = now
        };
        context.PipelineRecords.Add(record);
        durableActivity.ResultingPipelineRecordId = recordId;
        durableActivity.ResultingPipelineRecordRevision = revision;
        durableActivity.UpdatedAtUtc = now;
        if (deferredCapability is not null)
        {
            deferredCapability.ClaimedAtUtc = now;
            deferredCapability.ClaimedProcessorVersion = replayCapability!.ProcessorVersion;
        }
        var jobId = Guid.NewGuid();
        context.Jobs.Add(new JobEntity
        {
            Id = jobId,
            PipelineRecordId = recordId,
            SourceRevision = revision,
            Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8,
            PublicState = (int)FluxKnowledge.Domain.Jobs.PublicJobState.WorkerQueued,
            DueAtUtc = now
        });
        var dispatchId = Guid.NewGuid();
        context.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = dispatchId,
            PipelineRecordId = recordId,
            SourceRevision = revision,
            Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8,
            DispatchGeneration = 0,
            IdempotencyKey = $"{recordId:N}:{revision}:{(int)PipelineStage.Extract}:0",
            DueAtUtc = now,
            CreatedAtUtc = now
        });
        context.AuditEvents.Add(new AuditEventEntity
        {
            PipelineRecordId = recordId, EventType = "retained source pipeline record registered", Actor = "source-reconciliation",
            DetailsJson = JsonSerializer.Serialize(new { SourceRevisionId = sourceRevision.Id, sourceRevision.ContentSha256, ActivityId = durableActivity.Id }),
            OccurredAtUtc = now
        });
        OperatorEventAppender.Add(context, new OperatorEventDraft(
            "pipeline.registered", "pipeline", "information", "source-reconciliation", now,
            PipelineRecordId: recordId, SourceRootId: sourceRevision.SourceRootId, SourceRevisionId: sourceRevision.Id,
            SourceActivityId: durableActivity.Id, CorrelationId: $"source:{sourceRevision.Id:N}",
            Details: new { revision, sourceActivity = true }));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool IsSupported(SourceActivity activity) =>
        activity.ExecutionClass == ExecutionClass.InProcess &&
        activity.State == SourceActivityState.Pending &&
        activity.Kind is SourceActivityKind.TextExtraction or SourceActivityKind.MetadataExtraction;

    private static bool MatchesImmutable(SourceActivityEntity entity, SourceActivity activity) =>
        entity.SourceRevisionId == activity.SourceRevisionId.Value &&
        entity.ActivityKind == (int)activity.Kind &&
        entity.ExecutionClass == (int)activity.ExecutionClass &&
        entity.ProcessorVersion == activity.ProcessorVersion &&
        entity.InputFingerprint == activity.InputFingerprint;

    private static bool IsReplayEligible(SourceActivityEntity activity, RegisteredSourceCapability? capability) =>
        capability is null
            ? activity.State == (int)SourceActivityState.Pending && activity.ExecutionClass == (int)ExecutionClass.InProcess &&
                activity.ActivityKind is (int)SourceActivityKind.TextExtraction or (int)SourceActivityKind.MetadataExtraction
            : capability.IsRunnable && capability.ExecutionClass == ExecutionClass.InProcess &&
                activity.State == (int)SourceActivityState.DeferredUnsupported &&
                activity.ExecutionClass == (int)ExecutionClass.DeferredCapability &&
                activity.ActivityKind == (int)SourceActivityKind.TextExtraction &&
                string.Equals(activity.RequiredCapability, capability.ProcessorKind, StringComparison.Ordinal) &&
                string.Equals(activity.ProcessorVersion, capability.ProcessorVersion, StringComparison.Ordinal);

    private async ValueTask<IReadOnlyList<DeferredContentReplayRequest>> ReadReplayRequestsAsync(
        RegisteredSourceCapability capability,
        Guid? rootId,
        CancellationToken cancellationToken)
    {
        if (!capability.IsRunnable || capability.ExecutionClass != ExecutionClass.InProcess)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = from activity in context.SourceActivities.AsNoTracking()
            join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
            join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
            join deferred in context.DeferredCapabilities.AsNoTracking() on new
            {
                SourceRevisionId = activity.SourceRevisionId,
                ArtifactFingerprint = activity.InputFingerprint,
                RequiredCapability = activity.RequiredCapability!
            } equals new
            {
                deferred.SourceRevisionId,
                deferred.ArtifactFingerprint,
                deferred.RequiredCapability
            }
            where activity.State == (int)SourceActivityState.DeferredUnsupported &&
                activity.ExecutionClass == (int)ExecutionClass.DeferredCapability &&
                revision.Classification == AcceptedUtf8Classification &&
                activity.RequiredCapability == capability.ProcessorKind &&
                activity.ProcessorVersion == capability.ProcessorVersion &&
                activity.ResultingPipelineRecordId == null &&
                revision.SuppressedAtUtc == null &&
                EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                EF.Functions.Collate(artifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation)
            select new { activity, revision.SourceRootId };
        if (rootId is not null)
        {
            query = query.Where(value => value.SourceRootId == rootId.Value);
        }

        var rows = await query.OrderBy(value => value.activity.SourceRevisionId)
            .ThenBy(value => value.activity.ActivityKind)
            .ThenBy(value => value.activity.ProcessorVersion)
            .ThenBy(value => value.activity.InputFingerprint)
            .Select(value => value.activity)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(activity => new DeferredContentReplayRequest(
            activity.Id,
            SourceActivity.Restore(new SourceActivityId(activity.Id), new SourceRevisionId(activity.SourceRevisionId),
                (SourceActivityKind)activity.ActivityKind, (ExecutionClass)activity.ExecutionClass, activity.ProcessorVersion,
                activity.InputFingerprint, activity.RequiredCapability, (SourceActivityState)activity.State, activity.Reason).IdempotencyKey,
            activity.RequiredCapability!, capability.Id, capability.ProcessorVersion, capability.ProcessorFingerprint)).ToArray();
    }

    private static bool Matches(DeferredContentReplayRequest request, RegisteredSourceCapability capability) =>
        request.CapabilityId == capability.Id && capability.IsRunnable && capability.ExecutionClass == ExecutionClass.InProcess &&
        string.Equals(request.RequiredCapability, capability.ProcessorKind, StringComparison.Ordinal) &&
        string.Equals(request.ProcessorVersion, capability.ProcessorVersion, StringComparison.Ordinal) &&
        string.Equals(request.ProcessorFingerprint, capability.ProcessorFingerprint, StringComparison.Ordinal);

    private static string CreateRetainedSourceKey(SourceRevisionEntity revision) =>
        $"retained:{revision.SourceRootId:N}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(revision.StableSourceIdentity)))}";

    private async Task<string?> VerifyReplayArtifactAsync(
        FluxKnowledgeDbContext context,
        SourceRevisionEntity sourceRevision,
        SourceArtifactEntity artifact,
        CancellationToken cancellationToken)
    {
        var outlookSpoolRoot = await context.OutlookCaptureProfiles.AsNoTracking()
            .Where(profile => profile.SourceRootId == sourceRevision.SourceRootId)
            .Select(profile => profile.SpoolRoot)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        string? artifactRoot;
        try
        {
            artifactRoot = outlookSpoolRoot is null
                ? _retainedArtifactRoot
                : outlookSpoolPolicy?.RequireCanonicalBeforeIo(outlookSpoolRoot)
                    ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
        }
        catch (InvalidDataException)
        {
            return "retained-artifact-root-unavailable";
        }
        if (artifactRoot is null)
        {
            return "retained-artifact-root-unavailable";
        }

        try
        {
            var verified = await ContainedFileReader.ReadAsync(
                artifactRoot,
                artifact.StoreRelativePath,
                16L * 1024 * 1024,
                cancellationToken,
                sourceRevision.ContentSha256,
                sourceRevision.ByteLength).ConfigureAwait(false);
            _ = new UTF8Encoding(false, true).GetString(verified.Bytes);
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return "retained-artifact-missing";
        }
        catch (UnauthorizedAccessException)
        {
            return "retained-artifact-path-invalid";
        }
        catch (IOException)
        {
            return "retained-artifact-path-invalid";
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
        {
            return "retained-artifact-checksum-invalid";
        }
    }
}

/// <summary>Reads only the source artifact bound to an immutable revision and verifies it before decoding.</summary>
public sealed class SqlRetainedSourceReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    string artifactRoot,
    PersistedOutlookSpoolRootPolicy? outlookSpoolPolicy = null) : IRetainedSourceReader, IDisposable
{
    private readonly string _artifactRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactRoot));
    private readonly PhysicalDirectoryLease _rootLease = PhysicalFileIdentity.OpenDirectoryLease(artifactRoot);
    private const long MaximumAcceptedBinaryBytes = 128L * 1024 * 1024;
    private const long MaximumAcceptedUtf8TextBytes = 16L * 1024 * 1024;

    public async ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
    {
        var verified = await ReadVerifiedAsync(sourceRevisionId, cancellationToken).ConfigureAwait(false);
        if (verified.ByteLength > MaximumAcceptedBinaryBytes)
        {
            throw new InvalidDataException("The retained artifact exceeds the accepted binary limit.");
        }
        return new RetainedSourceBytes(verified.SourceRevisionId, verified.Bytes, verified.ContentSha256, verified.ByteLength);
    }

    public async ValueTask<RetainedArtifactInspection> InspectAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = await (
            from revision in context.SourceRevisions.AsNoTracking()
            join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
            where revision.Id == sourceRevisionId.Value
            select new { revision.SourceRootId, revision.ContentSha256, artifact.StoreRelativePath, artifact.ByteLength, ArtifactContentSha256 = artifact.ContentSha256 })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source is null) throw new FileNotFoundException("The retained source revision does not have an artifact.");
        if (!string.Equals(source.ContentSha256, source.ArtifactContentSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The retained artifact checksum does not match its source revision.");
        var outlookSpoolRoot = await context.OutlookCaptureProfiles.AsNoTracking().Where(profile => profile.SourceRootId == source.SourceRootId)
            .Select(profile => profile.SpoolRoot).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var selectedArtifactRoot = _artifactRoot;
        PhysicalDirectoryLease? privateRootLease = null;
        if (outlookSpoolRoot is not null)
        {
            selectedArtifactRoot = outlookSpoolPolicy?.RequireCanonicalBeforeIo(outlookSpoolRoot)
                ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(selectedArtifactRoot);
            privateRootLease = PhysicalFileIdentity.OpenDirectoryLease(selectedArtifactRoot);
        }
        using (privateRootLease)
        {
            var rootLease = privateRootLease ?? _rootLease;
            var artifactPath = ResolveArtifactPath(source.StoreRelativePath, source.ContentSha256, selectedArtifactRoot, rootLease);
            if (source.ByteLength < 0) throw new InvalidDataException("The retained artifact has an invalid byte length.");
            using var sha256Lease = OpenContainedLease(Path.Combine(selectedArtifactRoot, "sha256"), selectedArtifactRoot, rootLease);
            using var shardLease = OpenContainedLease(Path.GetDirectoryName(artifactPath)!, selectedArtifactRoot, rootLease);
            using var artifactHandle = PhysicalFileIdentity.OpenReadNoFollow(artifactPath);
            var finalArtifactPath = PhysicalFileIdentity.GetFinalPath(artifactHandle);
            var finalShardPath = Path.GetDirectoryName(finalArtifactPath) ?? throw new InvalidDataException("The retained artifact final path has no shard directory.");
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(finalShardPath);
            var finalShard = PhysicalFileIdentity.GetDirectory(finalShardPath);
            if (!IsWithin(shardLease.Identity.CanonicalPath, finalArtifactPath) || !string.Equals(finalShard.IdentityFingerprint, shardLease.Identity.IdentityFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("The retained artifact final file does not belong to its leased shard.");
            await using var stream = new FileStream(artifactHandle, FileAccess.Read, bufferSize: 81920, isAsync: true);
            if (stream.Length != source.ByteLength) throw new InvalidDataException("The retained artifact has an unexpected byte length.");
            return new RetainedArtifactInspection(sourceRevisionId, source.ContentSha256, source.ByteLength);
        }
    }

    public async ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
    {
        var verified = await ReadVerifiedAsync(sourceRevisionId, cancellationToken).ConfigureAwait(false);
        if (verified.ByteLength > MaximumAcceptedUtf8TextBytes)
        {
            throw new InvalidDataException("The retained artifact exceeds the accepted UTF-8 text limit.");
        }
        try
        {
            var payload = verified.Bytes.AsSpan();
            if (payload.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                payload = payload[3..];
            }

            return new Utf8FileSource(verified.CanonicalPath, verified.Bytes,
                new UTF8Encoding(false, true).GetString(payload), verified.ContentSha256);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The retained artifact is not valid UTF-8.", exception);
        }
    }

    private async ValueTask<VerifiedRetainedSource> ReadVerifiedAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = await (
            from revision in context.SourceRevisions.AsNoTracking()
            join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
            where revision.Id == sourceRevisionId.Value
            select new
            {
                revision.SourceRootId,
                revision.CanonicalPath,
                revision.ContentSha256,
                artifact.StoreRelativePath,
                artifact.ByteLength,
                ArtifactContentSha256 = artifact.ContentSha256
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            throw new FileNotFoundException("The retained source revision does not have an artifact.");
        }

        if (!string.Equals(source.ContentSha256, source.ArtifactContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The retained artifact checksum does not match its source revision.");
        }

        var outlookSpoolRoot = await context.OutlookCaptureProfiles
            .AsNoTracking()
            .Where(profile => profile.SourceRootId == source.SourceRootId)
            .Select(profile => profile.SpoolRoot)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedArtifactRoot = _artifactRoot;
        PhysicalDirectoryLease? privateRootLease = null;
        if (outlookSpoolRoot is not null)
        {
            selectedArtifactRoot = outlookSpoolPolicy?.RequireCanonicalBeforeIo(outlookSpoolRoot)
                ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(selectedArtifactRoot);
            privateRootLease = PhysicalFileIdentity.OpenDirectoryLease(selectedArtifactRoot);
        }

        using (privateRootLease)
        {
            var selectedRootLease = privateRootLease ?? _rootLease;
            var artifactPath = ResolveArtifactPath(
                source.StoreRelativePath,
                source.ContentSha256,
                selectedArtifactRoot,
                selectedRootLease);
            if (source.ByteLength < 0 || source.ByteLength > MaximumAcceptedBinaryBytes)
            {
                throw new InvalidDataException("The retained artifact exceeds the accepted retained-byte limit.");
            }
            var bytes = new byte[checked((int)source.ByteLength)];
            using var sha256Lease = OpenContainedLease(
                Path.Combine(selectedArtifactRoot, "sha256"),
                selectedArtifactRoot,
                selectedRootLease);
            using var shardLease = OpenContainedLease(
                Path.GetDirectoryName(artifactPath)!,
                selectedArtifactRoot,
                selectedRootLease);
            using var artifactHandle = PhysicalFileIdentity.OpenReadNoFollow(artifactPath);
            var finalArtifactPath = PhysicalFileIdentity.GetFinalPath(artifactHandle);
            var finalShardPath = Path.GetDirectoryName(finalArtifactPath)
                ?? throw new InvalidDataException("The retained artifact final path has no shard directory.");
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(finalShardPath);
            var finalShard = PhysicalFileIdentity.GetDirectory(finalShardPath);
            if (!IsWithin(shardLease.Identity.CanonicalPath, finalArtifactPath) ||
                !string.Equals(finalShard.IdentityFingerprint, shardLease.Identity.IdentityFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The retained artifact final file does not belong to its leased shard.");
            }
            await using var stream = new FileStream(artifactHandle, FileAccess.Read, bufferSize: 81920, isAsync: true);
            if (stream.Length != source.ByteLength)
            {
                throw new InvalidDataException("The retained artifact has an unexpected byte length.");
            }
            var offset = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("The retained artifact ended before its recorded length.");
                hash.AppendData(bytes, offset, read);
                offset += read;
            }
            if (stream.ReadByte() != -1 || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), source.ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The retained artifact checksum is invalid.");
            }

            return new VerifiedRetainedSource(sourceRevisionId, source.CanonicalPath, bytes, source.ContentSha256, source.ByteLength);
        }
    }

    private static string ResolveArtifactPath(
        string storedRelativePath,
        string contentSha256,
        string artifactRoot,
        PhysicalDirectoryLease rootLease)
    {
        EnsureRootCurrent(artifactRoot, rootLease);
        var expectedRelativePath = Path.Combine("sha256", contentSha256[..2], $"{contentSha256}.bin");
        if (string.IsNullOrWhiteSpace(storedRelativePath) || Path.IsPathRooted(storedRelativePath) ||
            !string.Equals(storedRelativePath, expectedRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The retained artifact path is invalid.");
        }

        return Path.Combine(artifactRoot, expectedRelativePath);
    }

    private static PhysicalDirectoryLease OpenContainedLease(
        string path,
        string artifactRoot,
        PhysicalDirectoryLease rootLease)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(path);
        var lease = PhysicalFileIdentity.OpenDirectoryLease(path);
        if (!IsWithin(rootLease.Identity.CanonicalPath, lease.Identity.CanonicalPath))
        {
            lease.Dispose();
            throw new InvalidDataException("The retained artifact path escapes the configured store.");
        }
        EnsureRootCurrent(artifactRoot, rootLease);
        return lease;
    }

    private static void EnsureRootCurrent(string artifactRoot, PhysicalDirectoryLease rootLease)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(artifactRoot);
        var current = PhysicalFileIdentity.GetDirectory(artifactRoot);
        if (!string.Equals(current.CanonicalPath, rootLease.Identity.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.IdentityFingerprint, rootLease.Identity.IdentityFingerprint, StringComparison.Ordinal))
        {
            throw new IOException("The retained artifact root changed after reader registration.");
        }
    }

    private static bool IsWithin(string root, string path) => string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _rootLease.Dispose();

    private sealed record VerifiedRetainedSource(
        SourceRevisionId SourceRevisionId,
        string CanonicalPath,
        byte[] Bytes,
        string ContentSha256,
        long ByteLength);
}
