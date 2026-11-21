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
        if (artifacts is not ISourceRetainedObservationStore retained) return null;
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
            using var source = await observations.OpenForScanAsync(root, relativePath,
                (dispatch, token) => AdmitAccessRepairAsync(root, request, plan, dispatch, token), ct).ConfigureAwait(false);
            if (source.Observation.ByteLength != prior.ByteLength ||
                source.Observation.ByteLength > root.MaximumFileBytes ||
                source.Observation.LastWriteTicks != prior.FileLastWriteAtUtc!.Value.UtcDateTime.ToFileTimeUtc()) return null;
            await using var blob = await retained.OpenObservationAsync(artifact.StoreRelativePath, artifact.ContentSha256,
                artifact.ByteLength, ct).ConfigureAwait(false);
            SourceRevisionId? result = null;
            await _journal.ExecuteAsync(async db =>
            {
                if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked)
                    await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
                var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                SqlSourceIncrementalState.RequirePlan(state, plan);
                if (state.PolicyFingerprint != SqlRepositoryWorkRecovery.Policy(current)) return;
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
                entry.RelativePath = relativePath;
                BindPhysicalInventory(entry, source.Observation);
                entry.SourceRevisionId = prior.Id; entry.SourceArtifactId = artifact.Id; entry.BaselineEpoch = plan.BaselineEpoch;
                // A current metadata fact, never a new ingestion/hash/coverage receipt.
                // Completion acknowledges only the frozen path/generation, so a
                // later event remains pending even when its metadata is unchanged.
                entry.ObservationJson = JsonSerializer.Serialize(new CachedObservation(4, source.Observation, blob.Observation,
                    false, state.RecoveryGeneration, MetadataOnly: true));
                result = new(prior.Id);
            }, ct, () => { source.ValidateCurrent(); blob.ValidateCurrent(); }).ConfigureAwait(false);
            return result;
        }
        catch (IOException error) when (error is not SourceContentUnavailableException) { return null; }
        catch (UnauthorizedAccessException error) when (error is not SourceAccessDeniedException) { return null; }
    }
}
