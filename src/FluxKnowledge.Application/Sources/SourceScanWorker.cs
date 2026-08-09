using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Projects a single deterministic filesystem crawl into immutable source records.</summary>
public sealed class SourceScanWorker(
    ISourceFileEnumerator enumerator,
    ISourceScanStore scanStore,
    ISourceArtifactStore artifactStore,
    ISourceActivityStore activityStore,
    RetainedTextActivityPlanner? retainedTextActivityPlanner = null) : ISourceScanner
{
    private const string TextProcessorVersion = "phase-3a-v1";

    public async ValueTask<SourceScanResult> ScanAsync(
        SourceRootConfiguration sourceRoot,
        SourceScanRequest scanRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(scanRequest);
        if (!scanRequest.IsReleased)
        {
            throw new InvalidOperationException("A held source scan request cannot be scanned.");
        }

        var convergedRevisionIds = new HashSet<SourceRevisionId>();
        var discovered = 0;
        var indexed = 0;
        var deferred = 0;
        var blocked = 0;
        await foreach (var file in enumerator.EnumerateAsync(sourceRoot, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            discovered++;
            SourceRevisionId revisionId;
            try
            {
                var receipt = await artifactStore.PutFileAsync(
                    file,
                    new SourceArtifactMetadata(
                        file.ContentSha256,
                        "application/octet-stream",
                        file.ByteLength),
                    cancellationToken).ConfigureAwait(false);
                revisionId = await scanStore.ConvergeRevisionAndArtifactAsync(sourceRoot, file, receipt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var reason = ArtifactFailureReason(exception);
                var retention = await scanStore.ConvergeBlockedRevisionAsync(sourceRoot, file, reason, cancellationToken).ConfigureAwait(false);
                revisionId = retention.SourceRevisionId;
                convergedRevisionIds.Add(revisionId);
                if (retention.IsRetentionBlocked)
                {
                    var activity = await activityStore.FindOrCreateAsync(
                        new SourceActivityDraft(revisionId, SourceActivityKind.DocumentParsing,
                            ExecutionClass.DeferredCapability, TextProcessorVersion, file.ContentSha256,
                            "source-artifact-store", reason, SourceActivityState.DeferredPolicy), cancellationToken).ConfigureAwait(false);
                    if (retainedTextActivityPlanner is not null)
                    {
                        await retainedTextActivityPlanner.PlanAsync(activity, cancellationToken).ConfigureAwait(false);
                    }
                    blocked++;
                    continue;
                }
            }
            convergedRevisionIds.Add(revisionId);
            var acceptedByRootPolicy = file.Classification.IsAccepted &&
                (sourceRoot.AllowedClassifications.Count == 0 ||
                    sourceRoot.AllowedClassifications.Contains("text/plain", StringComparer.OrdinalIgnoreCase));
            if (acceptedByRootPolicy)
            {
                var activity = await activityStore.FindOrCreateAsync(
                    new SourceActivityDraft(
                        revisionId,
                        SourceActivityKind.TextExtraction,
                        ExecutionClass.InProcess,
                        TextProcessorVersion,
                        file.ContentSha256,
                        RequiredCapability: null,
                        Reason: null),
                    cancellationToken).ConfigureAwait(false);
                if (retainedTextActivityPlanner is not null)
                {
                    await retainedTextActivityPlanner.PlanAsync(activity, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var isCapability = file.Classification.Classification == SourceClassification.DeferredCapability;
                var reason = acceptedByRootPolicy
                    ? file.Classification.Reason
                    : file.Classification.IsAccepted
                        ? "The source root policy does not allow text/plain classification."
                        : file.Classification.Reason;
                var activity = await activityStore.FindOrCreateAsync(
                    new SourceActivityDraft(
                        revisionId,
                        SourceActivityKind.DocumentParsing,
                        isCapability ? ExecutionClass.DeferredCapability : ExecutionClass.DeferredCapability,
                        TextProcessorVersion,
                        file.ContentSha256,
                        isCapability ? "local-source-capability" : null,
                        reason ?? "Source was not accepted for text ingestion.",
                        isCapability ? SourceActivityState.DeferredUnsupported : SourceActivityState.DeferredPolicy),
                    cancellationToken).ConfigureAwait(false);
                if (retainedTextActivityPlanner is not null)
                {
                    await retainedTextActivityPlanner.PlanAsync(activity, cancellationToken).ConfigureAwait(false);
                }
                if (isCapability)
                {
                    deferred++;
                }
                else
                {
                    blocked++;
                }
            }
        }

        var evidence = enumerator.LastEvidence;
        await scanStore.RecordEnumerationEvidenceAsync(scanRequest.Id, evidence, cancellationToken).ConfigureAwait(false);
        if (evidence.Count == 0)
        {
            await scanStore.SuppressUnseenAsync(sourceRoot.Id, convergedRevisionIds, cancellationToken).ConfigureAwait(false);
        }
        return new SourceScanResult(sourceRoot.Id, scanRequest.Id, discovered, indexed, deferred, blocked);
    }

    private static string ArtifactFailureReason(Exception exception) => exception switch
    {
        SourceSnapshotChangedException => "source-snapshot-changed",
        UnauthorizedAccessException => "artifact-access-denied",
        InvalidDataException => "artifact-integrity-failed",
        IOException => "artifact-io-failed",
        _ => "artifact-retention-failed"
    };
}
