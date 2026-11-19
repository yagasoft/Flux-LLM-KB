using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Application.Sources;

/// <summary>Runs one fenced, durable source deletion without granting any new source work.</summary>
public sealed class SourceDeletionCoordinator(
    ISourceDeletionStore store,
    IIndexGenerationPublisher? generationPublisher = null,
    ISourceDeletionFileStore? fileStore = null,
    ISourceArtifactPublicationGate? artifactPublicationGate = null,
    IDerivedIndexRecoveryStore? queryCleanupGate = null,
    ILogger<SourceDeletionCoordinator>? logger = null,
    IIndexSourceDeletionMaintenance? indexMaintenance = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<SourceDeletionCoordinator> _logger = logger ?? NullLogger<SourceDeletionCoordinator>.Instance;

    public async ValueTask<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        var progressed = false;
        for (var pass = 0; pass < 8; pass++)
        {
            SourceDeletionWorkItem? workItem;
            try
            {
                workItem = await store.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Source deletion could not claim work; existing durable operations remain recoverable");
                return false;
            }
            if (workItem is null)
            {
                return progressed;
            }

            try
            {
                if (workItem.Phase == "cleanup-memberships" && indexMaintenance is not null &&
                    await indexMaintenance.AdvanceAsync(workItem, cancellationToken) is { } blocked)
                {
                    await store.DeferAsync(workItem, blocked, cancellationToken);
                    // This disposition follows committed native segment reclamation;
                    // another claimed unit must still finish its manifest and targets.
                    return progressed || blocked == "source-delete-segment-retirement-progressing";
                }
                if (string.Equals(workItem.Phase, "rebuild-index", StringComparison.Ordinal))
                {
                    IndexGenerationCandidateSnapshot? candidate = null;
                    if (generationPublisher is not null)
                    {
                        try
                        {
                            candidate = await generationPublisher.BuildAndPlaceAsync(Guid.Empty, cancellationToken).ConfigureAwait(false);
                        }
                        catch (NoEligibleVectorsException)
                        {
                            // The SQL store proves the strict empty-catalogue case before it
                            // can complete the cutover without a physical generation.
                        }
                    }

                    var rebuilt = await store.PurgeAsync(workItem, candidate, cancellationToken).ConfigureAwait(false);
                    if (candidate is not null && rebuilt.Phase == "rebuild-index" && generationPublisher is not null)
                        await generationPublisher.AbandonAsync(candidate, cancellationToken);
                    if (rebuilt.ContinueImmediately)
                    {
                        progressed = true;
                        continue;
                    }
                    return rebuilt.Completed;
                }

                if (string.Equals(workItem.Phase, "cleanup-files", StringComparison.Ordinal))
                {
                    var pending = await store.ReadPendingCleanupAsync(workItem, cancellationToken,
                        ISourceDeletionStore.FileCleanupBatchSize).ConfigureAwait(false);
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

                    if (!await RunFileBatchAsync(workItem, pending, fileStore, cancellationToken).ConfigureAwait(false))
                        return false;
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
            catch (Exception exception)
            {
                var reason = exception switch
                {
                    SourceDeletionFailureException failure => failure.ReasonCode,
                    TimeoutException => "source-delete-timeout",
                    IOException => "source-delete-io-failed",
                    _ => "source-delete-cleanup-failed"
                };
                _logger.LogError(exception,
                    "Source deletion {OperationId} failed in {Phase} under lease {LeaseId}: {ReasonCode}",
                    workItem.OperationId, workItem.Phase, workItem.LeaseId, reason);
                try
                {
                    await store.FailAsync(workItem, reason, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception recordingException)
                {
                    // Retain the original diagnostic above. The durable lease/phase is
                    // recoverable even when SQL cannot record attention right now.
                    _logger.LogError(recordingException,
                        "Source deletion {OperationId} could not record {ReasonCode}; its durable phase remains recoverable",
                        workItem.OperationId, reason);
                }
                return false;
            }
        }

        return progressed;
    }

    private async ValueTask<bool> RunFileBatchAsync(SourceDeletionWorkItem work,
        IReadOnlyList<SourceDeletionFileTarget> pending, ISourceDeletionFileStore files, CancellationToken cancellationToken)
    {
        var storageKind = pending[0].StorageKind;
        if (storageKind == 2)
        {
            if (queryCleanupGate is null)
            {
                await store.FailAsync(work, "source-delete-query-cleanup-gate-unavailable", cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using var queryLease = await queryCleanupGate.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            if (queryLease is null)
            {
                await store.DeferAsync(work, "source-delete-search-queries-active", cancellationToken).ConfigureAwait(false);
                return false;
            }

            var count = pending.TakeWhile(target => target.StorageKind == 2).Count();
            return await DeleteGatedTargetsAsync(work, pending, count, files, (_, ct) => queryLease.IsHeldAsync(ct),
                cancellationToken).ConfigureAwait(false);
        }

        if (storageKind != 1 || pending[0].ContentSha256 is null)
        {
            var reason = storageKind == 1 ? "source-delete-artifact-path-invalid" : "source-delete-storage-kind-invalid";
            await store.FailAsync(work, reason, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (artifactPublicationGate is null)
        {
            await store.FailAsync(work, "source-delete-artifact-publication-gate-unavailable", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var hashes = pending.TakeWhile(target => target.StorageKind == 1 && target.ContentSha256 is not null)
            .Select(target => target.ContentSha256!).ToArray();
        await using var artifactLease = await artifactPublicationGate.TryAcquireExclusiveBatchAsync(hashes, cancellationToken).ConfigureAwait(false);
        if (artifactLease is null)
        {
            await store.DeferAsync(work, "source-delete-artifact-publication-active", cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await DeleteGatedTargetsAsync(work, pending, artifactLease.AcquiredCount, files,
            (target, ct) => artifactLease.IsHeldAsync(target.ContentSha256!, ct), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> DeleteGatedTargetsAsync(SourceDeletionWorkItem work,
        IReadOnlyList<SourceDeletionFileTarget> pending, int count, ISourceDeletionFileStore files,
        Func<SourceDeletionFileTarget, CancellationToken, ValueTask<bool>> isGateHeld, CancellationToken cancellationToken)
    {
        // Both kinds require a fresh authority and target read under their execution
        // gates. The exact gates stay held through native IO and the receipt commit.
        var current = await store.ReadPendingCleanupAsync(work, cancellationToken, count).ConfigureAwait(false);
        if (current.Count != count) return false;
        for (var index = 0; index < count; index++)
        {
            if (current[index].MatchesBinding(pending[index])) continue;
            await store.FailAsync(work, "source-delete-cleanup-target-changed", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var outcomes = new List<SourceDeletionFileOutcome>(count);
        var started = _timeProvider.GetTimestamp();
        foreach (var target in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Give queries a chance to acquire the global gate between slow legacy
            // directories. Once started, an individual deletion keeps its gate.
            if (target.StorageKind == 2 && outcomes.Count > 0 &&
                _timeProvider.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) break;
            if (!await isGateHeld(target, cancellationToken).ConfigureAwait(false)) break;
            if (work.LeaseExpiresAtUtc is not { } expires || expires <= _timeProvider.GetUtcNow())
            {
                if (outcomes.Count == 0) return false;
                break;
            }

            var result = target.SharedReferenceExists
                ? new SourceDeletionFileResult(true, "source-delete-shared-artifact-preserved")
                : await files.DeleteAsync(target, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(target, result));
            if (!result.Completed) break;
        }

        if (outcomes.Count == 0)
        {
            await store.DeferAsync(work, "source-delete-cleanup-gate-lost", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // An in-flight delete can finish under an expired work lease. Report the
        // physical progress to the worker, while SQL refuses the stale receipt.
        await store.RecordCleanupResultsAsync(work, outcomes, cancellationToken).ConfigureAwait(false);
        return outcomes[^1].Result.Completed;
    }
}
