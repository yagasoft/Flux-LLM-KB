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
    ILogger<SourceDeletionCoordinator>? logger = null)
{
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
                    if (target.StorageKind == 2 && queryCleanupGate is null)
                    {
                        await store.FailAsync(workItem, "source-delete-query-cleanup-gate-unavailable", cancellationToken).ConfigureAwait(false);
                        return false;
                    }
                    await using var queryCleanupLease = target.StorageKind == 2
                        ? await queryCleanupGate!.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false) : null;
                    if (target.StorageKind == 2 && queryCleanupLease is null)
                    {
                        await store.DeferAsync(workItem, "source-delete-search-queries-active", cancellationToken).ConfigureAwait(false);
                        return false;
                    }
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
                        await store.DeferAsync(workItem, "source-delete-artifact-publication-active", cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    // Both kinds require a fresh authority/target read after acquiring
                    // their execution gate. The exact gate stays held through native IO.
                    pending = await store.ReadPendingCleanupAsync(workItem, cancellationToken).ConfigureAwait(false);
                    var current = pending.SingleOrDefault(candidate => candidate.CleanupItemId == target.CleanupItemId);
                    if (current is null) return progressed;
                    if (current.StorageKind != target.StorageKind || current.RelativePath != target.RelativePath ||
                        current.ContentSha256 != target.ContentSha256 || current.ByteLength != target.ByteLength)
                    {
                        await store.FailAsync(workItem, "source-delete-cleanup-target-changed", cancellationToken).ConfigureAwait(false);
                        return false;
                    }
                    var result = current.SharedReferenceExists
                        ? new SourceDeletionFileResult(true, "source-delete-shared-artifact-preserved")
                        : await fileStore.DeleteAsync(current, cancellationToken).ConfigureAwait(false);
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
}
