using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    private async ValueTask<SourceRevisionId?> TryAdoptLegacyMetadataAsync(SourceRootConfiguration root, SourceScanRequest request,
        SourceIncrementalPlan plan, string relativePath, SourceInventoryEntryEntity? cached, CancellationToken ct)
    {
        if (plan.ForceFullContent || artifacts is not ISourceRetainedObservationStore retained ||
            plan.Targets.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relativePath) && IsFileContentEvent(x, relativePath))) return null;
        var path = Path.GetFullPath(relativePath, root.CanonicalPath);
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var revisions = await lookup.SourceRevisions.AsNoTracking().Where(x => x.SourceRootId == root.Id.Value &&
            x.CanonicalPath == path && x.OriginKind == 0 && x.SuppressedAtUtc == null).Take(2).ToArrayAsync(ct).ConfigureAwait(false);
        if (revisions.Length != 1 || revisions[0].FileLastWriteAtUtc is null) return null;
        var prior = revisions[0];
        var blobs = await lookup.SourceArtifacts.AsNoTracking().Where(x => x.SourceRevisionId == prior.Id).Take(2).ToArrayAsync(ct).ConfigureAwait(false);
        if (blobs.Length != 1) return null;
        var artifact = blobs[0];
        if (artifact.ChecksumVerifiedAtUtc == default || artifact.ReferenceCount <= 0 || artifact.ByteLength != prior.ByteLength ||
            artifact.ContentSha256 != prior.ContentSha256 || cached?.SourceRevisionId is { } owner && owner != prior.Id ||
            cached?.SourceArtifactId is { } blobId && blobId != artifact.Id) return null;
        try
        {
            using var source = observations.Open(root, relativePath);
            if (source.Observation.Identity != prior.StableSourceIdentity || source.Observation.ByteLength != prior.ByteLength ||
                source.Observation.ByteLength > root.MaximumFileBytes ||
                source.Observation.LastWriteTicks != prior.FileLastWriteAtUtc!.Value.UtcDateTime.ToFileTimeUtc() ||
                cached is { FileId.Length: > 0 } && cached.FileId != source.Observation.FileId) return null;
            await using var blob = await retained.OpenObservationAsync(artifact.StoreRelativePath, artifact.ContentSha256,
                artifact.ByteLength, false, ct).ConfigureAwait(false);
            SourceRevisionId? result = null;
            await _journal.ExecuteAsync(async db =>
            {
                if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked)
                    await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
                var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                SqlSourceIncrementalState.RequirePlan(state, plan);
                if (state.PolicyFingerprint != SqlRepositoryWorkRecovery.Policy(current)) return;
                var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id.Value).ToArrayAsync(ct).ConfigureAwait(false);
                if (pending.Any(x => SourcePathScope.Contains(x.RelativePath, x.IsSubtree, relativePath) &&
                    IsFileContentEvent(new(x.RelativePath, x.Generation, x.IsSubtree, x.ReasonFlags), relativePath))) return;
                var owners = await db.SourceRevisions.Where(x => x.SourceRootId == root.Id.Value && x.CanonicalPath == path &&
                    x.OriginKind == 0 && x.SuppressedAtUtc == null).Take(2).ToArrayAsync(ct).ConfigureAwait(false);
                var bound = await db.SourceArtifacts.SingleOrDefaultAsync(x => x.Id == artifact.Id && x.SourceRevisionId == prior.Id, ct).ConfigureAwait(false);
                if (owners.Length != 1 || owners[0].Id != prior.Id || !owners[0].RowVersion.SequenceEqual(prior.RowVersion) ||
                    bound is null || !bound.RowVersion.SequenceEqual(artifact.RowVersion) ||
                    !owners[0].CanonicalPath.Equals(path, StringComparison.Ordinal)) return;
                var key = SqlSourceChangeJournalStore.PathKey(relativePath);
                var entry = await db.SourceInventoryEntries.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value && x.PathFingerprint == key, ct).ConfigureAwait(false);
                if (cached is null ? entry is not null : entry is null || entry.Id != cached.Id ||
                    !entry.RowVersion.SequenceEqual(cached.RowVersion)) return;
                if (entry?.ObservationJson is not null) return;
                if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked &&
                    (plan.GitInventoryGeneration is not { } generation ||
                     !SqlRepositoryWorkRecovery.ObserveMetadata(current, owners[0], bound, request.Id.Value,
                         request.Lease!.Generation, generation, time.GetUtcNow()))) return;
                source.ValidateCurrent(); blob.ValidateCurrent();
                if (entry is null)
                {
                    entry = new() { Id = Guid.NewGuid(), SourceRootId = root.Id.Value, PathFingerprint = key };
                    db.SourceInventoryEntries.Add(entry);
                }
                entry.RelativePath = relativePath; entry.FileId = source.Observation.FileId; entry.ParentId = source.Observation.ParentId;
                entry.SourceRevisionId = prior.Id; entry.SourceArtifactId = artifact.Id; entry.BaselineEpoch = plan.BaselineEpoch;
                // A current metadata fact, never a new ingestion/hash/coverage receipt.
                // Null capture generation ensures this cannot consume a file event.
                entry.ObservationJson = JsonSerializer.Serialize(new CachedObservation(4, source.Observation, blob.Observation,
                    false, state.RecoveryGeneration, MetadataOnly: true));
                result = new(prior.Id);
            }, ct, () => { source.ValidateCurrent(); blob.ValidateCurrent(); }).ConfigureAwait(false);
            return result;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
