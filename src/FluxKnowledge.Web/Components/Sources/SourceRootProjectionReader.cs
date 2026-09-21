using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
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
        var deletions = await context.SourceDeletionOperations.AsNoTracking()
            .Where(operation => rootIds.Contains(operation.SourceRootId))
            .ToDictionaryAsync(operation => operation.SourceRootId, cancellationToken)
            .ConfigureAwait(false);

        return roots.Select(root =>
        {
            var operation = deletions.GetValueOrDefault(root.Id);
            return ToListProjection(root, latestByRoot.GetValueOrDefault(root.Id), summaries.GetValueOrDefault(root.Id) ?? SourceStateSummary.Empty) with
            {
                DeletionPhase = operation?.Phase,
                DeletionReason = operation?.ReasonCode
            };
        }).ToArray();
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
        var deletion = await context.SourceDeletionOperations.AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.SourceRootId == rootId, cancellationToken)
            .ConfigureAwait(false);
        var revisions = await context.SourceRevisions.AsNoTracking()
            .Where(revision => revision.SourceRootId == rootId && revision.SuppressedAtUtc == null)
            .Select(revision => new SourceRevisionRow(
                revision.Id,
                revision.CanonicalPath,
                revision.Classification,
                revision.Extension,
                revision.OriginKind))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var publications = await (
                from publication in context.DocumentPublications.AsNoTracking()
                join owner in context.SourceRevisions.AsNoTracking() on publication.OwnerSourceRevisionId equals owner.Id
                join input in context.SourceRevisions.AsNoTracking() on publication.DocumentInputSourceRevisionId equals input.Id
                join branch in context.SourceProcessorBranches.AsNoTracking() on publication.SourceProcessorBranchId equals branch.Id
                where owner.SourceRootId == rootId && owner.SuppressedAtUtc == null
                select new DocumentPublicationRow(
                    publication.OwnerSourceRevisionId,
                    publication.PipelineRecordId,
                    input.OriginKind,
                    branch.Id,
                    branch.CreatedAtUtc))
            .ToDictionaryAsync(publication => publication.OwnerSourceRevisionId, cancellationToken)
            .ConfigureAwait(false);
        var logicalStates = await ReadLogicalOwnerStatesAsync(context, [rootId], cancellationToken).ConfigureAwait(false);
        var activities = await (
                from activity in context.SourceActivities.AsNoTracking()
                join revision in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                join artifactValue in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifactValue.SourceRevisionId into artifactValues
                from artifact in artifactValues.DefaultIfEmpty()
                where revision.SourceRootId == rootId && revision.SuppressedAtUtc == null
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
                    revision.Extension,
                    revision.OriginKind,
                    revision.ContentSha256,
                    revision.ByteLength,
                    artifact == null ? null : artifact.ContentSha256,
                    artifact == null ? null : artifact.ByteLength,
                    artifact == null ? null : artifact.StoreRelativePath))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var terminalBlockedBranches = await (
                from branch in context.SourceProcessorBranches.AsNoTracking()
                join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
                where revision.SourceRootId == rootId && revision.SuppressedAtUtc == null &&
                      branch.State == (int)RetainedProcessorBranchState.Blocked
                select new TerminalProcessorBranchRow(branch.Id, branch.SourceRevisionId, branch.LeaseGeneration))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var terminalBlockedBranchIds = terminalBlockedBranches.Select(branch => branch.Id).ToArray();
        var terminalAttempts = terminalBlockedBranchIds.Length == 0
            ? []
            : await context.SourceProcessorAttempts.AsNoTracking()
                .Where(attempt => terminalBlockedBranchIds.Contains(attempt.BranchId))
                .Select(attempt => new SourceProcessorAttemptRow(
                    attempt.BranchId,
                    attempt.LeaseGeneration,
                    attempt.StartedAtUtc,
                    attempt.FinishedAtUtc,
                    attempt.OutcomeCode))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var terminalBlockedReasons = terminalBlockedBranches
            .Where(branch => !publications.ContainsKey(branch.SourceRevisionId))
            .Select(branch => new SourceActivityReasonProjection(
                "Blocked",
                terminalAttempts
                    .Where(attempt => attempt.BranchId == branch.Id)
                    .OrderByDescending(attempt => attempt.LeaseGeneration)
                    .ThenByDescending(attempt => attempt.FinishedAtUtc)
                    .ThenByDescending(attempt => attempt.StartedAtUtc)
                    .Select(attempt => attempt.OutcomeCode)
                    .FirstOrDefault(outcome => !string.IsNullOrWhiteSpace(outcome)) ?? "No reason recorded.",
                1));
        var reasons = activities
            .Where(activity => activity.OriginKind is not (2 or 3) && !publications.ContainsKey(activity.SourceRevisionId))
            .Where(activity => activity.State == (int)SourceActivityState.DeferredUnsupported ||
                activity.State == (int)SourceActivityState.DeferredPolicy ||
                activity.State == (int)SourceActivityState.FailedTerminal)
            .Select(activity => new SourceActivityReasonProjection(
                ((SourceActivityState)activity.State).ToString(),
                DisplayReason(activity),
                1))
            .Concat(terminalBlockedReasons)
            .GroupBy(activity => new
            {
                activity.State,
                activity.Reason
            })
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.State).ThenBy(group => group.Key.Reason)
            .Take(20)
            .Select(group => new SourceActivityReasonProjection(group.Key.State, group.Key.Reason, group.Count()))
            .ToArray();
        var requiredCapabilities = activities
            .Where(activity => activity.OriginKind is not (2 or 3) && !publications.ContainsKey(activity.SourceRevisionId))
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
            .Where(activity => activity.OriginKind is not (2 or 3) && !publications.ContainsKey(activity.SourceRevisionId))
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
        var files = ProjectFiles(root.CanonicalPath, revisions, activities, terminalBlockedBranches, terminalAttempts, publications, logicalStates);

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
            replayActivities)
        {
            Files = files,
            DeletionPhase = deletion?.Phase,
            DeletionReason = deletion?.ReasonCode
        };
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
            where rootIds.Contains(revision.SourceRootId) && revision.SuppressedAtUtc == null && revision.OriginKind != 2 && revision.OriginKind != 3
            join branch in context.SourceProcessorBranches.AsNoTracking() on activity.Id equals branch.SourceActivityId into branches
            from branch in branches.DefaultIfEmpty()
            join publicationValue in context.DocumentPublications.AsNoTracking() on revision.Id equals publicationValue.OwnerSourceRevisionId into publicationValues
            from publication in publicationValues.DefaultIfEmpty()
            select new SourceStateRow(
                revision.SourceRootId,
                activity.SourceRevisionId,
                activity.State,
                activity.ResultingPipelineRecordId,
                branch == null ? null : branch.State,
                publication == null ? null : publication.PipelineRecordId,
                publication == null ? null : context.SourceRevisions
                    .Where(input => input.Id == publication.DocumentInputSourceRevisionId)
                    .Select(input => (int?)input.OriginKind).Single(),
                false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var logicalStates = await ReadLogicalOwnerStatesAsync(context, rootIds, cancellationToken).ConfigureAwait(false);
        foreach (var state in logicalStates.Values)
        {
            rows.Add(new SourceStateRow(
                state.SourceRootId,
                state.OwnerSourceRevisionId,
                state.Status == "Failed" ? (int)SourceActivityState.FailedTerminal :
                    state.Status == "Blocked" ? (int)SourceActivityState.DeferredPolicy :
                    state.Status == "Pending" ? (int)SourceActivityState.Pending : (int)SourceActivityState.Completed,
                null,
                null,
                null,
                null,
                true));
        }
        return rows.GroupBy(row => row.SourceRootId).ToDictionary(
            group => group.Key,
            group =>
            {
                var revisions = group.GroupBy(row => row.SourceRevisionId)
                    .Select(revision => ClassifyRevision(revision.ToArray()))
                    .ToArray();
                return new SourceStateSummary(
                    revisions.Count(state => state == SourceRevisionProjectionState.Indexed),
                    revisions.Count(state => state == SourceRevisionProjectionState.Deferred),
                    revisions.Count(state => state == SourceRevisionProjectionState.Blocked),
                    revisions.Count(state => state == SourceRevisionProjectionState.Error));
            });
    }

    private static SourceRevisionProjectionState ClassifyRevision(IReadOnlyCollection<SourceStateRow> rows)
    {
        if (rows.Any(row => row.OverridesPublication && row.State == (int)SourceActivityState.FailedTerminal))
            return SourceRevisionProjectionState.Error;
        if (rows.Any(row => row.OverridesPublication && row.State == (int)SourceActivityState.DeferredPolicy))
            return SourceRevisionProjectionState.Blocked;
        if (rows.Any(row => row.OverridesPublication && row.State == (int)SourceActivityState.DeferredUnsupported))
            return SourceRevisionProjectionState.Deferred;
        if (rows.Any(row => row.PublishedPipelineRecordId is not null && row.PublishedOriginKind == 2))
            return SourceRevisionProjectionState.Indexed;
        if (rows.Any(row => row.State == (int)SourceActivityState.FailedTerminal)) return SourceRevisionProjectionState.Error;
        if (rows.Any(row => row.ProcessorBranchState == (int)RetainedProcessorBranchState.Blocked ||
                            row.State == (int)SourceActivityState.DeferredPolicy)) return SourceRevisionProjectionState.Blocked;
        if (rows.Any(row => row.State == (int)SourceActivityState.DeferredUnsupported && row.ResultingPipelineRecordId is null))
            return SourceRevisionProjectionState.Deferred;
        if (rows.Any(row => row.State == (int)SourceActivityState.Completed && row.ResultingPipelineRecordId is not null))
            return SourceRevisionProjectionState.Indexed;
        return SourceRevisionProjectionState.Unresolved;
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

    private static IReadOnlyList<SourceFileProjection> ProjectFiles(
        string rootCanonicalPath,
        IReadOnlyList<SourceRevisionRow> revisions,
        IReadOnlyList<SourceActivityRow> activities,
        IReadOnlyList<TerminalProcessorBranchRow> terminalBlockedBranches,
        IReadOnlyList<SourceProcessorAttemptRow> terminalAttempts,
        IReadOnlyDictionary<Guid, DocumentPublicationRow> publications,
        IReadOnlyDictionary<Guid, LogicalOwnerStateRow> logicalStates) =>
        revisions.Where(revision => revision.OriginKind is not (2 or 3)).Select(revision =>
        {
            logicalStates.TryGetValue(revision.Id, out var logicalState);
            if (publications.TryGetValue(revision.Id, out var publication))
            {
                var publishedRelativePath = RelativePath(rootCanonicalPath, revision.CanonicalPath);
                if (logicalState is not null)
                {
                    return new SourceFileProjection(
                        Path.GetFileName(publishedRelativePath),
                        publishedRelativePath,
                        PublishedDocumentClassification(revision.Extension, revision.Classification),
                        logicalState.Status,
                        logicalState.Reason,
                        publication.PipelineRecordId);
                }
                return new SourceFileProjection(
                    Path.GetFileName(publishedRelativePath),
                    publishedRelativePath,
                    PublishedDocumentClassification(revision.Extension, revision.Classification),
                    publication.InputOriginKind == 3 ? "Metadata only" : "Indexed",
                    publication.InputOriginKind == 3 ? "media-metadata-only" : null,
                    publication.PipelineRecordId);
            }
            if (logicalState is not null)
            {
                var logicalRelativePath = RelativePath(rootCanonicalPath, revision.CanonicalPath);
                return new SourceFileProjection(
                    Path.GetFileName(logicalRelativePath),
                    logicalRelativePath,
                    revision.Classification,
                    logicalState.Status,
                    logicalState.Reason,
                    null);
            }
            var revisionActivities = activities.Where(activity => activity.SourceRevisionId == revision.Id).ToArray();
            var revisionBranches = terminalBlockedBranches.Where(branch => branch.SourceRevisionId == revision.Id).ToArray();
            var state = ClassifyRevision(revisionActivities, revisionBranches.Length > 0);
            var relativePath = RelativePath(rootCanonicalPath, revision.CanonicalPath);
            return new SourceFileProjection(
                Path.GetFileName(relativePath),
                relativePath,
                revision.Classification,
                DisplayStatus(state),
                DisplayFileReason(state, revisionActivities, revisionBranches, terminalAttempts),
                state == SourceRevisionProjectionState.Indexed
                    ? revisionActivities
                        .Where(activity => activity.State == (int)SourceActivityState.Completed && activity.ResultingPipelineRecordId is not null)
                        .Select(activity => activity.ResultingPipelineRecordId)
                        .FirstOrDefault()
                    : null);
        })
        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static async ValueTask<IReadOnlyDictionary<Guid, LogicalOwnerStateRow>> ReadLogicalOwnerStatesAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> rootIds,
        CancellationToken cancellationToken)
    {
        if (rootIds.Count == 0)
        {
            return new Dictionary<Guid, LogicalOwnerStateRow>();
        }

        var noContent = await (
                from branch in context.SourceProcessorBranches.AsNoTracking()
                join owner in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals owner.Id
                join ownerActivity in context.SourceActivities.AsNoTracking() on branch.SourceActivityId equals ownerActivity.Id
                join member in context.SourceProcessorBranchMembers.AsNoTracking() on branch.Id equals member.BranchId
                where rootIds.Contains(owner.SourceRootId) && owner.SuppressedAtUtc == null &&
                      branch.State == (int)RetainedProcessorBranchState.Completed && branch.CompletedMemberCount == 0 &&
                      branch.ProcessorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                      branch.ProcessorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint &&
                      ownerActivity.SourceRevisionId == owner.Id &&
                      EF.Functions.Collate(ownerActivity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                          EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                      EF.Functions.Collate(ownerActivity.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) ==
                          EF.Functions.Collate(branch.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) &&
                      EF.Functions.Collate(ownerActivity.DescriptorFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                          EF.Functions.Collate(branch.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                      member.Disposition == "skipped" && member.ReasonCode == "office-document-no-extractable-text" &&
                      context.SourceProcessorBranchMembers.Count(other => other.BranchId == branch.Id) == 1
                select new LogicalOwnerStateRow(
                    owner.SourceRootId, owner.Id, branch.Id, branch.CreatedAtUtc, 2,
                    "Completed", "office-document-no-extractable-text"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var childJobs = await (
                from branch in context.SourceProcessorBranches.AsNoTracking()
                join owner in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals owner.Id
                join member in context.SourceProcessorBranchMembers.AsNoTracking() on branch.Id equals member.BranchId
                join child in context.SourceRevisions.AsNoTracking() on member.ChildSourceRevisionId equals child.Id
                join childActivity in context.SourceActivities.AsNoTracking() on member.ChildSourceActivityId equals childActivity.Id
                join job in context.Jobs.AsNoTracking() on new
                {
                    Id = childActivity.ResultingPipelineRecordId!.Value,
                    Revision = childActivity.ResultingPipelineRecordRevision!.Value
                } equals new { Id = job.PipelineRecordId, Revision = job.SourceRevision }
                where rootIds.Contains(owner.SourceRootId) && owner.SuppressedAtUtc == null && child.SuppressedAtUtc == null &&
                      branch.State == (int)RetainedProcessorBranchState.Completed && member.Disposition == "completed" &&
                      child.ParentSourceRevisionId == owner.Id && child.OriginKind >= 2 && child.OriginKind <= 3 &&
                      childActivity.SourceRevisionId == child.Id && childActivity.ResultingPipelineRecordId != null &&
                      childActivity.ResultingPipelineRecordRevision != null
                select new LogicalChildJobRow(
                    owner.SourceRootId, owner.Id, branch.Id, branch.CreatedAtUtc,
                    child.OriginKind == 3 ? 1 : 2, job.PublicState, job.Reason))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var incompleteBranches = await (
                from branch in context.SourceProcessorBranches.AsNoTracking()
                join owner in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals owner.Id
                join ownerActivity in context.SourceActivities.AsNoTracking() on branch.SourceActivityId equals ownerActivity.Id
                where rootIds.Contains(owner.SourceRootId) && owner.SuppressedAtUtc == null &&
                      branch.State != (int)RetainedProcessorBranchState.Completed &&
                      ownerActivity.SourceRevisionId == owner.Id &&
                      EF.Functions.Collate(ownerActivity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                          EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                      EF.Functions.Collate(ownerActivity.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) ==
                          EF.Functions.Collate(branch.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) &&
                      EF.Functions.Collate(ownerActivity.DescriptorFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                          EF.Functions.Collate(branch.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
                      ((branch.ProcessorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion &&
                        branch.ProcessorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint) ||
                       (branch.ProcessorVersion == VisioDocumentInputProcessor.Capability.ProcessorVersion &&
                        branch.ProcessorFingerprint == VisioDocumentInputProcessor.Capability.ProcessorFingerprint) ||
                       (branch.ProcessorVersion == PdfDocumentProcessor.Capability.ProcessorVersion &&
                        branch.ProcessorFingerprint == PdfDocumentProcessor.Capability.ProcessorFingerprint) ||
                       (branch.ProcessorVersion == ImageDocumentInputProcessor.Capability.ProcessorVersion &&
                        branch.ProcessorFingerprint == ImageDocumentInputProcessor.Capability.ProcessorFingerprint) ||
                       (branch.ProcessorVersion == MediaMetadataRetainedProcessor.Capability.ProcessorVersion &&
                        branch.ProcessorFingerprint == MediaMetadataRetainedProcessor.Capability.ProcessorFingerprint))
                select new LogicalBranchExecutionRow(
                    owner.SourceRootId,
                    owner.Id,
                    branch.Id,
                    branch.CreatedAtUtc,
                    branch.ProcessorFingerprint == MediaMetadataRetainedProcessor.Capability.ProcessorFingerprint ? 1 : 2,
                    branch.State,
                    branch.LeaseGeneration))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var incompleteBranchIds = incompleteBranches.Select(value => value.BranchId).ToArray();
        var incompleteAttempts = incompleteBranchIds.Length == 0
            ? []
            : await context.SourceProcessorAttempts.AsNoTracking()
                .Where(attempt => incompleteBranchIds.Contains(attempt.BranchId))
                .Select(attempt => new SourceProcessorAttemptRow(
                    attempt.BranchId,
                    attempt.LeaseGeneration,
                    attempt.StartedAtUtc,
                    attempt.FinishedAtUtc,
                    attempt.OutcomeCode))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var incompleteStates = incompleteBranches.Select(branch =>
            new LogicalOwnerStateRow(
                branch.SourceRootId,
                branch.OwnerSourceRevisionId,
                branch.BranchId,
                branch.BranchCreatedAtUtc,
                branch.Priority,
                branch.State == (int)RetainedProcessorBranchState.Blocked ? "Blocked" : "Pending",
                branch.State == (int)RetainedProcessorBranchState.Blocked
                    ? incompleteAttempts
                        .Where(attempt => attempt.BranchId == branch.BranchId)
                        .OrderByDescending(attempt => attempt.LeaseGeneration)
                        .ThenByDescending(attempt => attempt.FinishedAtUtc)
                        .ThenByDescending(attempt => attempt.StartedAtUtc)
                        .Select(attempt => attempt.OutcomeCode)
                        .FirstOrDefault(outcome => !string.IsNullOrWhiteSpace(outcome)) ?? "processor-branch-blocked"
                    : null));

        var candidates = noContent.Concat(childJobs
                .GroupBy(value => new
                {
                    value.SourceRootId,
                    value.OwnerSourceRevisionId,
                    value.BranchId,
                    value.BranchCreatedAtUtc,
                    value.Priority
                })
                .Select(group => group.Any(value => value.JobState == (int)PublicJobState.Failed)
                    ? new LogicalOwnerStateRow(
                        group.Key.SourceRootId, group.Key.OwnerSourceRevisionId, group.Key.BranchId,
                        group.Key.BranchCreatedAtUtc, group.Key.Priority, "Failed",
                        group.Where(value => value.JobState == (int)PublicJobState.Failed)
                            .Select(value => value.Reason).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "pipeline-failed")
                    : group.Any(value => value.JobState != (int)PublicJobState.Completed)
                        ? new LogicalOwnerStateRow(
                            group.Key.SourceRootId, group.Key.OwnerSourceRevisionId, group.Key.BranchId,
                            group.Key.BranchCreatedAtUtc, group.Key.Priority, "Pending", null)
                        : null)
                .Where(static value => value is not null)
                .Select(static value => value!))
            .Concat(incompleteStates)
            .GroupBy(value => value.OwnerSourceRevisionId)
            .Select(group => group.OrderByDescending(value => value.Priority)
                .ThenByDescending(value => value.BranchCreatedAtUtc)
                .ThenByDescending(value => value.BranchId.ToString("N"), StringComparer.Ordinal)
                .First())
            .ToArray();

        var publicationBranches = await (
                from publication in context.DocumentPublications.AsNoTracking()
                join branch in context.SourceProcessorBranches.AsNoTracking() on publication.SourceProcessorBranchId equals branch.Id
                join input in context.SourceRevisions.AsNoTracking() on publication.DocumentInputSourceRevisionId equals input.Id
                where candidates.Select(value => value.OwnerSourceRevisionId).Contains(publication.OwnerSourceRevisionId)
                select new PublicationBranchRow(
                    publication.OwnerSourceRevisionId, branch.Id, branch.CreatedAtUtc, input.OriginKind == 3 ? 1 : 2))
            .ToDictionaryAsync(value => value.OwnerSourceRevisionId, cancellationToken).ConfigureAwait(false);

        return candidates
            .Where(candidate => !publicationBranches.TryGetValue(candidate.OwnerSourceRevisionId, out var publication) ||
                candidate.Priority > publication.Priority ||
                candidate.Priority == publication.Priority &&
                (candidate.BranchCreatedAtUtc > publication.BranchCreatedAtUtc ||
                 candidate.BranchCreatedAtUtc == publication.BranchCreatedAtUtc &&
                 string.CompareOrdinal(candidate.BranchId.ToString("N"), publication.BranchId.ToString("N")) > 0))
            .ToDictionary(value => value.OwnerSourceRevisionId);
    }

    private static string PublishedDocumentClassification(string extension, string fallback) => extension.ToLowerInvariant() switch
    {
        ".vsdx" => "VsdxDocumentContainer",
        ".pdf" => "PdfDocumentContainer",
        ".jpg" or ".jpeg" or ".png" => "ImageDocumentContainer",
        _ => fallback
    };

    private static SourceRevisionProjectionState ClassifyRevision(
        IReadOnlyCollection<SourceActivityRow> activities,
        bool hasBlockedProcessorBranch) =>
        ClassifyRevision(activities.Select(activity => new SourceStateRow(
            Guid.Empty,
            activity.SourceRevisionId,
            activity.State,
            activity.ResultingPipelineRecordId,
            hasBlockedProcessorBranch ? (int)RetainedProcessorBranchState.Blocked : null,
            null,
            null,
            false)).ToArray());

    private static string DisplayStatus(SourceRevisionProjectionState state) => state switch
    {
        SourceRevisionProjectionState.Indexed => "Indexed",
        SourceRevisionProjectionState.Deferred => "Deferred",
        SourceRevisionProjectionState.Blocked => "Blocked",
        SourceRevisionProjectionState.Error => "Failed",
        _ => "Pending"
    };

    private static string? DisplayFileReason(
        SourceRevisionProjectionState state,
        IReadOnlyList<SourceActivityRow> activities,
        IReadOnlyList<TerminalProcessorBranchRow> branches,
        IReadOnlyList<SourceProcessorAttemptRow> attempts) => state switch
    {
        SourceRevisionProjectionState.Error => activities
            .Where(activity => activity.State == (int)SourceActivityState.FailedTerminal)
            .Select(DisplayReason)
            .FirstOrDefault(),
        SourceRevisionProjectionState.Blocked => activities
            .Where(activity => activity.State == (int)SourceActivityState.DeferredPolicy)
            .Select(DisplayReason)
            .FirstOrDefault() ?? BranchReason(branches, attempts),
        SourceRevisionProjectionState.Deferred => activities
            .Where(activity => activity.State == (int)SourceActivityState.DeferredUnsupported && activity.ResultingPipelineRecordId is null)
            .Select(DisplayReason)
            .FirstOrDefault(),
        _ => null
    };

    private static string? BranchReason(
        IReadOnlyList<TerminalProcessorBranchRow> branches,
        IReadOnlyList<SourceProcessorAttemptRow> attempts) => branches
        .Select(branch => attempts
            .Where(attempt => attempt.BranchId == branch.Id)
            .OrderByDescending(attempt => attempt.LeaseGeneration)
            .ThenByDescending(attempt => attempt.FinishedAtUtc)
            .ThenByDescending(attempt => attempt.StartedAtUtc)
            .Select(attempt => attempt.OutcomeCode)
            .FirstOrDefault(outcome => !string.IsNullOrWhiteSpace(outcome)))
        .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));

    private static string RelativePath(string rootCanonicalPath, string canonicalPath)
    {
        var root = rootCanonicalPath.TrimEnd('\\', '/');
        if (canonicalPath.Length > root.Length &&
            canonicalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            canonicalPath[root.Length] is '\\' or '/')
        {
            return canonicalPath[(root.Length + 1)..];
        }

        return Path.GetFileName(canonicalPath);
    }

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

    private static string DisplayReason(SourceActivityRow activity) =>
        string.Equals(activity.Reason, "Binary signature requires a capability that is not registered.", StringComparison.Ordinal) &&
        string.Equals(activity.Extension, ".pdf", StringComparison.OrdinalIgnoreCase)
            ? "pdf-parser-unavailable"
            : string.IsNullOrWhiteSpace(activity.Reason) ? "No reason recorded." : activity.Reason;

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
        string Extension,
        int OriginKind,
        string ContentSha256,
        long ByteLength,
        string? ArtifactContentSha256,
        long? ArtifactByteLength,
        string? ArtifactStoreRelativePath);

    private sealed record SourceRevisionRow(Guid Id, string CanonicalPath, string Classification, string Extension, int OriginKind);

    private sealed record DocumentPublicationRow(
        Guid OwnerSourceRevisionId,
        Guid PipelineRecordId,
        int InputOriginKind,
        Guid BranchId,
        DateTimeOffset BranchCreatedAtUtc);

    private sealed record LogicalOwnerStateRow(
        Guid SourceRootId,
        Guid OwnerSourceRevisionId,
        Guid BranchId,
        DateTimeOffset BranchCreatedAtUtc,
        int Priority,
        string Status,
        string? Reason);

    private sealed record LogicalChildJobRow(
        Guid SourceRootId,
        Guid OwnerSourceRevisionId,
        Guid BranchId,
        DateTimeOffset BranchCreatedAtUtc,
        int Priority,
        int JobState,
        string? Reason);

    private sealed record LogicalBranchExecutionRow(
        Guid SourceRootId,
        Guid OwnerSourceRevisionId,
        Guid BranchId,
        DateTimeOffset BranchCreatedAtUtc,
        int Priority,
        int State,
        long LeaseGeneration);

    private sealed record PublicationBranchRow(
        Guid OwnerSourceRevisionId,
        Guid BranchId,
        DateTimeOffset BranchCreatedAtUtc,
        int Priority);

    private sealed record SourceCapabilityRow(
        Guid Id,
        string ProcessorKind,
        string ProcessorVersion,
        string ProcessorFingerprint,
        string AcceptedClassificationsJson,
        string OutputContract);

    private sealed record TerminalProcessorBranchRow(Guid Id, Guid SourceRevisionId, long LeaseGeneration);

    private sealed record SourceProcessorAttemptRow(
        Guid BranchId,
        long LeaseGeneration,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? FinishedAtUtc,
        string? OutcomeCode);

    private sealed record SourceStateRow(
        Guid SourceRootId,
        Guid SourceRevisionId,
        int State,
        Guid? ResultingPipelineRecordId,
        int? ProcessorBranchState,
        Guid? PublishedPipelineRecordId,
        int? PublishedOriginKind,
        bool OverridesPublication);

    private enum SourceRevisionProjectionState
    {
        Unresolved,
        Indexed,
        Deferred,
        Blocked,
        Error
    }

    private sealed record SourceStateSummary(int Indexed, int Deferred, int Blocked, int Error)
    {
        public static SourceStateSummary Empty { get; } = new(0, 0, 0, 0);
    }
}
