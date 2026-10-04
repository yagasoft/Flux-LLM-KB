using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceScanStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider, EmbeddingGpuRuntime? embeddingRuntime = null) : ISourceScanStore, ISourceScanControlStore
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

        await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
        // All source reconciliation transactions take these locks in this order: publication fence, root, stable identity,
        // canonical path/content hash, then artifact-by-revision. This prevents inverse lock deadlocks.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT [Id] FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceRoot.Id.Value};",
            cancellationToken).ConfigureAwait(false);
        string? admittedGitRepositoryIdentity = null;
        SourceRootConfigurationEntity? currentGitRoot = null;
        if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked)
        {
            var currentRoot = await context.SourceRootConfigurations.SingleAsync(value => value.Id == sourceRoot.Id.Value, cancellationToken).ConfigureAwait(false);
            currentGitRoot = currentRoot;
            admittedGitRepositoryIdentity = ParseGitAdmissionIdentity(currentRoot.HealthEvidenceJson);
            if (currentRoot.State != (int)SourceRootState.Enabled || currentRoot.CrawlMode != (int)SourceDiscoveryMode.GitTracked ||
                currentRoot.ConfigurationRevision != sourceRoot.ConfigurationRevision || file.GitInventory is null ||
                file.GitInventory.RepositoryIdentity != admittedGitRepositoryIdentity || file.ScanOwnership is null ||
                !await OwnsRootScanAsync(context, sourceRoot.Id.Value, file.ScanOwnership, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Git source configuration or inventory admission is stale.");
        }
        var revisions = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceRevisions_SourceRootId_StableSourceIdentity_Revision])) WHERE [SourceRootId] = {sourceRoot.Id.Value} AND [StableSourceIdentity] = {file.StableSourceIdentity}")
            .OrderByDescending(value => value.Revision)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pathAndHash = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK, INDEX([IX_SourceRevisions_SourceRootId_CanonicalPathFingerprint_ContentSha256])) WHERE [SourceRootId] = {sourceRoot.Id.Value} AND [CanonicalPathFingerprint] = CONVERT(char(64), HASHBYTES('SHA2_256', {file.CanonicalPath}), 2) AND [ContentSha256] = {file.ContentSha256}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var exactOwners = pathAndHash.Where(value => string.Equals(value.CanonicalPath, file.CanonicalPath, StringComparison.Ordinal)).ToList();
        SourceRevisionEntity? revision = null;
        if (exactOwners.Any(value => !string.Equals(value.StableSourceIdentity, file.StableSourceIdentity, StringComparison.Ordinal)))
        {
            // Git checkout/atomic saves can replace the physical file while retaining identical tracked bytes.
            // Reuse only a proven Git owner; its immutable identity, artifact and existing work remain unchanged.
            if (sourceRoot.DiscoveryMode != SourceDiscoveryMode.GitTracked || exactOwners.Count != 1 ||
                !IsRetainedGitOwner(exactOwners[0], file.ByteLength, admittedGitRepositoryIdentity))
                throw new InvalidOperationException("A canonical source path and content hash are already owned by another stable source identity.");
            revision = exactOwners[0];
        }

        revision ??= revisions.FirstOrDefault(value =>
            string.Equals(value.ContentSha256, file.ContentSha256, StringComparison.Ordinal) &&
            string.Equals(value.CanonicalPath, file.CanonicalPath, StringComparison.Ordinal));
        var eventType = "source.unchanged";
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
                DiscoveryEvidenceJson = JsonSerializer.Serialize(new { relativePath = file.RelativePath, stableIdentity = file.StableSourceIdentity, gitInventory = file.GitInventory })
            };
            context.SourceRevisions.Add(revision);
            eventType = revisions.Count == 0 ? "source.added" : "source.updated";
        }
        else if (revision.SuppressedAtUtc is not null && currentGitRoot is null)
        {
            await SqlPublishedPassageSelection.AdvanceVersionAsync(context, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            revision.SuppressedAtUtc = null;
            revision.RetentionEvidenceJson = null;
            eventType = "source.updated";
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
        else if (artifact is null && MarkRetentionBlocked(revision, blockedReason ?? "artifact-retention-failed"))
        {
            OperatorEventAppender.Add(context, new OperatorEventDraft(
                "source.retention_blocked", "source", "warning", "source-reconciliation", timeProvider.GetUtcNow(),
                SourceRootId: sourceRoot.Id.Value, SourceRevisionId: revision.Id, CorrelationId: $"source:{revision.Id:N}",
                Details: new { reasonCode = "artifact-retention-failed" }));
        }

        if (currentGitRoot is not null && receipt is not null)
            revision.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Observe(currentGitRoot, revision,
                artifact?.Id ?? receipt.SourceArtifactId.Value, file, timeProvider.GetUtcNow());

        if (eventType != "source.unchanged")
        {
            var correlationId = $"source:{revision.Id:N}";
            OperatorEventAppender.Add(context, eventType == "source.added"
                ? OperatorEventDraft.SourceAdded(sourceRoot.Id.Value, null, revision.Id, correlationId, new { revision = revision.Revision, classification = revision.Classification })
                : OperatorEventDraft.SourceUpdated(sourceRoot.Id.Value, null, revision.Id, correlationId, new { revision = revision.Revision, classification = revision.Classification }));
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SourceRetentionConvergence(new SourceRevisionId(revision.Id), receipt is null && artifact is null);
    }

    private static bool ReceiptMatchesFile(SourceArtifactReceipt receipt, SourceDiscoveredFile file) =>
        receipt.ByteLength == file.ByteLength &&
        string.Equals(receipt.ContentSha256, file.ContentSha256, StringComparison.Ordinal);

    private static bool IsRetainedGitOwner(SourceRevisionEntity revision, long byteLength, string? repositoryIdentity)
    {
        if (revision.OriginKind != 0 || revision.ByteLength != byteLength || repositoryIdentity is null) return false;
        try
        {
            using var evidence = JsonDocument.Parse(revision.DiscoveryEvidenceJson ?? "{}");
            if (!evidence.RootElement.TryGetProperty("gitInventory", out var inventory)) return false;
            var retained = inventory.Deserialize<GitInventoryEvidence>();
            return retained is not null && retained.RepositoryIdentity == repositoryIdentity &&
                retained.Generation is not null && IsValidFingerprint(retained.Generation);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private static bool MarkRetentionBlocked(SourceRevisionEntity revision, string reason)
    {
        var evidence = ParseRetentionEvidence(revision.RetentionEvidenceJson);
        if (string.Equals(evidence["artifactRetention"]?.GetValue<string>(), "failed", StringComparison.Ordinal))
        {
            return false;
        }

        evidence["artifactRetention"] = "failed";
        evidence["reasonCode"] = reason[..Math.Min(reason.Length, 128)];
        revision.RetentionEvidenceJson = evidence.ToJsonString();
        return true;
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

    public async ValueTask<bool> SuppressUnseenAuthoritativelyAsync(
        SourceRootConfiguration root, SourceScanRequest request, GitInventoryEvidence inventory,
        IReadOnlySet<SourceRevisionId> convergedRevisionIds, CancellationToken cancellationToken)
    {
        if (request.Lease is null || request.SourceRootId != root.Id || root.DiscoveryMode != SourceDiscoveryMode.GitTracked ||
            inventory.RepositoryIdentity != root.RepositoryIdentityFingerprint || !IsValidFingerprint(inventory.Generation)) return false;
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => SuppressUnseenOnceAsync(root.Id, convergedRevisionIds, cancellationToken,
            root, request, inventory)).ConfigureAwait(false);
    }

    private async Task<bool> SuppressUnseenOnceAsync(
        SourceRootId sourceRootId,
        IReadOnlySet<SourceRevisionId> convergedRevisionIds,
        CancellationToken cancellationToken, SourceRootConfiguration? expectedRoot = null,
        SourceScanRequest? request = null, GitInventoryEvidence? inventory = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
        // This follows the reconciliation publication-fence/root lock order so a suppression pass cannot
        // observe a partially converged rename or historic-path restoration.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT [Id] FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceRootId.Value};",
            cancellationToken).ConfigureAwait(false);
        var root = await context.SourceRootConfigurations.SingleAsync(value => value.Id == sourceRootId.Value, cancellationToken).ConfigureAwait(false);
        if (root.CrawlMode != (int)SourceDiscoveryMode.Filesystem)
        {
            if (root.CrawlMode != (int)SourceDiscoveryMode.GitTracked || expectedRoot is null || request?.Lease is not { } lease || inventory is null ||
                root.State != (int)SourceRootState.Enabled || root.ConfigurationRevision != expectedRoot.ConfigurationRevision ||
                inventory.RepositoryIdentity != ParseGitAdmissionIdentity(root.HealthEvidenceJson)) return false;
            if (!await OwnsRootScanAsync(context, root.Id, new(request.Id, lease), timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)) return false;
        }
        var active = await context.SourceRevisions
            .FromSqlInterpolated($"SELECT * FROM [SourceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRootId] = {sourceRootId.Value} AND [OriginKind] = 0")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var suppressionChanged = false;
        foreach (var revision in active)
        {
            if (convergedRevisionIds.Contains(new SourceRevisionId(revision.Id)))
            {
                if (inventory is not null && request is not null)
                {
                    var proof = SqlRepositoryWorkRecovery.ReadObservation(revision.CurrentDiscoveryEvidenceJson);
                    var artifact = await context.SourceArtifacts.SingleOrDefaultAsync(value => value.SourceRevisionId == revision.Id, cancellationToken);
                    if (proof is { Version: 1, Authoritative: false } && proof.SourceId == revision.Id &&
                        proof.ScanRequestId == request.Id.Value && proof.LeaseGeneration == request.Lease!.Generation &&
                        proof.ConfigurationRevision == root.ConfigurationRevision && proof.Policy == SqlRepositoryWorkRecovery.Policy(root) &&
                        proof.Repository == inventory.RepositoryIdentity && proof.Inventory == inventory.Generation &&
                        artifact is not null && artifact.Id == proof.ArtifactId && artifact.ContentSha256 == proof.Hash &&
                        artifact.ByteLength == proof.Bytes && proof.Hash == revision.ContentSha256 && proof.Bytes == revision.ByteLength &&
                        artifact.ChecksumVerifiedAtUtc != default && artifact.ReferenceCount > 0 &&
                        (revision.Classification != "AcceptedUtf8Text" || proof.EligibleText) &&
                        !(revision.SuppressedAtUtc != null && revision.RetainUntilUtc <= now) &&
                        await context.SourceScanRequests.AnyAsync(value => value.Id == request.Id.Value && value.ErrorFileCount == 0, cancellationToken))
                    {
                        revision.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Authorize(proof, SqlRepositoryWorkRecovery.RequiresRecovery(revision));
                        if (revision.SuppressedAtUtc is not null)
                        {
                            revision.SuppressedAtUtc = null;
                            revision.RetentionEvidenceJson = null;
                            suppressionChanged = true;
                            OperatorEventAppender.Add(context, OperatorEventDraft.SourceUpdated(root.Id, null, revision.Id,
                                $"source:{revision.Id:N}", new { revision = revision.Revision, reason = "authoritative-rediscovery" }));
                        }
                    }
                }
                continue;
            }

            revision.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Withdraw();
            if (revision.SuppressedAtUtc is not null) continue;

            revision.SuppressedAtUtc = now;
            suppressionChanged = true;
            revision.RetainUntilUtc = now.AddDays(30);
            revision.RetentionEvidenceJson = "{\"reason\":\"unseen-during-authoritative-scan\"}";
            OperatorEventAppender.Add(context, OperatorEventDraft.SourceRemoved(
                sourceRootId.Value,
                revision.Id,
                $"source:{revision.Id:N}",
                new { revision = revision.Revision, reason = "unseen-during-authoritative-scan" }));
        }

        if (suppressionChanged)
            await SqlPublishedPassageSelection.AdvanceVersionAsync(context, now, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (inventory is not null)
            await SqlRepositoryWorkRecovery.ReconcileAsync(context, root.Id, now, embeddingRuntime, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
                value.SourceScanRequest.SourceRoot.State == (int)SourceRootState.Enabled &&
                (value.SourceScanRequest.SourceRoot.CrawlMode != (int)SourceDiscoveryMode.GitTracked ||
                    !context.SourceScanJobs.Any(other => other.Id != value.Id &&
                        other.SourceScanRequest.SourceRootId == value.SourceScanRequest.SourceRootId &&
                        other.State == (int)SourceScanJobState.Running && other.LeaseExpiresAtUtc > nowUtc)) &&
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
                requiresPhysicalIdentityValidation: true, discoveryMode: (SourceDiscoveryMode)root.CrawlMode, repositoryIdentityFingerprint: ParseGitAdmissionIdentity(root.HealthEvidenceJson)),
            SourceScanRequest.Restore(
                new SourceScanRequestId(candidate.SourceScanRequest.Id), new SourceRootId(root.Id),
                candidate.SourceScanRequest.RequestedBy, candidate.SourceScanRequest.RequestedAtUtc,
                (SourceScanRequestState)candidate.SourceScanRequest.State, candidate.SourceScanRequest.ReleasedAtUtc ?? nowUtc,
                new SourceScanLease(candidate.Id, leaseOwner, candidate.LeaseGeneration)));
        }).ConfigureAwait(false);
    }

    public async ValueTask<bool> RenewLeaseAsync(ClaimedSourceScan claim, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await executionContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var root = await context.SourceRootConfigurations.FromSqlInterpolated(
                $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {claim.SourceRoot.Id.Value}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (root is null || root.State != (int)SourceRootState.Enabled || root.CrawlMode != (int)SourceDiscoveryMode.GitTracked ||
                root.ConfigurationRevision != claim.SourceRoot.ConfigurationRevision ||
                !await OwnsRootScanAsync(context, root.Id, new(claim.ScanRequest.Id,
                    new(claim.ControlJobId, claim.LeaseOwner, claim.LeaseGeneration)), now, cancellationToken).ConfigureAwait(false)) return false;
            var job = await context.SourceScanJobs.SingleAsync(value => value.Id == claim.ControlJobId, cancellationToken).ConfigureAwait(false);
            job.LeaseExpiresAtUtc = now.Add(leaseDuration); job.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(
        ClaimedSourceScan claim,
        SourceScanResult result,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var root = await context.SourceRootConfigurations.FromSqlInterpolated(
            $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {claim.SourceRoot.Id.Value}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (claim.SourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked &&
            (root.State != (int)SourceRootState.Enabled || root.CrawlMode != (int)SourceDiscoveryMode.GitTracked ||
             root.ConfigurationRevision != claim.SourceRoot.ConfigurationRevision ||
             !await OwnsRootScanAsync(context, root.Id, new(claim.ScanRequest.Id,
                 new(claim.ControlJobId, claim.LeaseOwner, claim.LeaseGeneration)), now, cancellationToken).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("The Git source scan completion no longer owns current root authority.");
        }
        var job = await context.SourceScanJobs.SingleAsync(value => value.Id == claim.ControlJobId, cancellationToken).ConfigureAwait(false);
        if (job.State != (int)SourceScanJobState.Running || job.LeaseGeneration != claim.LeaseGeneration ||
            job.SourceScanRequestId != claim.ScanRequest.Id.Value || !string.Equals(job.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source scan control lease is no longer owned by this worker.");
        }
        var request = await context.SourceScanRequests.SingleAsync(value => value.Id == claim.ScanRequest.Id.Value, cancellationToken).ConfigureAwait(false);
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
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

    private static async Task<bool> OwnsRootScanAsync(FluxKnowledgeDbContext context, Guid rootId,
        SourceScanOwnership ownership, DateTimeOffset now, CancellationToken token)
    {
        // Caller holds the root lock used by claim creation/reclaim: no second job can become authoritative here.
        var lease = ownership.Lease;
        var job = await context.SourceScanJobs.FromSqlInterpolated($"SELECT * FROM [SourceScanJobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {lease.JobId}")
            .SingleOrDefaultAsync(token).ConfigureAwait(false);
        return job is not null && job.SourceScanRequestId == ownership.RequestId.Value &&
            job.State == (int)SourceScanJobState.Running && job.LeaseOwner == lease.Owner && job.LeaseGeneration == lease.Generation &&
            job.LeaseExpiresAtUtc > now && await context.SourceScanRequests.AnyAsync(value => value.Id == job.SourceScanRequestId &&
                value.SourceRootId == rootId && value.State == (int)SourceScanRequestState.Running && value.IsReleased, token).ConfigureAwait(false) &&
            !await context.SourceScanJobs.AnyAsync(value => value.Id != job.Id && value.SourceScanRequest.SourceRootId == rootId &&
                value.State == (int)SourceScanJobState.Running && value.LeaseExpiresAtUtc > now, token).ConfigureAwait(false);
    }

    public static string? ParseGitAdmissionIdentity(string? healthEvidenceJson)
    {
        try
        {
            var evidence = JsonNode.Parse(healthEvidenceJson ?? "{}") as JsonObject;
            var fingerprint = evidence?["gitRepositoryIdentityFingerprint"]?.GetValue<string>();
            return fingerprint is not null && IsValidFingerprint(fingerprint) ? fingerprint : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return null; }
    }

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
