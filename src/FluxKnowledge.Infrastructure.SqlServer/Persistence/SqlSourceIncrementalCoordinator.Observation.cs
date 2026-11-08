using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    internal Action<string, string>? ReuseRejected { get; init; }

    public async ValueTask<SourceRevisionId?> TryReuseAsync(SourceRootConfiguration root, SourceScanRequest request,
        SourceIncrementalPlan plan, string relativePath, CancellationToken ct)
    {
        if (plan.ForceFullContent) return Reject("manual-full-content");
        if (artifacts is not ISourceRetainedObservationStore retained) return Reject("scan-scope");
        var key = SqlSourceChangeJournalStore.PathKey(relativePath);
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var cached = await lookup.SourceInventoryEntries.AsNoTracking().SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value &&
            x.PathFingerprint == key && !x.IsDirectory, ct).ConfigureAwait(false);
        if (cached?.ObservationJson is null)
            return await TryAdoptLegacyMetadataAsync(root, request, plan, relativePath, cached, ct).ConfigureAwait(false);
        if (cached.SourceRevisionId is null) return Reject("inventory-missing");
        var proof = JsonSerializer.Deserialize<CachedObservation>(cached.ObservationJson);
        if (proof is null || proof.Version is not (2 or 3 or 4)) return Reject("receipt-format");
        if (plan.Targets.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relativePath) &&
            RequiresContentCapture(x, proof, relativePath))) return Reject("claimed-content-obligation");
        var artifact = await lookup.SourceArtifacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cached.SourceArtifactId &&
            x.SourceRevisionId == cached.SourceRevisionId, ct).ConfigureAwait(false);
        if (artifact is null || artifact.ChecksumVerifiedAtUtc == default || artifact.ReferenceCount <= 0) return Reject("artifact-authority");
        try
        {
            using var source = observations.Open(root, relativePath);
            if (source.Observation.ByteLength > root.MaximumFileBytes) return Reject("current-policy-byte-limit");
            if (source.Observation.Identity != proof.Source.Identity || source.Observation.ByteLength != proof.Source.ByteLength ||
                source.Observation.LinkCount < 1) return Reject("source-identity-or-size");
            if (source.Observation.LastWriteTicks != proof.Source.LastWriteTicks) return Reject("source-modification-time");
            await using var blob = await retained.OpenObservationAsync(artifact.StoreRelativePath, artifact.ContentSha256,
                artifact.ByteLength, false, ct).ConfigureAwait(false);
            if (blob.Observation != proof.Retained) return Reject("retained-metadata:" + JsonSerializer.Serialize(new { Expected = proof.Retained, Actual = blob.Observation }));
            SourceRevisionId? result = null;
            await _journal.ExecuteAsync(async db =>
            {
                if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked)
                    await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
                var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                if (state.BaselineEpoch != plan.BaselineEpoch || state.RecoveryGeneration != plan.RecoveryGeneration ||
                    state.PolicyFingerprint != SqlRepositoryWorkRecovery.Policy(current) ||
                    state.Mode == "Legacy") { Reject("confirmation-epoch"); return; }
                var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id.Value).ToArrayAsync(ct).ConfigureAwait(false);
                // Pure metadata debt remains for a later namespace sweep; it does not
                // invalidate this guarded content receipt or get acknowledged by reuse.
                if (pending.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relativePath) &&
                    RequiresContentCapture(new(x.RelativePath, x.Generation, x.IsSubtree, x.ReasonFlags), proof, relativePath)))
                { Reject("current-pending-obligation"); return; }
                var entry = await db.SourceInventoryEntries.SingleAsync(x => x.Id == cached.Id, ct).ConfigureAwait(false);
                var revision = await db.SourceRevisions.SingleOrDefaultAsync(x => x.Id == entry.SourceRevisionId && x.SourceRootId == root.Id.Value &&
                    x.SuppressedAtUtc == null && x.OriginKind == 0, ct).ConfigureAwait(false);
                var bound = await db.SourceArtifacts.SingleOrDefaultAsync(x => x.Id == entry.SourceArtifactId && x.SourceRevisionId == entry.SourceRevisionId, ct).ConfigureAwait(false);
                if (revision is null || bound is null || entry.ObservationJson != cached.ObservationJson ||
                    bound.ContentSha256 != artifact.ContentSha256 || bound.ByteLength != artifact.ByteLength ||
                    !revision.CanonicalPath.Equals(Path.GetFullPath(relativePath, root.CanonicalPath), StringComparison.Ordinal)) { Reject("confirmation-binding"); return; }
                if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked &&
                    !SqlRepositoryWorkRecovery.CanReuseCurrentDiscovery(current, revision, bound) &&
                    (plan.GitInventoryGeneration is not { } gitGeneration ||
                     !SqlRepositoryWorkRecovery.ObserveMetadata(current, revision, bound, request.Id.Value,
                         request.Lease!.Generation, gitGeneration, time.GetUtcNow())))
                { Reject("git-discovery-authority"); return; }
                source.ValidateCurrent(); blob.ValidateCurrent();
                // This is a current metadata observation, not a new hash, seal or
                // journal-coverage receipt. Keep the original capture evidence.
                entry.BaselineEpoch = plan.BaselineEpoch;
                result = new(revision.Id);
            }, ct, () => { source.ValidateCurrent(); blob.ValidateCurrent(); }).ConfigureAwait(false);
            return result;
        }
        catch (IOException error) { return Reject("io:" + error.Message); }
        catch (UnauthorizedAccessException error) { return Reject("access:" + error.Message); }

        SourceRevisionId? Reject(string reason) { ReuseRejected?.Invoke(relativePath, reason); return null; }
    }

    public async ValueTask RecordCaptureAsync(SourceRootConfiguration root, SourceScanRequest request,
        SourceIncrementalPlan plan, SourceDiscoveredFile file, SourceRevisionId revision, ISourceFileCapture? capture, CancellationToken ct)
    {
        if (file.Observation is null || artifacts is not ISourceRetainedObservationStore retained) return;
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var artifact = await lookup.SourceArtifacts.AsNoTracking().SingleAsync(x => x.SourceRevisionId == revision.Value, ct).ConfigureAwait(false);
        using var source = observations.Open(root, file.RelativePath);
        if (source.Observation != file.Observation)
            throw new SourceSnapshotChangedException("SourceCaptureObservationChanged");
        // Actual ingestion retains its hash and retained-publication checks.
        // No continuous journal history or content seal is asserted by this receipt.
        await using var blob = await retained.OpenObservationAsync(artifact.StoreRelativePath, file.ContentSha256, file.ByteLength, true, ct).ConfigureAwait(false);
        BeforeCaptureAttestation?.Invoke();
        await _journal.ExecuteAsync(async db =>
        {
            await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            SqlSourceIncrementalState.RequirePlan(state, plan);
            var key = SqlSourceChangeJournalStore.PathKey(file.RelativePath);
            var entry = await db.SourceInventoryEntries.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value && x.PathFingerprint == key, ct).ConfigureAwait(false);
            if (entry is null)
            {
                entry = new SourceInventoryEntryEntity { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, PathFingerprint = key };
                db.SourceInventoryEntries.Add(entry);
            }
            if (entry.RelativePath.Length > 0 && !entry.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SourceObservationPathCollision");
            source.ValidateCurrent(); blob.ValidateCurrent();
            entry.RelativePath = file.RelativePath; entry.FileId = source.Observation.FileId; entry.ParentId = source.Observation.ParentId;
            entry.IsDirectory = false; entry.IsAncestor = false; entry.BaselineEpoch = state.BaselineEpoch;
            entry.SourceRevisionId = revision.Value; entry.SourceArtifactId = artifact.Id;
            entry.ObservationJson = JsonSerializer.Serialize(new CachedObservation(4, source.Observation, blob.Observation,
                false, state.RecoveryGeneration, CaptureGeneration: plan.Generation));
            entry.IsSealed = false; entry.SealProfileId = null; entry.SealFailureCode = null;
            entry.NextForcedCheckAtUtc = null;
            entry.LastVerifiedAtUtc = time.GetUtcNow(); // The normal ingestion above actually hashed these bytes.
        }, ct, () => { source.ValidateCurrent(); blob.ValidateCurrent(); BeforeCaptureAttestationCommit?.Invoke(); }).ConfigureAwait(false);
    }

    private static bool IsFileContentEvent(SourceScanTarget target, string relativePath) =>
        target.RelativePath != "." && (!target.IsSubtree || target.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) &&
        ((target.ReasonFlags & SourcePendingReasons.FileEvent) != 0 ||
            new JournalChange(0, "", "", "", (uint)target.ReasonFlags, false).ContentChanged);

    private static bool RequiresContentCapture(SourceScanTarget target, CachedObservation proof, string relativePath) =>
        IsFileContentEvent(target, relativePath) &&
        (proof.CaptureGeneration is null || target.Generation > proof.CaptureGeneration);
}
