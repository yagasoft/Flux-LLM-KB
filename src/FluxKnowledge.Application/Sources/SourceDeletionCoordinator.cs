using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Sources;

/// <summary>Runs one fenced, durable source deletion without granting any new source work.</summary>
public sealed class SourceDeletionCoordinator(
    ISourceDeletionStore store,
    IIndexGenerationPublisher? generationPublisher = null,
    ISourceDeletionFileStore? fileStore = null,
    ISourceArtifactPublicationGate? artifactPublicationGate = null)
{
    public async ValueTask<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var progressed = false;
        for (var pass = 0; pass < 8; pass++)
        {
            var workItem = await store.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
            if (workItem is null)
            {
                return progressed;
            }

            try
            {
                if (string.Equals(workItem.Phase, "rebuild-index", StringComparison.Ordinal))
                {
                    IndexGenerationCandidateSnapshot? candidate = null;
                    if (generationPublisher is not null)
                    {
                        try
                        {
                            candidate = await generationPublisher.BuildAndPlaceAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                        }
                        catch (NoEligibleVectorsException)
                        {
                            // The SQL store proves the strict empty-catalogue case before it
                            // can complete the cutover without a physical generation.
                        }
                    }

                    var rebuilt = await store.PurgeAsync(workItem, candidate, cancellationToken).ConfigureAwait(false);
                    if (rebuilt.ContinueImmediately)
                    {
                        progressed = true;
                        continue;
                    }
                    return rebuilt.Completed;
                }

                if (string.Equals(workItem.Phase, "cleanup-files", StringComparison.Ordinal))
                {
                    var pending = await store.ReadPendingCleanupAsync(workItem, cancellationToken).ConfigureAwait(false);
                    if (pending.Count == 0)
                    {
                        var completed = await store.PurgeAsync(workItem, survivorGeneration: null, cancellationToken).ConfigureAwait(false);
                        return completed.Completed;
                    }
                    if (fileStore is null)
                    {
                        await store.FailAsync(workItem, "source-delete-file-store-unavailable", cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    var target = pending[0];
                    if (target.StorageKind == 1 && target.ContentSha256 is not null && artifactPublicationGate is null)
                    {
                        await store.FailAsync(workItem, "source-delete-artifact-publication-gate-unavailable", cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    await using var cleanupLease = target.StorageKind == 1
                        ? await artifactPublicationGate!.TryAcquireExclusiveAsync(target.ContentSha256!, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (target.StorageKind == 1 && cleanupLease is null)
                    {
                        // A concurrent publication owns the blob. Release this operation's
                        // lease through the durable pending-cleanup path so it can be retried
                        // immediately once that publication has committed its reference.
                        await store.PurgeAsync(workItem, survivorGeneration: null, cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    if (target.StorageKind == 1)
                    {
                        pending = await store.ReadPendingCleanupAsync(workItem, cancellationToken).ConfigureAwait(false);
                        if (pending.All(candidate => candidate.CleanupItemId != target.CleanupItemId))
                        {
                            progressed = true;
                            continue;
                        }
                    }

                    var result = await fileStore.DeleteAsync(target, cancellationToken).ConfigureAwait(false);
                    await store.RecordCleanupResultAsync(workItem, target.CleanupItemId, result, cancellationToken).ConfigureAwait(false);
                    if (!result.Completed)
                    {
                        return false;
                    }
                    progressed = true;
                    continue;
                }

                var outcome = await store.PurgeAsync(workItem, survivorGeneration: null, cancellationToken).ConfigureAwait(false);
                if (outcome.ContinueImmediately)
                {
                    progressed = true;
                    continue;
                }
                return outcome.Completed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await store.FailAsync(workItem, "source-delete-cleanup-failed", cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        return progressed;
    }
}
