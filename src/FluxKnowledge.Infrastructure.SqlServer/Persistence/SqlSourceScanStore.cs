using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceScanStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : ISourceScanStore, ISourceScanControlStore
{
    private const string SourceArtifactStoreCapability = "source-artifact-store";
    private const string SourceProcessorVersion = "phase-3a-v1";
    public ValueTask<SourceRevisionId> ConvergeRevisionAndArtifactAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        SourceArtifactReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ReceiptMatchesFile(receipt, file))
        {
            throw new InvalidOperationException("The retained artifact receipt does not match the discovered source bytes.");
        }

        return new ValueTask<SourceRevisionId>(ConvergeRevisionAndArtifactCoreAsync(sourceRoot, file, receipt, cancellationToken));
    }

    public ValueTask<SourceRetentionConvergence> ConvergeBlockedRevisionAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ValueTask<SourceRetentionConvergence>(ExecuteConvergenceAsync(sourceRoot, file, null, reason, cancellationToken));
    }

    private async Task<SourceRevisionId> ConvergeRevisionAndArtifactCoreAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        SourceArtifactReceipt receipt,
        CancellationToken cancellationToken) =>
        (await ExecuteConvergenceAsync(sourceRoot, file, receipt, null, cancellationToken).ConfigureAwait(false)).SourceRevisionId;

    private async Task<SourceRetentionConvergence> ExecuteConvergenceAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        SourceArtifactReceipt? receipt,
        string? blockedReason,
        CancellationToken cancellationToken)
    {
        return await SourceConvergenceRetryPolicy.ExecuteAsync(async (_, attemptCancellationToken) =>
        {
            await using var strategyContext = await contextFactory.CreateDbContextAsync(attemptCancellationToken).ConfigureAwait(false);
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(() => ConvergeOnceAsync(sourceRoot, file, receipt, blockedReason, attemptCancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRetentionConvergence> ConvergeOnceAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        SourceArtifactReceipt? receipt,
        string? blockedReason,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        // All source reconciliation transactions take these locks in this order: root, stable identity,
        // canonical path/content hash, then artifact-by-revision. This prevents inverse lock deadlocks.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT [Id] FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceRoot.Id.Value};",
            cancellationToken).ConfigureAwait(false);
        var revisions = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceRevisions_SourceRootId_StableSourceIdentity_Revision])) WHERE [SourceRootId] = {sourceRoot.Id.Value} AND [StableSourceIdentity] = {file.StableSourceIdentity}")
            .OrderByDescending(value => value.Revision)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pathAndHash = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceRevisions_SourceRootId_CanonicalPathFingerprint_ContentSha256])) WHERE [SourceRootId] = {sourceRoot.Id.Value} AND [CanonicalPathFingerprint] = CONVERT(char(64), HASHBYTES('SHA2_256', {file.CanonicalPath}), 2) AND [ContentSha256] = {file.ContentSha256}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pathAndHash.Any(value =>
                string.Equals(value.CanonicalPath, file.CanonicalPath, StringComparison.Ordinal) &&
                !string.Equals(value.StableSourceIdentity, file.StableSourceIdentity, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A canonical source path and content hash are already owned by another stable source identity.");
        }

        var revision = revisions.FirstOrDefault(value =>
            string.Equals(value.ContentSha256, file.ContentSha256, StringComparison.Ordinal) &&
            string.Equals(value.CanonicalPath, file.CanonicalPath, StringComparison.Ordinal));
        if (revision is null)
        {
            var latest = revisions.FirstOrDefault();
            revision = new SourceRevisionEntity
            {
                Id = Guid.NewGuid(), SourceRootId = sourceRoot.Id.Value, StableSourceIdentity = file.StableSourceIdentity,
                Revision = latest is null ? 1 : latest.Revision + 1, ContentSha256 = file.ContentSha256,
                CanonicalPath = file.CanonicalPath, ParentSourceRevisionId = latest?.Id,
                Classification = file.Classification.Classification.ToString(), Extension = Path.GetExtension(file.CanonicalPath),
                ByteLength = file.ByteLength, FileLastWriteAtUtc = file.LastWriteAtUtc, DiscoveredAtUtc = timeProvider.GetUtcNow(),
                DiscoveryEvidenceJson = JsonSerializer.Serialize(new { relativePath = file.RelativePath, stableIdentity = file.StableSourceIdentity })
            };
            context.SourceRevisions.Add(revision);
        }
        else if (revision.SuppressedAtUtc is not null)
        {
            revision.SuppressedAtUtc = null;
            revision.RetentionEvidenceJson = null;
        }

        var artifacts = await context.SourceArtifacts
            .FromSqlInterpolated($"SELECT * FROM [SourceArtifacts] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceArtifacts_SourceRevisionId])) WHERE [SourceRevisionId] = {revision.Id}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var artifact = artifacts.SingleOrDefault();
        if (receipt is not null)
        {
            if (artifact is not null &&
                (!string.Equals(artifact.ContentSha256, receipt.ContentSha256, StringComparison.Ordinal) ||
                 !string.Equals(artifact.StoreRelativePath, receipt.StoreRelativePath, StringComparison.Ordinal) ||
                 artifact.ByteLength != receipt.ByteLength))
            {
                throw new InvalidOperationException("A source revision already references different immutable artifact bytes.");
            }

            if (artifact is null)
            {
                context.SourceArtifacts.Add(new SourceArtifactEntity
                {
                    Id = receipt.SourceArtifactId.Value,
                    SourceRevisionId = revision.Id,
                    ContentSha256 = receipt.ContentSha256,
                    StoreRelativePath = receipt.StoreRelativePath,
                    ByteLength = receipt.ByteLength,
                    ChecksumVerifiedAtUtc = timeProvider.GetUtcNow(),
                    ReferenceCount = 1
                });
            }

            MarkRetentionRecovered(revision, timeProvider.GetUtcNow());
            await CancelSupersededArtifactRetentionActivitiesAsync(context, revision.Id, file.ContentSha256, cancellationToken).ConfigureAwait(false);
        }
        else if (artifact is null)
        {
            MarkRetentionBlocked(revision, blockedReason ?? "artifact-retention-failed");
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SourceRetentionConvergence(new SourceRevisionId(revision.Id), receipt is null && artifact is null);
    }

    private static bool ReceiptMatchesFile(SourceArtifactReceipt receipt, SourceDiscoveredFile file) =>
        receipt.ByteLength == file.ByteLength &&
        string.Equals(receipt.ContentSha256, file.ContentSha256, StringComparison.Ordinal);

    private static void MarkRetentionBlocked(SourceRevisionEntity revision, string reason)
    {
        var evidence = ParseRetentionEvidence(revision.RetentionEvidenceJson);
        if (string.Equals(evidence["artifactRetention"]?.GetValue<string>(), "failed", StringComparison.Ordinal))
        {
            return;
        }

        evidence["artifactRetention"] = "failed";
        evidence["reasonCode"] = reason[..Math.Min(reason.Length, 128)];
        revision.RetentionEvidenceJson = evidence.ToJsonString();
    }

    private static void MarkRetentionRecovered(SourceRevisionEntity revision, DateTimeOffset recoveredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(revision.RetentionEvidenceJson))
        {
            return;
        }

        var evidence = ParseRetentionEvidence(revision.RetentionEvidenceJson);
        if (!string.Equals(evidence["artifactRetention"]?.GetValue<string>(), "failed", StringComparison.Ordinal))
        {
            return;
        }

        var history = evidence["artifactRetentionHistory"] as JsonArray ?? [];
        if (history.Count == 0)
        {
            history.Add(new JsonObject
            {
                ["status"] = "failed",
                ["reasonCode"] = evidence["reasonCode"]?.DeepClone()
            });
        }
        evidence["artifactRetentionHistory"] = history;
        evidence["artifactRetention"] = "recovered";
        evidence["recoveredAtUtc"] = recoveredAtUtc;
        revision.RetentionEvidenceJson = evidence.ToJsonString();
    }

    private async Task CancelSupersededArtifactRetentionActivitiesAsync(
        FluxKnowledgeDbContext context,
        Guid sourceRevisionId,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        var activities = await context.SourceActivities
            .Where(activity => activity.SourceRevisionId == sourceRevisionId &&
                activity.ActivityKind == (int)SourceActivityKind.DocumentParsing &&
                activity.ProcessorVersion == SourceProcessorVersion &&
                activity.InputFingerprint == contentSha256 &&
                activity.RequiredCapability == SourceArtifactStoreCapability &&
                activity.State == (int)SourceActivityState.DeferredPolicy)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var activity in activities)
        {
            activity.State = (int)SourceActivityState.CancelledSuperseded;
            activity.UpdatedAtUtc = now;
        }
    }

    private static JsonObject ParseRetentionEvidence(string? evidenceJson)
    {
        try
        {
            return JsonNode.Parse(evidenceJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public async ValueTask SuppressUnseenAsync(
        SourceRootId sourceRootId,
        IReadOnlySet<SourceRevisionId> convergedRevisionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRootId);
        ArgumentNullException.ThrowIfNull(convergedRevisionIds);
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => SuppressUnseenOnceAsync(sourceRootId, convergedRevisionIds, cancellationToken)).ConfigureAwait(false);
    }

    private async Task SuppressUnseenOnceAsync(
        SourceRootId sourceRootId,
        IReadOnlySet<SourceRevisionId> convergedRevisionIds,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        // This follows the reconciliation root-first lock order so a suppression pass cannot
        // observe a partially converged rename or historic-path restoration.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT [Id] FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceRootId.Value};",
            cancellationToken).ConfigureAwait(false);
        var active = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRootId] = {sourceRootId.Value} AND [SuppressedAtUtc] IS NULL")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var revision in active)
        {
            if (convergedRevisionIds.Contains(new SourceRevisionId(revision.Id)))
            {
                continue;
            }

            revision.SuppressedAtUtc = now;
            revision.RetainUntilUtc = now.AddDays(30);
            revision.RetentionEvidenceJson = "{\"reason\":\"unseen-during-authoritative-scan\"}";
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecordEnumerationEvidenceAsync(
        SourceScanRequestId sourceScanRequestId,
        IReadOnlyList<SourceEnumerationEvidence> evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var request = await context.SourceScanRequests.Include(value => value.SourceRoot).SingleAsync(value => value.Id == sourceScanRequestId.Value, cancellationToken).ConfigureAwait(false);
        request.ErrorFileCount = evidence.Count;
        request.AuditEvidenceJson = MergeEvidence(request.AuditEvidenceJson, evidence);
        request.SourceRoot.HealthEvidenceJson = MergeEvidence(request.SourceRoot.HealthEvidenceJson, evidence);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ClaimedSourceScan?> ClaimNextReleasedAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await LockControlRangesAsync(context, cancellationToken).ConfigureAwait(false);
        await CreateDueRecurringRequestsAsync(context, nowUtc, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await context.SourceScanJobs
            .Include(value => value.SourceScanRequest).ThenInclude(value => value.SourceRoot)
            .Where(value => (value.State == (int)SourceScanJobState.Pending ||
                    (value.State == (int)SourceScanJobState.Running && value.LeaseExpiresAtUtc <= nowUtc)) &&
                value.DueAtUtc <= nowUtc &&
                value.SourceScanRequest.IsReleased &&
                (value.LeaseExpiresAtUtc == null || value.LeaseExpiresAtUtc <= nowUtc))
            .OrderBy(value => value.DueAtUtc).ThenBy(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var physicalIdentityFingerprint = ParseAdmissionIdentityFingerprint(candidate.SourceScanRequest.SourceRoot.HealthEvidenceJson);
        candidate.State = (int)SourceScanJobState.Running;
        candidate.SourceScanRequest.State = (int)SourceScanRequestState.Running;
        candidate.LeaseOwner = leaseOwner;
        candidate.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        candidate.LeaseGeneration++;
        candidate.AttemptCount++;
        candidate.UpdatedAtUtc = nowUtc;
        candidate.SourceScanRequest.SourceRoot.LastScanStartedAtUtc = nowUtc;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var root = candidate.SourceScanRequest.SourceRoot;
        return new ClaimedSourceScan(
            candidate.Id,
            leaseOwner,
            candidate.LeaseGeneration,
            SourceRootConfiguration.Restore(
                new SourceRootId(root.Id), root.CanonicalPath, root.DisplayName, root.Recursive, root.FollowLinks, root.MaximumFileBytes,
                DeserializeRules(root.IncludePatternsJson), DeserializeRules(root.ExcludePatternsJson),
                DeserializeRules(root.AllowedClassificationsJson), TimeSpan.FromSeconds(root.ReconciliationCadenceSeconds),
                (SourceRootState)root.State, root.ConfigurationRevision,
                physicalIdentityFingerprint: physicalIdentityFingerprint,
                requiresPhysicalIdentityValidation: true),
            SourceScanRequest.Restore(
                new SourceScanRequestId(candidate.SourceScanRequest.Id), new SourceRootId(root.Id),
                candidate.SourceScanRequest.RequestedBy, candidate.SourceScanRequest.RequestedAtUtc,
                (SourceScanRequestState)candidate.SourceScanRequest.State, candidate.SourceScanRequest.ReleasedAtUtc ?? nowUtc));
        }).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(
        ClaimedSourceScan claim,
        SourceScanResult result,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await context.SourceScanJobs.SingleAsync(value => value.Id == claim.ControlJobId, cancellationToken).ConfigureAwait(false);
        if (job.State != (int)SourceScanJobState.Running || job.LeaseGeneration != claim.LeaseGeneration ||
            !string.Equals(job.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source scan control lease is no longer owned by this worker.");
        }
        var request = await context.SourceScanRequests.SingleAsync(value => value.Id == claim.ScanRequest.Id.Value, cancellationToken).ConfigureAwait(false);
        var root = await context.SourceRootConfigurations.SingleAsync(value => value.Id == claim.SourceRoot.Id.Value, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        job.State = failureReason is null ? (int)SourceScanJobState.Completed : (int)SourceScanJobState.Pending;
        job.Reason = failureReason;
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.UpdatedAtUtc = now;
        request.State = failureReason is null ? (int)SourceScanRequestState.Completed : (int)SourceScanRequestState.Released;
        request.DiscoveredFileCount = result.DiscoveredCount;
        request.IndexedFileCount = result.IndexedCount;
        request.DeferredFileCount = result.DeferredCount;
        request.BlockedFileCount = result.BlockedCount;
        root.LastScanCompletedAtUtc = now;
        root.LastScanEvidenceJson = JsonSerializer.Serialize(new { result.DiscoveredCount, result.IndexedCount, result.DeferredCount, result.BlockedCount, failureReason });
        root.UpdatedAtUtc = now;
        if (failureReason is not null)
        {
            job.DueAtUtc = now.AddMinutes(1);
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateDueRecurringRequestsAsync(
        FluxKnowledgeDbContext context,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var roots = await context.SourceRootConfigurations
            .Where(root => root.State == (int)SourceRootState.Enabled &&
                (root.LastScanCompletedAtUtc == null || root.LastScanCompletedAtUtc.Value.AddSeconds(root.ReconciliationCadenceSeconds) <= nowUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in roots)
        {
            var hasActive = await context.SourceScanRequests.AnyAsync(request => request.SourceRootId == root.Id &&
                (request.State == (int)SourceScanRequestState.Held ||
                    request.State == (int)SourceScanRequestState.Released ||
                    request.State == (int)SourceScanRequestState.Running),
                cancellationToken).ConfigureAwait(false);
            if (hasActive)
            {
                continue;
            }

            var requestId = Guid.NewGuid();
            context.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = requestId,
                SourceRootId = root.Id,
                RequestKind = 1,
                RequestedBy = "reconciliation",
                RequestedAtUtc = nowUtc,
                IsReleased = true,
                ReleasedAtUtc = nowUtc,
                State = (int)SourceScanRequestState.Released
            });
            context.SourceScanJobs.Add(new SourceScanJobEntity
            {
                Id = Guid.NewGuid(),
                SourceScanRequestId = requestId,
                State = (int)SourceScanJobState.Pending,
                DueAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
            context.SourceScanOutbox.Add(new SourceScanOutboxEntity
            {
                Id = Guid.NewGuid(),
                SourceScanRequestId = requestId,
                Operation = "source.scan",
                IdempotencyKey = $"source-scan:{requestId:N}",
                DueAtUtc = nowUtc,
                CreatedAtUtc = nowUtc
            });
        }
    }

    private static Task LockControlRangesAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(
            """
            SELECT [Id]
            FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK)
            WHERE [State] = 0
            ORDER BY [Id];

            SELECT [Id]
            FROM [SourceScanRequests] WITH (UPDLOCK, HOLDLOCK)
            ORDER BY [SourceRootId], [Id];

            SELECT [Id]
            FROM [SourceScanJobs] WITH (UPDLOCK, HOLDLOCK)
            ORDER BY [SourceScanRequestId], [Id];
            """,
            cancellationToken);

    private static IReadOnlyList<string> DeserializeRules(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    public static string? ParseAdmissionIdentityFingerprint(string? healthEvidenceJson)
    {
        try
        {
            var root = JsonNode.Parse(healthEvidenceJson ?? "{}") as JsonObject;
            var physicalIdentity = root?["physicalIdentity"] as JsonObject;
            var node = physicalIdentity?["IdentityFingerprint"] ?? physicalIdentity?["identityFingerprint"];
            if (node is not JsonValue value || !value.TryGetValue<string>(out var fingerprint) || !IsValidFingerprint(fingerprint))
            {
                return null;
            }

            return fingerprint;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool IsValidFingerprint(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string MergeEvidence(string? existing, IReadOnlyList<SourceEnumerationEvidence> evidence)
    {
        JsonObject value;
        try
        {
            value = JsonNode.Parse(existing ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            value = new JsonObject();
        }

        value["enumerationErrors"] = JsonSerializer.SerializeToNode(evidence.Take(100));
        value["enumerationComplete"] = evidence.Count == 0;
        return value.ToJsonString();
    }

}
