using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    public async ValueTask<bool> CheckpointAsync(SourceVerificationBatch batch, SourceVerificationItem item, SourceVerificationDigest source,
        IRetainedFileObservationLease retained, DateTimeOffset retainedHashedAtUtc, long sourceBytesRead, long retainedBytesRead, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBytesRead);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytesRead);
        if (incrementalCoordinator is null) throw new InvalidOperationException("SourceVerificationJournalNotConfigured");
        var barrier = await incrementalCoordinator.ReadVerificationBarrierAsync(batch.Authority.VolumeIdentity, batch.Authority.JournalId, ct).ConfigureAwait(false);
        var accepted = false;
        await _transactions.ExecuteAsync(async db =>
        {
            accepted = false;
            var root = await RequireOwnedAsync(db, batch.Root, batch.Request, ct).ConfigureAwait(false);
            var run = await db.SourceVerificationRuns.SingleAsync(x => x.Id == batch.RunId, ct).ConfigureAwait(false);
            var request = await db.SourceScanRequests.SingleAsync(x => x.Id == batch.Request.Id.Value, ct).ConfigureAwait(false);
            var progress = RequireBatchProgress(request, batch, sourceBytesRead, retainedBytesRead);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
            var row = await db.SourceVerificationItems.SingleAsync(x => x.Id == item.Id && x.RunId == batch.RunId, ct).ConfigureAwait(false);
            if (!Matches(root, state, batch.Authority) || state.CoveredUsn < barrier.NextUsn)
                throw new SourceSnapshotChangedException("SourceVerificationAuthorityChanged");
            if (state.VerificationPaused || state.VerificationPolicyRevision != batch.PolicyRevision || !options.Enabled ||
                source.AttestedAtUtc >= batch.WindowEndUtc || retainedHashedAtUtc >= batch.WindowEndUtc ||
                !run.Active) return;
            if (row.State is "Verified" or "CapturedSuccessor")
            {
                SaveBatchProgress(run, request, progress, sourceBytesRead, retainedBytesRead, progress.Completed);
                accepted = true; return;
            }
            if (progress.Completed) throw new InvalidDataException("SourceVerificationAttemptFinished");
            var binding = item.Binding;
            if ((row.CandidateBindingJson ?? row.BindingJson) != JsonSerializer.Serialize(binding) || source.Sha256 != binding.Sha256 || source.BytesRead != binding.ByteLength ||
                source.Observation.Identity != binding.Source?.Identity || source.Observation.FileId != binding.FileId ||
                source.AttestedAtUtc < batch.CycleCreatedAtUtc || source.AttestedAtUtc > time.GetUtcNow() ||
                retainedHashedAtUtc < batch.CycleCreatedAtUtc || retainedHashedAtUtc > time.GetUtcNow() || retained.Observation.ByteLength != binding.ByteLength)
                throw new InvalidDataException("SourceVerificationProofBindingInvalid");
            var entry = await db.SourceInventoryEntries.SingleOrDefaultAsync(x => x.Id == binding.InventoryEntryId, ct).ConfigureAwait(false);
            var revision = await db.SourceRevisions.SingleOrDefaultAsync(x => x.Id == binding.RevisionId && x.SourceRootId == root.Id &&
                x.OriginKind == 0 && x.SuppressedAtUtc == null, ct).ConfigureAwait(false);
            var artifact = await db.SourceArtifacts.SingleOrDefaultAsync(x => x.Id == binding.ArtifactId && x.SourceRevisionId == binding.RevisionId, ct).ConfigureAwait(false);
            var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id).ToArrayAsync(ct).ConfigureAwait(false);
            if (entry is null || entry.SourceRevisionId != binding.RevisionId || entry.SourceArtifactId != binding.ArtifactId ||
                entry.BaselineEpoch != batch.Authority.BaselineEpoch || entry.FileId != binding.FileId || entry.RelativePath != binding.RelativePath ||
                revision is null || revision.ContentSha256 != binding.Sha256 || revision.ByteLength != binding.ByteLength ||
                artifact is null || artifact.ReferenceCount <= 0 || artifact.ContentSha256 != binding.Sha256 || artifact.ByteLength != binding.ByteLength ||
                artifact.StoreRelativePath != binding.StoreRelativePath ||
                root.CrawlMode == (int)SourceDiscoveryMode.GitTracked && !SqlRepositoryWorkRecovery.CanReuseCurrentDiscovery(root, revision, artifact) ||
                pending.Any(x => AffectsCandidate(x, binding.RelativePath)))
            {
                row.State = "AwaitingCapture"; row.Reason = "candidate-changed";
                return;
            }
            retained.ValidateCurrent();
            var original = JsonSerializer.Deserialize<SourceVerificationBinding>(row.BindingJson) ?? throw new InvalidDataException("SourceVerificationBindingMissing");
            row.State = SameOriginalBinding(original, binding) ? "Verified" : "CapturedSuccessor";
            row.Reason = null; row.AttemptCount++; row.AttemptedAtUtc = time.GetUtcNow(); row.CompletedAtUtc = time.GetUtcNow();
            row.ReceiptJson = JsonSerializer.Serialize(new SourceVerificationReceipt(source, retained.Observation, retainedHashedAtUtc));
            row.ReceiptSha256 = source.Sha256; row.ReceiptByteLength = source.BytesRead;
            SaveBatchProgress(run, request, progress, sourceBytesRead, retainedBytesRead, completed: false);
            // Artifact acquisition metadata is immutable. Fresh hash times belong to this receipt.
            if (entry.LastVerifiedAtUtc is null || entry.LastVerifiedAtUtc < source.AttestedAtUtc) entry.LastVerifiedAtUtc = source.AttestedAtUtc;
            accepted = true;
        }, ct, retained.ValidateCurrent).ConfigureAwait(false);
        return accepted;
    }

    public async ValueTask DeferAsync(SourceVerificationBatch batch, SourceVerificationItem item, string reason, bool requestCapture, CancellationToken ct)
    {
        await _transactions.ExecuteAsync(async db =>
        {
            await RequireOwnedAsync(db, batch.Root, batch.Request, ct).ConfigureAwait(false);
            var row = await db.SourceVerificationItems.SingleAsync(x => x.Id == item.Id && x.RunId == batch.RunId, ct).ConfigureAwait(false);
            if (row.State is "Verified" or "CapturedSuccessor" or "Deleted" or "OutOfScope") return;
            row.State = requestCapture ? "AwaitingCapture" : "Deferred";
            row.Reason = reason; row.AttemptCount++; row.AttemptedAtUtc = time.GetUtcNow();
            if (requestCapture) await QueueForcedCaptureAsync(db, batch.Root.Id.Value, item.Binding.RelativePath, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task QueueForcedCaptureAsync(FluxKnowledgeDbContext db, Guid rootId, string relativePath, CancellationToken ct)
    {
        var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId, ct).ConfigureAwait(false);
        var key = SqlSourceChangeJournalStore.PathKey(relativePath);
        var pending = await db.SourcePendingChanges.SingleOrDefaultAsync(x => x.SourceRootId == rootId && x.PathFingerprint == key, ct).ConfigureAwait(false);
        if (pending is null)
        {
            if (await db.SourcePendingChanges.CountAsync(x => x.SourceRootId == rootId, ct).ConfigureAwait(false) >= 10000)
            { SqlSourceIncrementalState.InvalidateCoverage(state, "verification-pending-overflow", state.CoveredUsn); return; }
            pending = new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = relativePath, PathFingerprint = key };
            db.SourcePendingChanges.Add(pending);
        }
        pending.Generation = ++state.Generation; pending.ReasonFlags |= 1; pending.LastContentUsn ??= pending.LastUsn;
        pending.LastUnjournaledGeneration = pending.Generation; pending.FirstObservedAtUtc ??= time.GetUtcNow(); pending.NotBeforeUtc = time.GetUtcNow();
        var entry = await db.SourceInventoryEntries.SingleOrDefaultAsync(x => x.SourceRootId == rootId && x.PathFingerprint == key, ct).ConfigureAwait(false);
        if (entry is not null) { entry.IsSealed = false; entry.NextForcedCheckAtUtc = time.GetUtcNow(); }
    }
}
