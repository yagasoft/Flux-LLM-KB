using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Only enrolled repository work can continue automatically after withdrawal.</summary>
internal static class SqlRepositoryWorkRecovery
{
    internal const string Waiting = "repository-source-deferred";
    internal const string Blocked = "repository-source-blocked";
    private sealed record Binding(int Version, Guid RootId, long ConfigurationRevision, string Policy,
        Guid SourceId, Guid ArtifactId, string Hash, long Bytes, Guid ActivityId, string Activity);
    internal sealed record Discovery(int Version, bool Authoritative, Guid SourceId, Guid ArtifactId,
        string Hash, long Bytes, long ConfigurationRevision, string Policy, string Repository,
        Guid ScanRequestId, long LeaseGeneration, string Inventory, DateTimeOffset ObservedAtUtc, bool EligibleText);
    private sealed record DiscoveryState(int Version, Discovery? Current, Discovery? Observed, bool RecoveryRequired = false);
    private sealed record WaitingDetails(string Reason, int CheckAfterSeconds);

    internal static string? Capture(SourceRootConfigurationEntity root, SourceRevisionEntity source,
        SourceArtifactEntity artifact, SourceActivityEntity activity)
        => root.CrawlMode != (int)SourceDiscoveryMode.GitTracked || source.OriginKind != 0 ||
            source.Classification != "AcceptedUtf8Text" || activity.ActivityKind != (int)SourceActivityKind.TextExtraction ||
            activity.ExecutionClass != (int)ExecutionClass.InProcess ? null :
            JsonSerializer.Serialize(new Binding(1, root.Id, root.ConfigurationRevision, Policy(root), source.Id,
                artifact.Id, source.ContentSha256, source.ByteLength, activity.Id, Activity(activity)));

    internal static string Policy(SourceRootConfigurationEntity root)
    {
        var identity = SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson);
        return Hash(new { root.CanonicalPath, Physical = identity, Repository = SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson),
            root.CrawlMode, root.Recursive, root.FollowLinks, root.IncludePatternsJson, root.ExcludePatternsJson,
            root.MaximumFileBytes, root.AllowedClassificationsJson });
    }

    internal static string Observe(SourceRootConfigurationEntity root, SourceRevisionEntity source, Guid artifactId,
        SourceDiscoveredFile file, DateTimeOffset now) => JsonSerializer.Serialize(new DiscoveryState(1,
            source.SuppressedAtUtc is null ? ReadDiscovery(source.CurrentDiscoveryEvidenceJson) : null, new Discovery(1, false, source.Id,
            artifactId, file.ContentSha256, file.ByteLength, root.ConfigurationRevision, Policy(root),
            file.GitInventory!.RepositoryIdentity, file.ScanOwnership!.RequestId.Value,
            file.ScanOwnership.Lease.Generation, file.GitInventory.Generation, now, file.Classification.IsAccepted &&
                ((JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson ?? "[]") ?? []).Length == 0 ||
                 (JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson ?? "[]") ?? []).Contains("text/plain", StringComparer.OrdinalIgnoreCase))),
            source.SuppressedAtUtc is not null || RequiresRecovery(source)));

    internal static Discovery? ReadDiscovery(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<DiscoveryState>(json) is { Version: 1 } state ? state.Current : null; }
        catch (JsonException) { return null; }
    }

    internal static Discovery? ReadObservation(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<DiscoveryState>(json) is { Version: 1 } state ? state.Observed : null; }
        catch (JsonException) { return null; }
    }

    internal static string Authorize(Discovery proof, bool recoveryRequired) => JsonSerializer.Serialize(new DiscoveryState(1, proof with { Authoritative = true }, null, recoveryRequired));
    internal static string Withdraw() => JsonSerializer.Serialize(new DiscoveryState(1, null, null, true));
    internal static bool RequiresRecovery(SourceRevisionEntity source)
    {
        try { return JsonSerializer.Deserialize<DiscoveryState>(source.CurrentDiscoveryEvidenceJson ?? "{}")?.RecoveryRequired == true; }
        catch (JsonException) { return true; }
    }

    internal static async Task MarkRecoveryRequiredAsync(FluxKnowledgeDbContext context, JobEntity job, CancellationToken ct)
    {
        var source = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
            where record.Id == job.PipelineRecordId && record.RepositoryRecoveryBindingJson != null select revision).SingleOrDefaultAsync(ct);
        if (source is not null)
            source.CurrentDiscoveryEvidenceJson = JsonSerializer.Serialize(new DiscoveryState(1,
                ReadDiscovery(source.CurrentDiscoveryEvidenceJson), ReadObservation(source.CurrentDiscoveryEvidenceJson), true));
    }

    internal static async Task<RepositorySourceDeferral?> ReadEligibilityAsync(FluxKnowledgeDbContext context,
        JobEntity job, DateTimeOffset now, CancellationToken ct)
    {
        var record = await context.PipelineRecords.SingleOrDefaultAsync(value => value.Id == job.PipelineRecordId, ct);
        if (record?.SourceRevisionId is null) return null;
        var source = await context.SourceRevisions.SingleOrDefaultAsync(value => value.Id == record.SourceRevisionId, ct);
        if (source is null) return new("repository-source-binding-missing", true);
        var root = await context.SourceRootConfigurations.SingleAsync(value => value.Id == source.SourceRootId, ct);
        if (root.CrawlMode != (int)SourceDiscoveryMode.GitTracked && record.RepositoryRecoveryBindingJson is null) return null;
        if (record.IsDeleted || record.Revision != job.SourceRevision || record.CurrentStage != job.Stage || record.CompletionCriteriaMet ||
            root.State == (int)SourceRootState.Deleting || source.SuppressedAtUtc != null && source.RetainUntilUtc <= now ||
            await context.CorpusRebuildSupersededJobs.AnyAsync(value => value.JobId == job.Id, ct))
            return new("repository-work-deleted-expired-or-superseded", true);
        if (await context.IndexState.AnyAsync(value => value.Id == 1 && value.CorpusRebuildOperationId != null, ct))
        {
            try
            {
                // Explicit maintenance has its own captured, immutable source authority.
                // Ordinary automatic recovery cannot use this paused-intake exception.
                if (await SqlCorpusRebuildStore.ValidateMaintenanceJobAsync(context, job.Id, ct)) return null;
            }
            catch (Application.Indexing.CorpusRebuildRefusalException exception)
            {
                return exception.Message == "corpus-rebuild-job-not-authorised"
                    ? new("repository-source-waiting-for-authorised-maintenance")
                    : new("repository-maintenance-binding-invalid", true);
            }
        }
        // Legacy work may finish ordinarily while available, but cannot be enrolled from today's policy.
        if (record.RepositoryRecoveryBindingJson is null)
            return source.SuppressedAtUtc is null && root.State == (int)SourceRootState.Enabled
                ? null : new("repository-original-binding-missing-explicit-recovery-required", true);
        if (SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson) is null ||
            SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson) is null)
            return new("repository-admission-identity-unavailable", true);
        Binding? binding;
        try { binding = JsonSerializer.Deserialize<Binding>(record.RepositoryRecoveryBindingJson); }
        catch (JsonException) { return new("repository-original-binding-invalid", true); }
        var artifact = await context.SourceArtifacts.SingleOrDefaultAsync(value => value.SourceRevisionId == source.Id, ct);
        var activity = binding is null ? null : await context.SourceActivities.SingleOrDefaultAsync(value => value.Id == binding.ActivityId, ct);
        if (binding is null || binding.Version != 1 || binding.RootId != root.Id || binding.SourceId != source.Id ||
            binding.Policy != Policy(root) || binding.Hash != record.ContentHash || binding.Hash != source.ContentSha256 ||
            binding.Bytes != source.ByteLength || artifact is null || artifact.Id != binding.ArtifactId ||
            artifact.ContentSha256 != binding.Hash || artifact.ByteLength != binding.Bytes || artifact.ChecksumVerifiedAtUtc == default ||
            artifact.ReferenceCount < 1 || activity is null || activity.SourceRevisionId != source.Id ||
            activity.ResultingPipelineRecordId != record.Id || activity.ResultingPipelineRecordRevision != record.Revision ||
            binding.Activity != Activity(activity) || activity.State is (int)SourceActivityState.CancelledSuperseded or (int)SourceActivityState.FailedTerminal)
            return new("repository-processing-or-admission-binding-changed", true);
        if (root.State != (int)SourceRootState.Enabled) return new("repository-source-paused");
        if (source.SuppressedAtUtc is not null) return new("repository-source-awaiting-identical-authoritative-discovery");
        var proof = ReadDiscovery(source.CurrentDiscoveryEvidenceJson);
        if (proof is null || !proof.Authoritative || proof.ConfigurationRevision != root.ConfigurationRevision)
            return new("repository-source-awaiting-current-authoritative-discovery");
        if (proof.Version != 1 || !proof.EligibleText || proof.SourceId != binding.SourceId || proof.ArtifactId != binding.ArtifactId ||
            proof.Hash != binding.Hash || proof.Bytes != binding.Bytes || proof.Policy != binding.Policy ||
            proof.Repository != SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson) ||
            proof.ScanRequestId == Guid.Empty || proof.LeaseGeneration < 1 || proof.Inventory is not { Length: 64 } ||
            !proof.Inventory.All(Uri.IsHexDigit))
            return new("repository-current-discovery-binding-invalid", true);
        if (await context.SourceRevisions.AnyAsync(value => value.SourceRootId == root.Id && value.Id != source.Id &&
                value.CanonicalPath == source.CanonicalPath && value.OriginKind == 0 && value.SuppressedAtUtc == null, ct))
            return new("repository-source-replaced", true);
        if (RequiresRecovery(source) && !MatchesCurrentProcessing(activity, source))
            return new("repository-current-processing-contract-incompatible", true);
        return null;
    }

    private static bool MatchesCurrentProcessing(SourceActivityEntity activity, SourceRevisionEntity source)
    {
        if (activity.ActivityKind != (int)SourceActivityKind.TextExtraction || activity.ExecutionClass != (int)ExecutionClass.InProcess ||
            activity.RequiredCapability != null) return false;
        if (SourceClassifier.IsSourceTextExtension(source.CanonicalPath))
            return RepositorySourceTextPolicy.MatchesActivity(activity.ProcessorVersion, activity.DescriptorFingerprint);
        var expected = SourceActivity.Create(new(source.Id), SourceActivityKind.TextExtraction, ExecutionClass.InProcess,
            SourceScanWorker.TextProcessorVersion, source.ContentSha256, null, null);
        return activity.ProcessorVersion == expected.ProcessorVersion && activity.DescriptorFingerprint == expected.DescriptorFingerprint;
    }

    internal static async Task ValidateAsync(FluxKnowledgeDbContext context, JobEntity job, DateTimeOffset now,
        CancellationToken ct)
    {
        var refusal = await ReadEligibilityAsync(context, job, now, ct);
        if (refusal is not null) throw new RepositorySourceDeferredException(refusal);
    }

    internal static async Task<RepositorySourceDeferral?> CheckCheckpointAsync(FluxKnowledgeDbContext context,
        JobEntity job, EmbeddingGpuRuntime? runtime, CancellationToken ct, EmbeddingProfile? expectedProfile = null)
    {
        var enrolled = await context.PipelineRecords.AnyAsync(value => value.Id == job.PipelineRecordId && value.RepositoryRecoveryBindingJson != null, ct);
        if (!enrolled || job.Stage < (int)PipelineStage.Embed) return null;
        var embedJob = job.Stage == (int)PipelineStage.Embed ? job : await (from publication in context.OutboxMessages
            join parent in context.OutboxMessages on new { publication.PipelineRecordId, publication.SourceRevision } equals new { parent.PipelineRecordId, parent.SourceRevision }
            join previous in context.Jobs on parent.JobId equals previous.Id
            where publication.JobId == job.Id && parent.DispatchGeneration == publication.DispatchGeneration - 1 &&
                parent.Stage == (int)PipelineStage.Embed && parent.Operation == PipelineOperations.Embed &&
                previous.Stage == (int)PipelineStage.Embed && previous.Operation == PipelineOperations.Embed && previous.PipelineRecordId == parent.PipelineRecordId &&
                previous.SourceRevision == parent.SourceRevision
            select previous).SingleOrDefaultAsync(ct);
        if (embedJob is null) return new("repository-embedding-job-missing", true);
        ArtifactEntity? completedEmbedding = null;
        IndexGenerationEntity? draft;
        if (job.Stage == (int)PipelineStage.Publish)
        {
            completedEmbedding = await (from parent in context.OutboxMessages
                join artifact in context.Artifacts on parent.CompletedArtifactId equals artifact.Id
                where parent.JobId == embedJob.Id && parent.PipelineRecordId == job.PipelineRecordId && parent.SourceRevision == job.SourceRevision &&
                    parent.Stage == (int)PipelineStage.Embed && parent.Operation == PipelineOperations.Embed && parent.DispatchedAtUtc != null &&
                    artifact.PipelineRecordId == job.PipelineRecordId && artifact.SourceRevision == job.SourceRevision &&
                    artifact.Stage == (int)PipelineStage.Embed && artifact.ContentType == EmbedDraftDefaults.ArtifactContentType
                select artifact).SingleOrDefaultAsync(ct);
            if (embedJob.PublicState != (int)PublicJobState.Completed || completedEmbedding is null ||
                !Guid.TryParseExact(completedEmbedding.SearchText, "D", out var generationId))
                return new("repository-completed-embedding-artifact-invalid", true);
            draft = await context.IndexGenerations.SingleOrDefaultAsync(value => value.Id == generationId, ct);
            if (draft?.EmbeddingJobId is { } owner && owner != embedJob.Id)
                return new("repository-embedding-generation-owner-changed", true);
        }
        else
            draft = await context.IndexGenerations.SingleOrDefaultAsync(value => value.EmbeddingJobId == embedJob.Id, ct);
        if (draft is null) return job.Stage == (int)PipelineStage.Embed ? null : new("repository-embedding-draft-missing", true);
        try
        {
            var epoch = (await context.IndexState.SingleAsync(value => value.Id == 1, ct)).CorpusEpoch;
            if (draft.CorpusEpoch != epoch || draft.RetiredAtUtc is not null || draft.IndexPath.Length != 0 ||
                expectedProfile is not null && (draft.ModelFingerprint != expectedProfile.ModelFingerprint || draft.Dimensions != expectedProfile.Dimensions) ||
                runtime is not null && (draft.ModelFingerprint != runtime.Profile.ModelFingerprint || draft.Dimensions != runtime.Profile.Dimensions))
                return new("repository-checkpoint-profile-or-epoch-changed", true);
            var checksum = await SqlEmbeddingCheckpointStore.ReadCheckpointChecksumAsync(context, draft, job.PipelineRecordId,
                job.SourceRevision, job.Stage == (int)PipelineStage.Publish, ct);
            if (completedEmbedding is not null && completedEmbedding.ContentHash != checksum)
                return new("repository-completed-embedding-checksum-invalid", true);
            var source = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
                where record.Id == job.PipelineRecordId select revision).SingleAsync(ct);
            if (RequiresRecovery(source))
            {
                var record = await context.PipelineRecords.SingleAsync(value => value.Id == job.PipelineRecordId, ct);
                var canonical = await context.Artifacts.SingleOrDefaultAsync(value => value.PipelineRecordId == record.Id &&
                    value.SourceRevision == record.Revision && value.Stage == (int)PipelineStage.CanonicalIndex, ct);
                if (canonical?.SearchText is null || CodeDisclosureIntegrity.Hash(canonical.SearchText) != canonical.ContentHash)
                    return new("repository-canonical-input-integrity-failed", true);
                try { _ = await SqlEmbeddingRetry.ReadInputBindingAsync(context, record, canonical, draft, ct); }
                catch (NativeOperationException) { return new("repository-canonical-input-integrity-failed", true); }
            }
            if (RequiresRecovery(source) && await context.EmbeddingGpuRequests.AnyAsync(value => value.ParentJobId == embedJob.Id, ct))
            {
                if (runtime is null) return new("repository-gpu-runtime-verification-required", true);
                _ = await SqlEmbeddingRetry.ReadSettledGpuBindingAsync(context, embedJob, draft, runtime, ct);
            }
            return null;
        }
        catch (NativeOperationException) { return new("repository-gpu-outcome-cleanup-or-binding-unconfirmed", true); }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("embedding-checkpoint-", StringComparison.Ordinal))
        { return new("repository-checkpoint-integrity-failed", true); }
    }

    internal static void SetWaiting(FluxKnowledgeDbContext context, JobEntity job, OutboxMessageEntity dispatch,
        RepositorySourceDeferral refusal, DateTimeOffset now, int delaySeconds = 60)
    {
        job.PublicState = (int)PublicJobState.WorkerQueued;
        job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
        job.LeaseGeneration = checked(job.LeaseGeneration + 1);
        dispatch.LeaseGeneration = checked(dispatch.LeaseGeneration + 1);
        job.Reason = refusal.Blocked ? Blocked : Waiting;
        job.ErrorDetails = JsonSerializer.Serialize(new WaitingDetails(refusal.Reason, delaySeconds));
        job.DueAtUtc = dispatch.DueAtUtc = now.AddSeconds(delaySeconds);
        OperatorEventAppender.Add(context, new OperatorEventDraft("pipeline.stage_deferred", "pipeline", refusal.Blocked ? "warning" : "information",
            "repository-recovery", now, PipelineRecordId: job.PipelineRecordId,
            Details: new { stage = job.Stage, reason = refusal.Reason, blocked = refusal.Blocked, dueAtUtc = job.DueAtUtc }));
    }

    internal static async Task UpdateActivityAsync(FluxKnowledgeDbContext context, JobEntity job,
        RepositorySourceDeferral? refusal, DateTimeOffset now, CancellationToken ct)
    {
        await context.SourceActivities.Where(value => value.ResultingPipelineRecordId == job.PipelineRecordId &&
            value.ResultingPipelineRecordRevision == job.SourceRevision && value.State != (int)SourceActivityState.Completed &&
            value.State != (int)SourceActivityState.FailedTerminal && value.State != (int)SourceActivityState.CancelledSuperseded)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State,
                refusal != null && refusal.Blocked ? (int)SourceActivityState.DeferredPolicy : (int)SourceActivityState.FailedRetryable)
                .SetProperty(value => value.Reason, refusal == null ? "repository-source-resumed" : refusal.Reason)
                .SetProperty(value => value.UpdatedAtUtc, now), ct);
    }

    internal static async Task ReconcileAsync(FluxKnowledgeDbContext context, Guid? rootId, DateTimeOffset now,
        EmbeddingGpuRuntime? runtime, CancellationToken ct)
    {
        Guid last = Guid.Empty;
        while (true)
        {
            var ids = await context.Jobs.FromSqlInterpolated($"""
                SELECT TOP (64) j.* FROM Jobs j JOIN PipelineRecords p ON p.Id=j.PipelineRecordId
                JOIN SourceRevisions s ON s.Id=p.SourceRevisionId
                WHERE j.Id>{last} AND j.PublicState={(int)PublicJobState.WorkerQueued}
                  AND j.Reason={Waiting} AND ({rootId} IS NULL OR s.SourceRootId={rootId})
                  AND ({rootId} IS NOT NULL OR j.DueAtUtc<={now}) ORDER BY j.Id
                """).AsNoTracking().Select(value => value.Id).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            foreach (var id in ids)
            {
                var deliveries = await context.OutboxMessages.FromSqlInterpolated($"SELECT * FROM OutboxMessages WITH (UPDLOCK,HOLDLOCK) WHERE JobId={id}").ToArrayAsync(ct);
                var job = await context.Jobs.FromSqlInterpolated($"SELECT * FROM Jobs WITH (UPDLOCK,HOLDLOCK) WHERE Id={id}").SingleAsync(ct);
                if (job.Reason != Waiting || job.PublicState != (int)PublicJobState.WorkerQueued || job.LeaseOwner != null || job.LeaseExpiresAtUtc != null) continue;
                var dispatch = deliveries.SingleOrDefault();
                if (dispatch is null || dispatch.DispatchedAtUtc != null || dispatch.LeaseOwner != null || dispatch.LeaseExpiresAtUtc != null || dispatch.SourceRevision != job.SourceRevision ||
                    dispatch.PipelineRecordId != job.PipelineRecordId || dispatch.Stage != job.Stage || dispatch.Operation != job.Operation)
                    continue; // Never steal a delivery or fabricate a missing owner.
                var refusal = await ReadEligibilityAsync(context, job, now, ct) ?? await CheckCheckpointAsync(context, job, runtime, ct);
                if (refusal is null)
                {
                    job.Reason = "repository-source-resumed"; job.ErrorDetails = null;
                    job.DueAtUtc = dispatch.DueAtUtc = now;
                }
                else
                {
                    WaitingDetails? details = null;
                    try { details = JsonSerializer.Deserialize<WaitingDetails>(job.ErrorDetails ?? "{}"); } catch (JsonException) { }
                    var delay = Math.Min(900, Math.Max(60, details?.CheckAfterSeconds ?? 60) * 2);
                    job.Reason = refusal.Blocked ? Blocked : Waiting;
                    job.ErrorDetails = JsonSerializer.Serialize(new WaitingDetails(refusal.Reason, delay));
                    job.DueAtUtc = dispatch.DueAtUtc = now.AddSeconds(delay);
                }
                await UpdateActivityAsync(context, job, refusal, now, ct);
            }
            await context.SaveChangesAsync(ct);
            last = ids[^1];
        }
    }

    internal static async Task ReconcileDueAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        DateTimeOffset now, EmbeddingGpuRuntime? runtime, CancellationToken ct)
    {
        await using var probe = await factory.CreateDbContextAsync(ct);
        if (!await probe.Jobs.AnyAsync(value => value.PublicState == (int)PublicJobState.WorkerQueued && value.Reason == Waiting && value.DueAtUtc <= now, ct)) return;
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, ct);
            await ReconcileAsync(context, null, now, runtime, ct);
            await transaction.CommitAsync(ct);
        });
    }

    private static string Activity(SourceActivityEntity value) => Hash(new { value.SourceRevisionId, value.ActivityKind,
        value.ExecutionClass, value.ProcessorVersion, value.InputFingerprint, value.DescriptorFingerprint, value.RequiredCapability });
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
