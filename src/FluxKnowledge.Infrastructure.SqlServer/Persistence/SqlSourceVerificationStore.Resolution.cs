using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    private static SourceVerificationBinding BindingFor(SourceInventoryEntryEntity entry, SourceArtifactEntity? artifact)
    {
        SourceFileObservation? observation = null;
        try
        {
            using var document = JsonDocument.Parse(entry.ObservationJson ?? "{}");
            if (document.RootElement.TryGetProperty("Source", out var source)) observation = source.Deserialize<SourceFileObservation>();
        }
        catch (JsonException) { } // An inconsistent observation becomes capture debt, never verification evidence.
        return new(entry.Id, entry.RelativePath, entry.FileId, entry.SourceRevisionId, entry.SourceArtifactId,
            artifact?.ContentSha256, artifact?.ByteLength ?? 0, artifact?.StoreRelativePath, observation);
    }

    private static bool SameOriginalBinding(SourceVerificationBinding original, SourceVerificationBinding candidate) =>
        original.InventoryEntryId == candidate.InventoryEntryId && original.RelativePath == candidate.RelativePath &&
        original.RevisionId == candidate.RevisionId && original.ArtifactId == candidate.ArtifactId && original.FileId == candidate.FileId &&
        original.Sha256 == candidate.Sha256 && original.ByteLength == candidate.ByteLength && original.StoreRelativePath == candidate.StoreRelativePath;

    private static bool AffectsCandidate(SourcePendingChangeEntity pending, string relativePath) =>
        (pending.ReasonFlags & SourcePendingReasons.GitMembership) != 0 ||
        SourcePathScope.Contains(pending.RelativePath, pending.IsSubtree, relativePath) &&
        (new JournalChange(0, "", "", "", (uint)pending.ReasonFlags, false).ContentChanged ||
         (pending.ReasonFlags & SourcePendingReasons.ScopedNamespace) != 0);

    private static async Task ResolveCandidatesAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity root,
        SourceRootScanStateEntity state, SourceVerificationRunEntity run, SourceVerificationAuthority authority,
        SourceVerificationItemEntity[] rows, JournalCapability? barrier, DateTimeOffset now, CancellationToken ct)
    {
        if (barrier is null || state.CoveredUsn < barrier.NextUsn || barrier.JournalId.ToString("X16") != authority.JournalId) return;
        var originals = rows.ToDictionary(x => x.Id, x => JsonSerializer.Deserialize<SourceVerificationBinding>(x.BindingJson)
            ?? throw new InvalidDataException("SourceVerificationBindingMissing"));
        var keys = originals.Values.Select(x => SqlSourceChangeJournalStore.PathKey(x.RelativePath)).Distinct().ToArray();
        var entries = await db.SourceInventoryEntries.Where(x => x.SourceRootId == root.Id && !x.IsDirectory && keys.Contains(x.PathFingerprint))
            .ToDictionaryAsync(x => x.PathFingerprint, ct).ConfigureAwait(false);
        var revisionIds = entries.Values.Select(x => x.SourceRevisionId).Concat(originals.Values.Select(x => x.RevisionId))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var revisions = await db.SourceRevisions.Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var artifactIds = entries.Values.Select(x => x.SourceArtifactId).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var artifacts = await db.SourceArtifacts.Where(x => artifactIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var paths = originals.Values.Select(x => Path.GetFullPath(Path.Combine(root.CanonicalPath, x.RelativePath))).ToArray();
        var active = await db.SourceRevisions.Where(x => x.SourceRootId == root.Id && x.OriginKind == 0 && x.SuppressedAtUtc == null && paths.Contains(x.CanonicalPath))
            .Select(x => new { x.Id, x.CanonicalPath }).ToArrayAsync(ct).ConfigureAwait(false);
        var witnesses = revisions.Values.ToDictionary(x => x.Id, x => ReadRemovalWitness(x.RetentionEvidenceJson));
        var requestIds = witnesses.Values.Where(x => x is not null).Select(x => x!.SourceScanRequestId).Distinct().ToArray();
        var completed = await (from request in db.SourceScanRequests
            join job in db.SourceScanJobs on request.Id equals job.SourceScanRequestId
            where requestIds.Contains(request.Id) && request.State == (int)SourceScanRequestState.Completed && job.State == (int)SourceScanJobState.Completed
            select new { request.Id, request.SourceRootId, request.IncrementalManifestJson, job.LeaseGeneration }).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var latestCheck = await db.SourceVerificationItems.Where(x => x.RunId == run.Id && x.State == "AwaitingCapture")
            .MaxAsync(x => x.AttemptedAtUtc, ct).ConfigureAwait(false);
        var checkedAt = latestCheck >= now ? latestCheck.Value.AddTicks(1) : now;
        foreach (var row in rows)
        {
            // This is a metadata resolution attempt, not a checksum receipt. Advancing beyond
            // the prior maximum gives bounded fair rotation even if the wall clock moves back.
            if (row.State == "AwaitingCapture") row.AttemptedAtUtc = checkedAt;
            var original = originals[row.Id];
            if (pending.Any(x => AffectsCandidate(x, original.RelativePath))) continue;
            var fullPath = Path.GetFullPath(Path.Combine(root.CanonicalPath, original.RelativePath));
            entries.TryGetValue(SqlSourceChangeJournalStore.PathKey(original.RelativePath), out var entry);
            var revision = entry?.SourceRevisionId is { } revisionId ? revisions.GetValueOrDefault(revisionId) : null;
            var artifact = entry?.SourceArtifactId is { } artifactId ? artifacts.GetValueOrDefault(artifactId) : null;
            if (entry is not null && entry.BaselineEpoch == authority.BaselineEpoch && entry.RelativePath == original.RelativePath &&
                revision is { SuppressedAtUtc: null, OriginKind: 0 } && revision.SourceRootId == root.Id &&
                revision.CanonicalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
                artifact is { ReferenceCount: > 0 } && artifact.SourceRevisionId == revision.Id &&
                artifact.ContentSha256 == revision.ContentSha256 && artifact.ByteLength == revision.ByteLength &&
                !active.Any(x => x.Id != revision.Id && x.CanonicalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) &&
                (root.CrawlMode != (int)SourceDiscoveryMode.GitTracked || SqlRepositoryWorkRecovery.CanReuseCurrentDiscovery(root, revision, artifact)))
            {
                row.CandidateBindingJson = JsonSerializer.Serialize(BindingFor(entry, artifact));
                row.State = "Pending"; row.Reason = null;
                continue; // A current successor always takes precedence over an older removal witness.
            }
            if (entry is null || entry.Id != original.InventoryEntryId || entry.SourceRevisionId != original.RevisionId ||
                entry.FileId != original.FileId || entry.BaselineEpoch != authority.BaselineEpoch ||
                active.Any(x => x.CanonicalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) ||
                original.RevisionId is not { } oldId || !revisions.TryGetValue(oldId, out var old) ||
                witnesses.GetValueOrDefault(oldId) is not { Version: 1, Category: "Deleted" or "OutOfScope" } witness ||
                old.SourceRootId != root.Id || old.OriginKind != 0 || !old.CanonicalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
                old.SuppressedAtUtc != witness.SuppressedAtUtc || witness.SuppressedAtUtc < run.CreatedAtUtc || witness.SuppressedAtUtc > now ||
                witness.SourceRootId != root.Id || witness.SourceRevisionId != oldId || witness.RelativePath != original.RelativePath ||
                witness.ConfigurationRevision != authority.ConfigurationRevision || witness.BaselineEpoch != authority.BaselineEpoch ||
                witness.RecoveryGeneration != authority.RecoveryGeneration || witness.JournalId != authority.JournalId ||
                witness.CoveredThroughUsn > barrier.NextUsn || witness.LeaseGeneration <= 0 || witness.ManifestId == Guid.Empty ||
                !completed.TryGetValue(witness.SourceScanRequestId, out var owner) || owner.SourceRootId != root.Id || owner.LeaseGeneration != witness.LeaseGeneration ||
                ReadManifest(owner.IncrementalManifestJson) is not { } manifest || manifest.ManifestId != witness.ManifestId ||
                manifest.BaselineEpoch != authority.BaselineEpoch || manifest.RecoveryGeneration != authority.RecoveryGeneration ||
                manifest.LeaseGeneration != witness.LeaseGeneration || manifest.Cursor?.JournalId.ToString("X16") != authority.JournalId ||
                root.CrawlMode == (int)SourceDiscoveryMode.GitTracked &&
                    (witness.RepositoryIdentity != SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson) ||
                     witness.GitInventoryGeneration is not { Length: 64 } || witness.GitInventoryGeneration != state.GitInventoryGeneration)) continue;
            row.State = witness.Category; row.Reason = null; row.CompletedAtUtc = witness.SuppressedAtUtc;
            row.ReceiptJson = JsonSerializer.Serialize(witness);
        }
    }

    private static SourceAuthoritativeRemovalWitness? ReadRemovalWitness(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<SourceAuthoritativeRemovalWitness>(json); }
        catch (JsonException) { return null; }
    }
    private static SourceIncrementalPlan? ReadManifest(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<SourceIncrementalPlan>(json); }
        catch (JsonException) { return null; }
    }
}
