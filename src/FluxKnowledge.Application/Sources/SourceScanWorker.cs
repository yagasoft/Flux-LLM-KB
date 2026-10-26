using System.Runtime.CompilerServices;
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
    RetainedTextActivityPlanner? retainedTextActivityPlanner = null,
    ISourceIncrementalCoordinator? incrementalCoordinator = null) : ISourceScanner
{
    public const string TextProcessorVersion = "phase-3a-v1";

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
        var retentionEvidence = new List<SourceEnumerationEvidence>();
        var incrementalPlan = incrementalCoordinator is null ? null : await incrementalCoordinator.PrepareAsync(sourceRoot, scanRequest, cancellationToken).ConfigureAwait(false);
        using var enumerationScope = incrementalPlan is not null && enumerator is IIncrementalSourceFileEnumerator scoped
            ? scoped.UseScope(new(incrementalPlan.IsBaseline ? null : incrementalPlan.Targets.Where(x => x.HasCaptureScope).Select(x => x.RelativePath).ToArray(), async (relative, ct) =>
            {
                var reused = await incrementalCoordinator!.TryReuseAsync(sourceRoot, scanRequest, incrementalPlan, relative, ct).ConfigureAwait(false);
                if (reused is null) return true;
                convergedRevisionIds.Add(reused); discovered++; return false;
            }) { MetadataEntriesPerSecond = incrementalPlan.IsReconciliation && !incrementalPlan.IsBaseline
                ? incrementalPlan.MetadataEntriesPerSecond > 0 ? incrementalPlan.MetadataEntriesPerSecond : 64 : 0 }) : null;
        await foreach (var (file, capture) in EnumerateFilesAsync(sourceRoot, scanRequest, incrementalPlan is not null, cancellationToken).ConfigureAwait(false))
        {
            SourceRevisionId revisionId;
            SourceRetentionConvergence? failedRetention = null;
            string? failureReason = null;
            ISourceArtifactPublicationLease? publicationLease = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                discovered++;
                try
                {
                    var metadata = new SourceArtifactMetadata(file.ContentSha256, "application/octet-stream", file.ByteLength);
                    var receipt = await (capture is null || sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked
                        ? artifactStore.PutFileAsync(file, metadata, cancellationToken)
                        : ((ICapturedSourceArtifactStore)artifactStore).PutCapturedFileAsync(capture, metadata, cancellationToken)).ConfigureAwait(false);
                    publicationLease = receipt.PublicationLease;
                    revisionId = await scanStore.ConvergeRevisionAndArtifactAsync(sourceRoot, file, receipt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failureReason = ArtifactFailureReason(exception);
                    // A source can become unavailable after discovery; this scan is not a complete inventory.
                    retentionEvidence.Add(new(exception is SourceContentUnavailableException ? "unavailable" : "retention",
                        file.RelativePath, failureReason));
                    failedRetention = await scanStore.ConvergeBlockedRevisionAsync(sourceRoot, file, failureReason, cancellationToken).ConfigureAwait(false);
                    revisionId = failedRetention.SourceRevisionId;
                }
                // Retention has already succeeded. A lost incremental epoch/lease or failed
                // observation commit must retry the scan, not create a blocked-artifact record.
                if (failedRetention is null && incrementalPlan is not null)
                    await incrementalCoordinator!.RecordCaptureAsync(sourceRoot, scanRequest, incrementalPlan, file, revisionId, capture, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (publicationLease is not null) await publicationLease.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (capture is not null) await capture.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (failedRetention is { IsRetentionBlocked: true })
            {
                convergedRevisionIds.Add(revisionId);
                var activity = await activityStore.FindOrCreateAsync(
                    new SourceActivityDraft(revisionId, SourceActivityKind.DocumentParsing,
                        ExecutionClass.DeferredCapability, TextProcessorVersion, file.ContentSha256,
                        "source-artifact-store", failureReason, SourceActivityState.DeferredPolicy), cancellationToken).ConfigureAwait(false);
                if (retainedTextActivityPlanner is not null)
                {
                    await retainedTextActivityPlanner.PlanAsync(activity, cancellationToken).ConfigureAwait(false);
                }
                blocked++;
                continue;
            }
            convergedRevisionIds.Add(revisionId);
            var acceptedByRootPolicy = file.Classification.IsAccepted &&
                (sourceRoot.AllowedClassifications.Count == 0 ||
                    sourceRoot.AllowedClassifications.Contains("text/plain", StringComparer.OrdinalIgnoreCase));
            if (acceptedByRootPolicy &&
                file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                await activityStore.FindOrCreateAsync(
                    new SourceActivityDraft(
                        revisionId,
                        SourceActivityKind.DocumentParsing,
                        ExecutionClass.DeferredCapability,
                        RetainedCsharpCodeProcessor.ProcessorVersion,
                        file.ContentSha256,
                        RetainedCsharpCodeProcessor.ProcessorKind,
                        "csharp-code-writer-not-ready",
                        SourceActivityState.DeferredUnsupported),
                    cancellationToken).ConfigureAwait(false);
                deferred++;
                if (!RepositorySourceTextPolicy.IsEnabled(sourceRoot, file.RelativePath)) continue;
            }
            if (acceptedByRootPolicy)
            {
                var activity = await activityStore.FindOrCreateAsync(
                    new SourceActivityDraft(
                        revisionId,
                        SourceActivityKind.TextExtraction,
                        ExecutionClass.InProcess,
                        RepositorySourceTextPolicy.IsEnabled(sourceRoot, file.RelativePath) ? RepositorySourceTextPolicy.ProcessorVersion : TextProcessorVersion,
                        file.ContentSha256,
                        RequiredCapability: null,
                        Reason: null,
                        DescriptorFingerprint: RepositorySourceTextPolicy.IsEnabled(sourceRoot, file.RelativePath) ? RepositorySourceTextPolicy.DescriptorFingerprint : null),
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

        IReadOnlyList<SourceEnumerationEvidence> evidence = [.. enumerator.LastEvidence, .. retentionEvidence];
        await scanStore.RecordEnumerationEvidenceAsync(scanRequest.Id, evidence, cancellationToken).ConfigureAwait(false);
        var completeInventory = incrementalPlan is null;
        if (incrementalPlan is not null)
        {
            completeInventory = await incrementalCoordinator!.CompleteAsync(sourceRoot, scanRequest, incrementalPlan,
                ((IIncrementalSourceFileEnumerator)enumerator).LastDirectories, evidence, convergedRevisionIds,
                (enumerator as IAuthoritativeSourceFileEnumerator)?.LastInventory, cancellationToken).ConfigureAwait(false);
        }
        else if (evidence.Count == 0)
        {
            if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked)
            {
                if (enumerator is IAuthoritativeSourceFileEnumerator authoritative && authoritative.LastInventory is { } inventory &&
                    await authoritative.ValidateInventoryAsync(sourceRoot, cancellationToken).ConfigureAwait(false))
                    await scanStore.SuppressUnseenAuthoritativelyAsync(sourceRoot, scanRequest, inventory, convergedRevisionIds, cancellationToken).ConfigureAwait(false);
            }
            else await scanStore.SuppressUnseenAsync(sourceRoot.Id, convergedRevisionIds, cancellationToken).ConfigureAwait(false);
        }
        return new SourceScanResult(sourceRoot.Id, scanRequest.Id, discovered, indexed, deferred, blocked)
        { IsCompleteInventory = completeInventory, CompletesReconciliation = incrementalPlan is { IsReconciliation: true } or { IsBaseline: true } };
    }

    private async IAsyncEnumerable<(SourceDiscoveredFile File, ISourceFileCapture? Capture)> EnumerateFilesAsync(
        SourceRootConfiguration root, SourceScanRequest request, bool incremental, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request.Lease is { } ownership && enumerator is ICapturingSourceFileEnumerator capturing &&
            (root.DiscoveryMode == SourceDiscoveryMode.Filesystem && artifactStore is ICapturedSourceArtifactStore ||
             root.DiscoveryMode == SourceDiscoveryMode.GitTracked && incremental))
        {
            await foreach (var capture in capturing.EnumerateCapturedAsync(root, new(request.Id, ownership), cancellationToken).ConfigureAwait(false))
                yield return (capture.Snapshot, capture);
        }
        else
        {
            await foreach (var file in enumerator.EnumerateAsync(root, cancellationToken).ConfigureAwait(false))
                yield return (request.Lease is { } lease ? file with { ScanOwnership = new(request.Id, lease) } : file, null);
        }
    }

    private static string ArtifactFailureReason(Exception exception) => exception switch
    {
        SourceContentUnavailableException => "source-content-not-available-locally",
        SourceAccessDeniedException denied => denied.ReasonCode,
        SourceSnapshotChangedException => "source-snapshot-changed",
        UnauthorizedAccessException => "artifact-access-denied",
        InvalidDataException => "artifact-integrity-failed",
        IOException => "artifact-io-failed",
        _ => "artifact-retention-failed"
    };
}
