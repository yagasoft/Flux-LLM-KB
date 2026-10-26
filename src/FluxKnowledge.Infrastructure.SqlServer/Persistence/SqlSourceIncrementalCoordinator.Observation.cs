using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    internal Action<string, string>? ReuseRejected { get; init; }

    public async ValueTask<SourceRevisionId?> TryReuseAsync(SourceRootConfiguration root, SourceScanRequest request,
        SourceIncrementalPlan plan, string relativePath, CancellationToken ct)
    {
        if (artifacts is not ISourceRetainedObservationStore retained || !plan.IsFallback && plan.Cursor is null) return Reject("scan-scope");
        var key = SqlSourceChangeJournalStore.PathKey(relativePath);
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var cached = await lookup.SourceInventoryEntries.AsNoTracking().SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value &&
            x.PathFingerprint == key && x.BaselineEpoch == plan.BaselineEpoch && !x.IsDirectory, ct).ConfigureAwait(false);
        if (cached?.ObservationJson is null || cached.SourceRevisionId is null) return Reject("inventory-missing");
        var proof = JsonSerializer.Deserialize<CachedObservation>(cached.ObservationJson);
        if (proof is null || proof.Version is not (2 or 3) || proof.RecoveryGeneration != plan.RecoveryGeneration) return Reject("receipt-epoch");
        var sealedProof = IsQualifiedSeal(proof, root, plan, cached);
        if (!plan.IsFallback && !sealedProof) return Reject("seal:" + JsonSerializer.Serialize(new { cached.IsSealed, cached.SealFailureCode, cached.LatestJournalUsn, cached.LatestJournalReason, proof.SealUsn }));
        if (plan.Targets.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relativePath) &&
            !ContentObligationCovered(proof, sealedProof, x.ReasonFlags, x.LastContentUsn, x.LastUnjournaledGeneration))) return Reject("claimed-content-obligation");
        var artifact = await lookup.SourceArtifacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cached.SourceArtifactId &&
            x.SourceRevisionId == cached.SourceRevisionId, ct).ConfigureAwait(false);
        if (artifact is null || artifact.ChecksumVerifiedAtUtc == default || artifact.ReferenceCount <= 0) return Reject("artifact-authority");
        try
        {
            using var source = observations.Open(root, relativePath);
            if (source.Observation.Identity != proof.Source.Identity || source.Observation.ByteLength != proof.Source.ByteLength ||
                source.Observation.LinkCount != 1) return Reject("source-identity-or-size");
            if (plan.IsFallback && source.Observation != proof.Source) return Reject("fallback-source-metadata");
            // Acquire first, then drain: an earlier drain cannot protect a timestamp-restored write.
            JournalCapability? barrier = null;
            if (!plan.IsFallback)
            {
                barrier = await reader.QueryAsync(plan.Cursor!.VolumeIdentity, ct).ConfigureAwait(false);
                await DrainThroughAsync(barrier, ct).ConfigureAwait(false);
            }
            await using var blob = await retained.OpenObservationAsync(artifact.StoreRelativePath, artifact.ContentSha256,
                artifact.ByteLength, false, ct).ConfigureAwait(false);
            if (blob.Observation != proof.Retained) return Reject("retained-metadata:" + JsonSerializer.Serialize(new { Expected = proof.Retained, Actual = blob.Observation }));
            SourceRevisionId? result = null;
            await _journal.ExecuteAsync(async db =>
            {
                var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                if (state.BaselineEpoch != plan.BaselineEpoch || state.RecoveryGeneration != plan.RecoveryGeneration ||
                    state.PolicyFingerprint != SqlRepositoryWorkRecovery.Policy(current) ||
                    // A failed baseline can leave valid individual seals in this epoch.
                    // Reuse does not complete or skip its required namespace walk.
                    (plan.IsFallback ? state.Mode != "Fallback" : state.Mode is not ("Journal" or "JournalPartial") &&
                        !(plan.IsBaseline && state.Mode == "Baselining")) ||
                    barrier is not null && state.CoveredUsn < barrier.NextUsn) { Reject("confirmation-epoch"); return; }
                var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id.Value).ToArrayAsync(ct).ConfigureAwait(false);
                // Pure metadata debt remains for a later namespace sweep; it does not
                // invalidate this guarded content receipt or get acknowledged by reuse.
                if (pending.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relativePath) &&
                    (!ContentObligationCovered(proof, sealedProof, x.ReasonFlags, x.LastContentUsn, x.LastUnjournaledGeneration) ||
                     x.Generation > plan.Generation && (x.ReasonFlags & (SourcePendingReasons.ScopedNamespace | SourcePendingReasons.GitMembership)) != 0))) { Reject("current-pending-obligation"); return; }
                var entry = await db.SourceInventoryEntries.SingleAsync(x => x.Id == cached.Id, ct).ConfigureAwait(false);
                if (!plan.IsFallback && !IsQualifiedSeal(proof, root, plan, entry)) { Reject("confirmation-seal"); return; }
                var revision = await db.SourceRevisions.SingleOrDefaultAsync(x => x.Id == entry.SourceRevisionId && x.SourceRootId == root.Id.Value &&
                    x.SuppressedAtUtc == null && x.OriginKind == 0, ct).ConfigureAwait(false);
                var bound = await db.SourceArtifacts.SingleOrDefaultAsync(x => x.Id == entry.SourceArtifactId && x.SourceRevisionId == entry.SourceRevisionId, ct).ConfigureAwait(false);
                if (revision is null || bound is null || entry.ObservationJson != cached.ObservationJson || entry.BaselineEpoch != plan.BaselineEpoch ||
                    bound.ContentSha256 != artifact.ContentSha256 || bound.ByteLength != artifact.ByteLength ||
                    !revision.CanonicalPath.Equals(Path.GetFullPath(relativePath, root.CanonicalPath), StringComparison.Ordinal)) { Reject("confirmation-binding"); return; }
                // A content seal cannot repair a Git observation whose original completion
                // never authorised it. Recapture through the existing Observe path instead.
                if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked &&
                    !SqlRepositoryWorkRecovery.CanReuseCurrentDiscovery(current, revision, bound)) { Reject("git-discovery-authority"); return; }
                source.ValidateCurrent(); blob.ValidateCurrent();
                // A heuristic fallback reuse retains its existing receipt and checksum time.
                if (!plan.IsFallback)
                {
                    var next = JsonSerializer.Serialize(proof with { Source = source.Observation, Retained = blob.Observation });
                    if (entry.ObservationJson != next) entry.ObservationJson = next;
                }
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
        using (var beforeSeal = observations.Open(root, file.RelativePath))
            if (beforeSeal.Observation != file.Observation) throw new SourceSnapshotChangedException("SourceCaptureObservationChanged");
        await using var blob = await retained.OpenObservationAsync(artifact.StoreRelativePath, file.ContentSha256, file.ByteLength, true, ct).ConfigureAwait(false);
        var profile = _qualifications.GetProfile(root.PhysicalIdentityFingerprint, root.CanonicalPath);
        SourceCaptureJournalSeal? seal = null;
        JournalChange? sealRecord = null;
        var failure = profile is null ? "root-not-qualified" : !options.SealingEnabled ? "new-seal-writes-disabled" : "capture-not-sealable";
        if (!plan.IsFallback && plan.Cursor is { } sealCursor && options.SealingEnabled && profile is not null &&
            capture is IJournalSealableSourceCapture sealable && ReferenceEquals(file, capture.Snapshot) &&
            file.SourcePath?.RootIdentityFingerprint == root.PhysicalIdentityFingerprint)
        {
            try
            {
                seal = await sealable.SealAsync(ct).ConfigureAwait(false);
                var page = await reader.ReadAsync(new(sealCursor.VolumeIdentity, sealCursor.JournalId, seal.Usn), 65536, ct).ConfigureAwait(false);
                sealRecord = page.Changes.SingleOrDefault(x => x.Usn == seal.Usn && x.FileId == seal.Observation.FileId &&
                    (x.Reason & 0x80000000u) != 0);
                if (sealRecord is null) { seal = null; failure = "seal-record-unmatched"; }
            }
            catch (SourceCaptureSealUnavailableException error) { seal = null; failure = error.Reason; }
            catch (IOException error) when (error is not (SourceJournalGapException or SourceSnapshotChangedException))
            { seal = null; failure = "seal-unavailable"; }
            catch (UnauthorizedAccessException) { seal = null; failure = "seal-access-refused"; }
        }
        using var source = observations.Open(root, file.RelativePath);
        if (source.Observation.Identity != file.Observation.Identity || source.Observation.ByteLength != file.ByteLength ||
            source.Observation.LastWriteTicks != file.Observation.LastWriteTicks ||
            seal is not null && (source.Observation.LinkCount != 1 || source.Observation != seal.Observation))
            throw new SourceSnapshotChangedException("SourceCaptureSealObservationChanged");
        JournalCapability? barrier = null;
        if (plan.Cursor is { } cursor)
        {
            barrier = await reader.QueryAsync(cursor.VolumeIdentity, ct).ConfigureAwait(false);
            await DrainThroughAsync(barrier, ct).ConfigureAwait(false);
        }
        BeforeCaptureAttestation?.Invoke();
        await _journal.ExecuteAsync(async db =>
        {
            await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            SqlSourceIncrementalState.RequirePlan(state, plan);
            if (barrier is not null && state.CoveredUsn < barrier.NextUsn) throw new SourceSnapshotChangedException("SourceCaptureBarrierNotCovered");
            if (seal is not null && (profile != _qualifications.GetProfile(root.PhysicalIdentityFingerprint, root.CanonicalPath) ||
                barrier?.JournalId != plan.Cursor!.JournalId || seal.Usn >= barrier.NextUsn))
                throw new SourceSnapshotChangedException("SourceCaptureSealAuthorityChanged");
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
            entry.ObservationJson = JsonSerializer.Serialize(new CachedObservation(3, source.Observation, blob.Observation,
                seal is not null, state.RecoveryGeneration, seal?.Usn, seal is null ? null : profile,
                plan.Generation, seal is null ? null : plan.Cursor!.JournalId.ToString("X16")));
            entry.IsSealed = seal is not null; entry.SealProfileId = seal is null ? null : profile;
            entry.SealFailureCode = seal is null ? failure : null;
            entry.NextForcedCheckAtUtc = seal is null ? time.GetUtcNow().Add(root.ReconciliationCadence) : null;
            if (seal is not null && (entry.LatestJournalUsn is null || entry.LatestJournalUsn < seal.Usn))
            { entry.LatestJournalUsn = seal.Usn; entry.LatestJournalReason = sealRecord!.Reason; }
            entry.LastVerifiedAtUtc = time.GetUtcNow();
            if (seal is not null)
            {
                var pending = await db.SourcePendingChanges.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value && x.PathFingerprint == key, ct).ConfigureAwait(false);
                // The matched native CLOSE was generated by this capture after its
                // manifest froze. Its exact metadata-only row and valid receipt share
                // one commit. Every mixed, earlier/later or unknown obligation remains.
                if (pending is { ReasonFlags: 0x80000000L, IsSubtree: false, LastContentUsn: 0, LastUnjournaledGeneration: 0 } &&
                    pending.FirstUsn == seal.Usn && pending.LastUsn == seal.Usn &&
                    pending.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase)) db.SourcePendingChanges.Remove(pending);
            }
        }, ct, () => { source.ValidateCurrent(); blob.ValidateCurrent(); BeforeCaptureAttestationCommit?.Invoke(); }).ConfigureAwait(false);
    }

    private bool IsQualifiedSeal(CachedObservation proof, SourceRootConfiguration root, SourceIncrementalPlan plan, SourceInventoryEntryEntity entry) =>
        proof is { Version: 3, JournalCovered: true, SealUsn: > 0, CaptureGeneration: >= 0, SealProfileId: not null } &&
        entry.IsSealed && proof.SealProfileId == entry.SealProfileId &&
        proof.SealProfileId == _qualifications.GetProfile(root.PhysicalIdentityFingerprint, root.CanonicalPath) &&
        proof.JournalId == plan.Cursor?.JournalId.ToString("X16");

    private static bool ContentObligationCovered(CachedObservation proof, bool qualified, long reasons, long? contentUsn, long? unjournaledGeneration) =>
        !new JournalChange(0, "", "", "", (uint)reasons, false).ContentChanged ||
        qualified && contentUsn is not null && unjournaledGeneration is not null &&
        contentUsn <= proof.SealUsn && unjournaledGeneration <= proof.CaptureGeneration;
}
