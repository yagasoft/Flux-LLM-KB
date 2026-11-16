using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Resolves a physical Git text revision from completed capture authority, never revision order.</summary>
internal static class SqlGitTextSelection
{
    internal sealed record Selection(SourceRevisionEntity Source, SourceRootConfigurationEntity Root,
        SourceRevisionEntity[] Predecessors);

    internal static async Task<Selection?> ReadAsync(FluxKnowledgeDbContext context, Guid sourceId,
        CancellationToken ct, bool allowSuppressed = false)
    {
        var source = await context.SourceRevisions.SingleOrDefaultAsync(x => x.Id == sourceId, ct);
        if (source is null || source.OriginKind != 0 || !allowSuppressed && source.SuppressedAtUtc is not null) return null;
        var root = await context.SourceRootConfigurations.SingleAsync(x => x.Id == source.SourceRootId, ct);
        if (root.State != (int)SourceRootState.Enabled || root.CrawlMode != (int)SourceDiscoveryMode.GitTracked) return null;
        var proof = SqlRepositoryWorkRecovery.ReadDiscovery(source.CurrentDiscoveryEvidenceJson);
        if (proof is not { Version: 1, Authoritative: true, EligibleText: true, LeaseGeneration: > 0 } ||
            SqlRepositoryWorkRecovery.ReadObservation(source.CurrentDiscoveryEvidenceJson) is not null ||
            proof.SourceId != source.Id || proof.Hash != source.ContentSha256 || proof.Bytes != source.ByteLength ||
            proof.ConfigurationRevision != root.ConfigurationRevision || proof.Policy != SqlRepositoryWorkRecovery.Policy(root) ||
            proof.Repository != SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson)) return null;
        var artifact = await context.SourceArtifacts.SingleOrDefaultAsync(x => x.SourceRevisionId == source.Id, ct);
        if (artifact is null || artifact.Id != proof.ArtifactId || artifact.ContentSha256 != proof.Hash ||
            artifact.ByteLength != proof.Bytes || artifact.ChecksumVerifiedAtUtc == default || artifact.ReferenceCount < 1) return null;
        var relative = Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath);
        if (!Path.IsPathFullyQualified(source.CanonicalPath) || relative == "." || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var key = SqlSourceChangeJournalStore.PathKey(SqlSourceChangeJournalStore.NormaliseRelative(relative));
        var state = await context.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id, ct);
        var entry = await context.SourceInventoryEntries.SingleOrDefaultAsync(x => x.SourceRootId == root.Id && x.PathFingerprint == key, ct);
        if (state is null || entry is null || entry.IsDirectory || entry.IsAncestor || entry.SourceRevisionId != source.Id ||
            entry.SourceArtifactId != artifact.Id || entry.BaselineEpoch != state.BaselineEpoch) return null;
        var scan = await context.SourceScanRequests.SingleOrDefaultAsync(x => x.Id == proof.ScanRequestId && x.SourceRootId == root.Id, ct);
        if (scan?.State != (int)SourceScanRequestState.Completed ||
            !await context.SourceScanJobs.AnyAsync(x => x.SourceScanRequestId == scan.Id &&
                x.State == (int)SourceScanJobState.Completed && x.LeaseGeneration == proof.LeaseGeneration, ct)) return null;
        SourceIncrementalPlan? plan;
        try { plan = JsonSerializer.Deserialize<SourceIncrementalPlan>(scan.IncrementalManifestJson ?? "{}"); }
        catch (JsonException) { return null; }
        if (plan is not { Version: 1, LeaseGeneration: > 0 } || plan.LeaseGeneration > proof.LeaseGeneration ||
            plan.BaselineEpoch != state.BaselineEpoch || plan.GitInventoryGeneration != proof.Inventory ||
            plan.DetectionPolicy != SourcePolicyDefaults.DetectionPolicy) return null;
        if (await HasOutstandingCaptureAsync(context, source, root, ct)) return null;
        var predecessors = await context.SourceRevisions.Where(x => x.SourceRootId == root.Id && x.OriginKind == 0 &&
            x.CanonicalPath == source.CanonicalPath && x.Id != source.Id && x.SuppressedAtUtc == null).ToArrayAsync(ct);
        return new(source, root, predecessors);
    }

    internal static async Task<bool> HasOutstandingCaptureAsync(FluxKnowledgeDbContext context, SourceRevisionEntity source,
        SourceRootConfigurationEntity root, CancellationToken ct)
    {
        var proof = SqlRepositoryWorkRecovery.ReadDiscovery(source.CurrentDiscoveryEvidenceJson);
        var completed = proof is null ? null : await context.SourceScanRequests.SingleOrDefaultAsync(x =>
            x.Id == proof.ScanRequestId && x.SourceRootId == root.Id && x.State == (int)SourceScanRequestState.Completed, ct);
        // Legacy scans do not consume journal pending-content scopes. Their
        // existing authoritative full-scan/suppression contract still applies.
        if (completed?.IncrementalManifestJson is null) return false;
        var relative = SqlSourceChangeJournalStore.NormaliseRelative(Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath));
        var pending = await context.SourcePendingChanges.Where(x => x.SourceRootId == root.Id &&
            (x.ReasonFlags & ~SourcePendingReasons.RemovalPending) != 0)
            .Select(x => new { x.RelativePath, x.IsSubtree }).ToArrayAsync(ct);
        if (pending.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relative))) return true;
        var peers = await context.SourceRevisions.Where(x => x.SourceRootId == root.Id && x.OriginKind == 0 &&
            x.CanonicalPath == source.CanonicalPath && x.Id != source.Id).Select(x => x.CurrentDiscoveryEvidenceJson).ToArrayAsync(ct);
        var observations = peers.Select(SqlRepositoryWorkRecovery.ReadObservation).Where(x => x is not null).ToArray();
        if (observations.Length == 0) return false;
        foreach (var observation in observations)
        {
            var request = await context.SourceScanRequests.SingleOrDefaultAsync(x => x.Id == observation!.ScanRequestId && x.SourceRootId == root.Id, ct);
            // A later completed capture may supersede an older terminal attempt. An
            // outstanding/newer capture must retain its observation until completion.
            if (proof is null || request is null || observation!.ObservedAtUtc >= proof.ObservedAtUtc ||
                request.State is not ((int)SourceScanRequestState.Completed or (int)SourceScanRequestState.Failed)) return true;
        }
        return false;
    }

    internal static async Task PublishAsync(FluxKnowledgeDbContext context, PipelineRecordEntity record,
        DateTimeOffset now, CancellationToken ct)
    {
        if (record.RepositoryRecoveryBindingJson is null || record.SourceRevisionId is not { } sourceId) return;
        var selection = await ReadAsync(context, sourceId, ct);
        if (selection is null) return; // Legacy single-binding publication keeps its existing eligibility contract.
        foreach (var predecessor in selection.Predecessors)
        {
            predecessor.SuppressedAtUtc = now;
            if (predecessor.RetainUntilUtc is null || predecessor.RetainUntilUtc < now.AddDays(30))
                predecessor.RetainUntilUtc = now.AddDays(30);
            predecessor.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Withdraw();
            predecessor.RetentionEvidenceJson = JsonSerializer.Serialize(new
                { reason = "git-publication-superseded", successorSourceRevisionId = sourceId });
        }
    }

    internal static async Task<Guid?> RestorePublishedAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid rootId, long configurationRevision, DateTimeOffset now, CancellationToken ct, Guid? after = null)
    {
        await using var lookup = await factory.CreateDbContextAsync(ct);
        var candidates = (from entry in lookup.SourceInventoryEntries
            join source in lookup.SourceRevisions on entry.SourceRevisionId equals source.Id
            where entry.SourceRootId == rootId && !entry.IsDirectory && source.OriginKind == 0 && source.SuppressedAtUtc != null &&
                lookup.PipelineRecords.Any(record => record.SourceRevisionId == source.Id && !record.IsDeleted &&
                    record.CompletionCriteriaMet && record.CurrentStage == (int)PipelineStage.Publish && record.RepositoryRecoveryBindingJson != null)
            select source.Id).Distinct();
        var ids = await (after is { } cursor ? candidates.Where(x => x.CompareTo(cursor) > 0) : candidates)
            .OrderBy(x => x).Take(64).ToArrayAsync(ct);
        if (ids.Length == 0 && after is not null)
            ids = await candidates.OrderBy(x => x).Take(64).ToArrayAsync(ct);
        if (ids.Length == 0) return null;
        return await lookup.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct);
            if (await db.IndexState.AnyAsync(x => x.Id == 1 && x.CorpusRebuildOperationId != null, ct)) return after;
            foreach (var id in ids)
            {
                var selection = await ReadAsync(db, id, ct, allowSuppressed: true);
                if (selection is null || selection.Source.SuppressedAtUtc is null || selection.Root.ConfigurationRevision != configurationRevision ||
                    selection.Source.RetainUntilUtc is { } deadline && deadline <= now) continue;
                var relative = Path.GetRelativePath(selection.Root.CanonicalPath, selection.Source.CanonicalPath);
                var holds = await db.SourcePendingChanges.Where(x => x.SourceRootId == rootId &&
                    (x.ReasonFlags & SourcePendingReasons.RemovalPending) != 0).Select(x => new { x.RelativePath, x.IsSubtree }).ToArrayAsync(ct);
                if (holds.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relative))) continue;
                var records = await db.PipelineRecords.Where(x => x.SourceRevisionId == id && !x.IsDeleted && x.CompletionCriteriaMet &&
                    x.CurrentStage == (int)PipelineStage.Publish && x.ContentHash == selection.Source.ContentSha256).Take(2).ToArrayAsync(ct);
                if (records.Length != 1) continue;
                var record = records[0];
                var job = await db.Jobs.SingleOrDefaultAsync(x => x.PipelineRecordId == record.Id && x.SourceRevision == record.Revision &&
                    x.Stage == (int)PipelineStage.Publish && x.PublicState == (int)PublicJobState.Completed, ct);
                var canonical = await db.Artifacts.SingleOrDefaultAsync(x => x.PipelineRecordId == record.Id && x.SourceRevision == record.Revision &&
                    x.Stage == (int)PipelineStage.CanonicalIndex, ct);
                if (job is null || canonical?.SearchText is null || CodeDisclosureIntegrity.Hash(canonical.SearchText) != canonical.ContentHash ||
                    await db.Jobs.Where(x => x.PipelineRecordId == record.Id && x.SourceRevision == record.Revision &&
                        x.PublicState == (int)PublicJobState.Completed && x.Stage >= (int)PipelineStage.Extract && x.Stage <= (int)PipelineStage.Publish)
                        .Select(x => x.Stage).Distinct().CountAsync(ct) != 5) continue;
                var suppressed = selection.Source.SuppressedAtUtc;
                selection.Source.SuppressedAtUtc = null;
                if (await SqlRepositoryWorkRecovery.ReadEligibilityAsync(db, job, now, ct, completedPublication: true) is not null)
                {
                    selection.Source.SuppressedAtUtc = suppressed;
                    continue;
                }
                selection.Source.RetainUntilUtc = null;
                selection.Source.RetentionEvidenceJson = null;
                await PublishAsync(db, record, now, ct);
                await SqlPublishedPassageSelection.AdvanceVersionAsync(db, now, ct,
                    selection.Predecessors.Select(x => new IndexAffectedScope(2, x.Id)).Append(new(2, id)).ToArray());
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return (Guid?)ids[^1];
        });
    }
}
