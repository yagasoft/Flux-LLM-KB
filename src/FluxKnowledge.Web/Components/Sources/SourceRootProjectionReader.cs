using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Sources;

public interface ISourceRootProjectionReader
{
    ValueTask<IReadOnlyList<SourceRootListProjection>> ReadRootsAsync(CancellationToken cancellationToken);
    ValueTask<SourceRootDetailProjection?> ReadRootAsync(Guid rootId, CancellationToken cancellationToken);
    ValueTask<SourceRootPreview> PreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken);
}

/// <summary>Reads SQL-authoritative source state and previews only through the admitted local policy.</summary>
public sealed class SourceRootProjectionReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    ISourceRootPathPolicy pathPolicy,
    ISourceFileEnumerator enumerator,
    ILocalSourceCapabilityHandlerRegistry handlers) : ISourceRootProjectionReader
{
    public async ValueTask<IReadOnlyList<SourceRootListProjection>> ReadRootsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var roots = await context.SourceRootConfigurations.AsNoTracking()
            .OrderBy(root => root.DisplayName).ThenBy(root => root.CanonicalPath)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (roots.Count == 0)
        {
            return [];
        }

        var rootIds = roots.Select(root => root.Id).ToArray();
        var requests = await context.SourceScanRequests.AsNoTracking()
            .Where(request => rootIds.Contains(request.SourceRootId))
            .OrderByDescending(request => request.RequestedAtUtc).ThenByDescending(request => request.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var latestByRoot = requests
            .GroupBy(request => request.SourceRootId)
            .ToDictionary(group => group.Key, group => group.First());
        var summaries = await ReadStateSummariesAsync(context, rootIds, cancellationToken).ConfigureAwait(false);

        return roots.Select(root => ToListProjection(root, latestByRoot.GetValueOrDefault(root.Id), summaries.GetValueOrDefault(root.Id) ?? SourceStateSummary.Empty)).ToArray();
    }

    public async ValueTask<SourceRootDetailProjection?> ReadRootAsync(Guid rootId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var root = await context.SourceRootConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == rootId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var request = await context.SourceScanRequests.AsNoTracking()
            .Where(value => value.SourceRootId == rootId)
            .OrderByDescending(value => value.RequestedAtUtc).ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var summary = (await ReadStateSummariesAsync(context, [rootId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(rootId)
            ?? SourceStateSummary.Empty;
        var activities = await (
                from activity in context.SourceActivities.AsNoTracking()
                join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                join artifactValue in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifactValue.SourceRevisionId into artifactValues
                from artifact in artifactValues.DefaultIfEmpty()
                where revision.SourceRootId == rootId
                select new SourceActivityRow(
                    activity.Id,
                    activity.SourceRevisionId,
                    activity.ActivityKind,
                    activity.ExecutionClass,
                    activity.ProcessorVersion,
                    activity.InputFingerprint,
                    activity.State,
                    activity.Reason,
                    activity.RequiredCapability,
                    activity.ResultingPipelineRecordId,
                    revision.SuppressedAtUtc,
                    revision.Classification,
                    revision.ContentSha256,
                    revision.ByteLength,
                    artifact == null ? null : artifact.ContentSha256,
                    artifact == null ? null : artifact.ByteLength,
                    artifact == null ? null : artifact.StoreRelativePath))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var reasons = activities
            .Where(activity => activity.State == (int)SourceActivityState.DeferredUnsupported ||
                activity.State == (int)SourceActivityState.DeferredPolicy ||
                activity.State == (int)SourceActivityState.FailedTerminal)
            .GroupBy(activity => new
            {
                State = ((SourceActivityState)activity.State).ToString(),
                Reason = string.IsNullOrWhiteSpace(activity.Reason) ? "No reason recorded." : activity.Reason
            })
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.State).ThenBy(group => group.Key.Reason)
            .Take(20)
            .Select(group => new SourceActivityReasonProjection(group.Key.State, group.Key.Reason, group.Count()))
            .ToArray();
        var requiredCapabilities = activities
            .Where(activity => activity.State == (int)SourceActivityState.DeferredUnsupported &&
                !string.IsNullOrWhiteSpace(activity.RequiredCapability))
            .Select(activity => activity.RequiredCapability!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var runnableCapabilities = requiredCapabilities.Length == 0
            ? []
            : await context.SourceCapabilities.AsNoTracking()
                .Where(capability => capability.IsRunnable && capability.ExecutionClass == (int)ExecutionClass.InProcess)
                .Select(capability => new SourceCapabilityRow(
                    capability.Id, capability.ProcessorKind, capability.ProcessorVersion, capability.ProcessorFingerprint,
                    capability.AcceptedClassificationsJson, capability.OutputContract))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        var replayActivities = activities
            .Where(activity => activity.State == (int)SourceActivityState.DeferredUnsupported &&
                activity.ExecutionClass == (int)ExecutionClass.DeferredCapability &&
                activity.ActivityKind == (int)SourceActivityKind.TextExtraction && activity.RequiredCapability is not null &&
                activity.ResultingPipelineRecordId is null && activity.SuppressedAtUtc is null &&
                string.Equals(activity.InputFingerprint, activity.ContentSha256, StringComparison.Ordinal) &&
                string.Equals(activity.Classification, "AcceptedUtf8Text", StringComparison.Ordinal) &&
                activity.ByteLength is >= 0 and <= 16L * 1024 * 1024 &&
                activity.ArtifactByteLength == activity.ByteLength &&
                string.Equals(activity.ArtifactContentSha256, activity.ContentSha256, StringComparison.Ordinal) &&
                string.Equals(activity.ArtifactStoreRelativePath, Path.Combine("sha256", activity.ContentSha256[..2], $"{activity.ContentSha256}.bin"), StringComparison.OrdinalIgnoreCase))
            .Select(activity => new
            {
                Activity = activity,
                Capabilities = runnableCapabilities.Where(capability =>
                    string.Equals(capability.ProcessorKind, activity.RequiredCapability, StringComparison.Ordinal) &&
                    string.Equals(capability.ProcessorVersion, activity.ProcessorVersion, StringComparison.Ordinal) &&
                    string.Equals(capability.AcceptedClassificationsJson, "[\"text/plain\"]", StringComparison.Ordinal) &&
                    string.Equals(capability.OutputContract, "pipeline:extract-utf8", StringComparison.Ordinal) &&
                    handlers.TryResolve(capability.Id, out var handler) &&
                    LocalSourceCapabilityHandlerRegistry.SameDescriptor(handler, new SourceCapabilityDescriptor(
                        capability.Id, capability.ProcessorKind, capability.ProcessorVersion, ExecutionClass.InProcess,
                        capability.ProcessorFingerprint, SourceActivityKind.TextExtraction, "AcceptedUtf8Text", capability.OutputContract))).ToArray()
            })
            .Where(value => value.Capabilities.Length == 1)
            .Select(value => new DeferredContentReplayRequest(
                value.Activity.Id,
                ActivityIdempotencyKey(value.Activity),
                value.Activity.RequiredCapability!,
                value.Capabilities[0].Id,
                value.Capabilities[0].ProcessorVersion,
                value.Capabilities[0].ProcessorFingerprint))
            .ToArray();

        return new SourceRootDetailProjection(
            root.Id,
            root.DisplayName,
            root.CanonicalPath,
            ((SourceRootState)root.State).ToString(),
            request is null ? "No scan requested" : ((SourceScanRequestState)request.State).ToString(),
            root.LastScanCompletedAtUtc,
            request?.DiscoveredFileCount ?? 0,
            summary.Indexed,
            summary.Deferred,
            summary.Blocked,
            summary.Error + (request?.ErrorFileCount ?? 0),
            reasons,
            replayActivities);
    }

    public async ValueTask<SourceRootPreview> PreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var request = ToCreateRequest(draft);
        var validation = pathPolicy.ValidateAndCanonicalise(request);
        var sourceRoot = SourceRootConfiguration.Create(
            validation.CanonicalPath,
            request.DisplayName,
            request.Recursive,
            followLinks: false,
            request.MaximumFileBytes,
            request.IncludePatterns,
            request.ExcludePatterns,
            ["text/plain"],
            TimeSpan.FromMinutes(15));
        var matched = 0;
        var planned = 0;
        var deferred = 0;
        var blocked = 0;
        var reasons = new List<string>();
        await foreach (var file in enumerator.EnumerateAsync(sourceRoot, cancellationToken).ConfigureAwait(false))
        {
            matched++;
            if (file.Classification.IsAccepted)
            {
                planned++;
            }
            else if (file.Classification.Classification == SourceClassification.DeferredCapability)
            {
                deferred++;
            }
            else
            {
                blocked++;
            }

            if (!string.IsNullOrWhiteSpace(file.Classification.Reason))
            {
                reasons.Add(file.Classification.Reason);
            }
        }

        var evidence = enumerator.LastEvidence;
        reasons.AddRange(evidence.Select(item => item.Detail));
        return new SourceRootPreview(
            validation.CanonicalPath,
            matched,
            planned,
            deferred,
            blocked,
            evidence.Count(item => string.Equals(item.Kind, "permission", StringComparison.OrdinalIgnoreCase)),
            request.IncludePatterns.ToArray(),
            request.ExcludePatterns.ToArray(),
            reasons.Distinct(StringComparer.Ordinal).Take(20).ToArray());
    }

    private static SourceRootListProjection ToListProjection(
        Infrastructure.SqlServer.Persistence.Entities.SourceRootConfigurationEntity root,
        Infrastructure.SqlServer.Persistence.Entities.SourceScanRequestEntity? request,
        SourceStateSummary summary) =>
        new(
            root.Id,
            root.DisplayName,
            root.CanonicalPath,
            ((SourceRootState)root.State).ToString(),
            root.LastScanCompletedAtUtc,
            summary.Indexed,
            summary.Deferred,
            summary.Blocked,
            summary.Error + (request?.ErrorFileCount ?? 0));

    private static async ValueTask<IReadOnlyDictionary<Guid, SourceStateSummary>> ReadStateSummariesAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> rootIds,
        CancellationToken cancellationToken)
    {
        if (rootIds.Count == 0)
        {
            return new Dictionary<Guid, SourceStateSummary>();
        }

        var rows = await (
            from activity in context.SourceActivities.AsNoTracking()
            join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
            where rootIds.Contains(revision.SourceRootId) && revision.SuppressedAtUtc == null
            select new { revision.SourceRootId, activity.SourceRevisionId, activity.State, activity.ResultingPipelineRecordId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(row => row.SourceRootId).ToDictionary(
            group => group.Key,
            group =>
            {
                var revisions = group.GroupBy(row => row.SourceRevisionId).Select(revision => revision.ToArray()).ToArray();
                return new SourceStateSummary(
                    revisions.Count(revision => revision.Any(row => row.State == (int)SourceActivityState.Completed && row.ResultingPipelineRecordId != null)),
                    revisions.Count(revision => revision.Any(row => row.State == (int)SourceActivityState.DeferredUnsupported && row.ResultingPipelineRecordId == null)),
                    revisions.Count(revision => revision.Any(row => row.State == (int)SourceActivityState.DeferredPolicy)),
                    revisions.Count(revision => revision.Any(row => row.State == (int)SourceActivityState.FailedTerminal)));
            });
    }

    private static SourceRootCreateRequest ToCreateRequest(SourceRootDraft draft) =>
        new(
            draft.FullPath,
            string.IsNullOrWhiteSpace(draft.DisplayName) ? "Local source root" : draft.DisplayName,
            draft.Recursive,
            draft.IncludePatterns,
            draft.ExcludePatterns,
            FollowLinks: false,
            draft.MaximumFileBytes,
            ["text/plain"],
            TimeSpan.FromMinutes(15),
            string.IsNullOrWhiteSpace(draft.RequestedBy) ? "local-operator" : draft.RequestedBy);

    private static string ActivityIdempotencyKey(SourceActivityRow activity) =>
        SourceActivity.Restore(
            new SourceActivityId(activity.Id),
            new SourceRevisionId(activity.SourceRevisionId),
            (SourceActivityKind)activity.ActivityKind,
            (ExecutionClass)activity.ExecutionClass,
            activity.ProcessorVersion,
            activity.InputFingerprint,
            activity.RequiredCapability,
            (SourceActivityState)activity.State,
            activity.Reason).IdempotencyKey;

    private sealed record SourceActivityRow(
        Guid Id,
        Guid SourceRevisionId,
        int ActivityKind,
        int ExecutionClass,
        string ProcessorVersion,
        string InputFingerprint,
        int State,
        string? Reason,
        string? RequiredCapability,
        Guid? ResultingPipelineRecordId,
        DateTimeOffset? SuppressedAtUtc,
        string Classification,
        string ContentSha256,
        long ByteLength,
        string? ArtifactContentSha256,
        long? ArtifactByteLength,
        string? ArtifactStoreRelativePath);

    private sealed record SourceCapabilityRow(
        Guid Id,
        string ProcessorKind,
        string ProcessorVersion,
        string ProcessorFingerprint,
        string AcceptedClassificationsJson,
        string OutputContract);

    private sealed record SourceStateSummary(int Indexed, int Deferred, int Blocked, int Error)
    {
        public static SourceStateSummary Empty { get; } = new(0, 0, 0, 0);
    }
}
