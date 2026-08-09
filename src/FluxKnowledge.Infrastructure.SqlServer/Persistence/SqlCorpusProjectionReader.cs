using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// SQL-authoritative catalogue projection.  It deliberately has no dependency on a
/// source reader: persisted source metadata and indexed artefacts are the only inputs.
/// </summary>
public sealed class SqlCorpusProjectionReader(IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : ICorpusProjectionReader
{
    private const int PreviewLimit = 8_192;

    public async ValueTask<CorpusPage> ReadPageAsync(CorpusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var candidates = await SearchCandidatesAsync(context, query.Filters.Search, cancellationToken).ConfigureAwait(false);
        var rows = BuildRows(context);
        var filters = query.Filters;

        if (!query.IncludeHistorical)
        {
            rows = rows.Where(row => !row.IsDeleted && (row.SourceRevisionId == null || row.SuppressedAtUtc == null));
        }

        if (candidates is not null)
        {
            rows = rows.Where(row => candidates.Contains(row.PipelineRecordId) ||
                                      row.SafeSourceIdentity.Contains(filters.Search!) ||
                                      (row.RelativePath != null && row.RelativePath.Contains(filters.Search!)));
        }

        if (filters.SourceKind is { } sourceKind)
            rows = rows.Where(row => row.SourceKind == sourceKind);
        if (filters.SourceRootId is { } sourceRootId)
            rows = rows.Where(row => row.SourceRootId == sourceRootId);
        if (filters.SourceClassification is { } classification)
            rows = rows.Where(row => row.SourceClassification == classification);
        if (filters.Folder is { } folder)
            rows = rows.Where(row => row.RelativePath != null && row.RelativePath.StartsWith(folder + "\\"));
        if (filters.PipelineStatus is { } pipelineStatus && Enum.TryParse<PipelineStage>(pipelineStatus, true, out var stage))
            rows = rows.Where(row => row.CurrentStage == (int)stage);
        if (filters.SourceActivityStatus is { } activityStatus)
            rows = ApplyActivityFilter(rows, activityStatus);
        if (filters.UpdatedFromUtc is { } from)
            rows = rows.Where(row => row.LastActivityAtUtc >= from);
        if (filters.UpdatedToUtc is { } to)
            rows = rows.Where(row => row.LastActivityAtUtc <= to);
        if (query.Cursor is { } cursor)
            rows = rows.Where(row => row.LastActivityAtUtc < cursor.LastActivityAtUtc ||
                                     (row.LastActivityAtUtc == cursor.LastActivityAtUtc && row.PipelineRecordId.CompareTo(cursor.PipelineRecordId) < 0));

        var pageRows = await rows.OrderByDescending(row => row.LastActivityAtUtc)
            .ThenByDescending(row => row.PipelineRecordId)
            .Take(query.PageSize + 1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var items = pageRows.Take(query.PageSize).Select(ToEntry).ToArray();
        var last = items.LastOrDefault();
        return new CorpusPage(items, pageRows.Length > query.PageSize && last is not null
            ? CorpusCursor.Create(last.LastActivityAtUtc, last.PipelineRecordId, query.CanonicalFilter)
            : null);
    }

    public async ValueTask<IReadOnlyList<CorpusFolder>> ReadFoldersAsync(Guid sourceRootId, string? folder, CancellationToken cancellationToken)
    {
        var normalisedFolder = CorpusFilters.NormaliseFolder(folder) ?? string.Empty;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // This is one parameterised SQL aggregate.  It intentionally never materialises
        // every revision under a root and never asks the operating system about folders.
        var folderRows = await context.Database.SqlQuery<FolderRow>($"""
            WITH [Root] AS (
                SELECT [CanonicalPath] FROM [SourceRootConfigurations] WHERE [Id] = {sourceRootId}
            ), [Rows] AS (
                SELECT [revision].[Id],
                       SUBSTRING([revision].[CanonicalPath], LEN([root].[CanonicalPath]) + 2, 4000) AS [RelativePath],
                       [latest].[State], [latest].[ResultingPipelineRecordId]
                FROM [SourceRevisions] AS [revision]
                CROSS JOIN [Root] AS [root]
                OUTER APPLY (
                    SELECT TOP (1) [activity].[State], [activity].[ResultingPipelineRecordId]
                    FROM [SourceActivities] AS [activity]
                    WHERE [activity].[SourceRevisionId] = [revision].[Id]
                    ORDER BY [activity].[UpdatedAtUtc] DESC, [activity].[Id] DESC
                ) AS [latest]
                WHERE [revision].[SourceRootId] = {sourceRootId}
                  AND [revision].[SuppressedAtUtc] IS NULL
                  AND LEFT([revision].[CanonicalPath], LEN([root].[CanonicalPath]) + 1) = [root].[CanonicalPath] + N'\'
            ), [Scoped] AS (
                SELECT *, CASE WHEN {normalisedFolder} = N'' THEN [RelativePath]
                     ELSE SUBSTRING([RelativePath], LEN({normalisedFolder}) + 2, 4000) END AS [ScopedPath]
                FROM [Rows]
                WHERE {normalisedFolder} = N'' OR LEFT([RelativePath], LEN({normalisedFolder}) + 1) = {normalisedFolder} + N'\'
            ), [Children] AS (
                SELECT *, CASE WHEN CHARINDEX(N'\', [ScopedPath]) > 0
                     THEN LEFT([ScopedPath], CHARINDEX(N'\', [ScopedPath]) - 1) END AS [Child]
                FROM [Scoped]
            )
            SELECT CASE WHEN {normalisedFolder} = N'' THEN [Child] ELSE {normalisedFolder} + N'\' + [Child] END AS [RelativePath],
                   SUM(CASE WHEN [State] IS NULL OR ([State] = {(int)SourceActivityState.Completed} AND [ResultingPipelineRecordId] IS NOT NULL) THEN 1 ELSE 0 END) AS [CurrentCount],
                   SUM(CASE WHEN [State] = {(int)SourceActivityState.DeferredUnsupported} THEN 1 ELSE 0 END) AS [DeferredCount],
                   SUM(CASE WHEN [State] = {(int)SourceActivityState.DeferredPolicy} THEN 1 ELSE 0 END) AS [BlockedCount],
                   SUM(CASE WHEN [State] = {(int)SourceActivityState.FailedTerminal} THEN 1 ELSE 0 END) AS [FailedCount]
            FROM [Children]
            WHERE [Child] IS NOT NULL
            GROUP BY [Child]
            ORDER BY [RelativePath]
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return folderRows.Select(row => new CorpusFolder(sourceRootId, row.RelativePath, row.CurrentCount, row.DeferredCount, row.BlockedCount, row.FailedCount)).ToArray();
    }

    public async ValueTask<CorpusEntryDetail?> ReadDetailAsync(Guid pipelineRecordId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await BuildRows(context).SingleOrDefaultAsync(value => value.PipelineRecordId == pipelineRecordId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null;

        var state = SourceActivityStatus(row.LatestActivityState, row.LatestActivityResultingPipelineRecordId, row.PipelineRecordId, row.SourceRevisionId);
        var canPreview = !row.IsDeleted && row.SourceRevisionId is not null && row.SuppressedAtUtc is null && state == "Indexed";
        var preview = canPreview
            ? await ReadPreviewAsync(context, pipelineRecordId, row.RecordRevision, cancellationToken).ConfigureAwait(false)
            : null;
        var jobRows = await context.Jobs.AsNoTracking().Where(job => job.PipelineRecordId == pipelineRecordId)
            .OrderByDescending(job => job.DueAtUtc).ThenByDescending(job => job.Id)
            .Select(job => new { job.PublicState, job.Stage })
            .Take(20).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var jobs = jobRows.Select(job => ((PublicJobState)job.PublicState).ToString() + ":" + ((PipelineStage)job.Stage).ToString()).ToArray();
        var eventRows = await context.AuditEvents.AsNoTracking().Where(e => e.PipelineRecordId == pipelineRecordId ||
                (row.SourceRevisionId != null && e.SourceRevisionId == row.SourceRevisionId))
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id).Select(e => new { e.Id, e.OccurredAtUtc, e.EventType, e.DetailsJson }).Take(20).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var activityRows = row.SourceRevisionId is null ? [] : await context.SourceActivities.AsNoTracking().Where(activity => activity.SourceRevisionId == row.SourceRevisionId)
            .OrderByDescending(activity => activity.UpdatedAtUtc).ThenByDescending(activity => activity.Id).Take(20)
            .Select(activity => new { activity.Id, activity.State, activity.Reason, activity.ResultingPipelineRecordId, activity.UpdatedAtUtc })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var activities = activityRows.Select(activity => new CorpusActivityEvidence(activity.Id, ((SourceActivityState)activity.State).ToString(), activity.Reason, activity.ResultingPipelineRecordId, activity.UpdatedAtUtc)).ToArray();

        return new CorpusEntryDetail(row.PipelineRecordId, row.Entry, row.SourceKind, ((PipelineStage)row.CurrentStage).ToString(), state,
            row.LatestActivityReason, preview, row.SourceRootId, row.SourceRevisionId, row.SafeDisplayIdentity, row.ContentSha256, jobs, eventRows.Select(value => value.Id).ToArray(),
            new CorpusLineage(row.RootLineageRecordId, row.ParentPipelineRecordId, row.ParentSourceRevisionId), activities,
            eventRows.Select(value => new CorpusEventEvidence(value.Id, value.OccurredAtUtc, value.EventType, SanitiseEventDetails(value.DetailsJson))).ToArray());
    }

    private static IQueryable<Row> BuildRows(FluxKnowledgeDbContext context) =>
        from record in context.PipelineRecords.AsNoTracking()
        join identity in context.SourceIdentities.AsNoTracking() on record.SourceIdentityId equals identity.Id
        join revisionValue in context.SourceRevisions.AsNoTracking() on record.SourceRevisionId equals revisionValue.Id into revisions
        from revision in revisions.DefaultIfEmpty()
        join rootValue in context.SourceRootConfigurations.AsNoTracking() on revision.SourceRootId equals rootValue.Id into roots
        from root in roots.DefaultIfEmpty()
        let latestEvent = context.AuditEvents.Where(e => e.PipelineRecordId == record.Id || (revision != null && e.SourceRevisionId == revision.Id))
            .Select(e => (DateTimeOffset?)e.OccurredAtUtc).Max()
        let latestActivity = context.SourceActivities.Where(a => revision != null && a.SourceRevisionId == revision.Id &&
                (a.ResultingPipelineRecordId == null || a.ResultingPipelineRecordId == record.Id))
            .OrderByDescending(a => a.ResultingPipelineRecordId == record.Id).ThenByDescending(a => a.UpdatedAtUtc).ThenByDescending(a => a.Id).Select(a => new { a.State, a.ResultingPipelineRecordId, a.Reason }).FirstOrDefault()
        select new Row
        {
            PipelineRecordId = record.Id,
            RecordRevision = record.Revision,
            IsDeleted = record.IsDeleted,
            CurrentStage = record.CurrentStage,
            RegisteredAtUtc = record.RegisteredAtUtc,
            RootLineageRecordId = record.RootLineageRecordId,
            ParentPipelineRecordId = record.ParentRevisionRecordId,
            SourceKind = identity.SourceKind,
            SafeSourceIdentity = identity.StableKey,
            SourceRevisionId = revision == null ? null : revision.Id,
            SourceRootId = revision == null ? null : revision.SourceRootId,
            RootDisplayName = root == null ? null : root.DisplayName,
            SourceClassification = revision == null ? null : revision.Classification,
            SuppressedAtUtc = revision == null ? null : revision.SuppressedAtUtc,
            ParentSourceRevisionId = revision == null ? null : revision.ParentSourceRevisionId,
            RelativePath = revision == null || root == null ? null : revision.CanonicalPath.Substring(root.CanonicalPath.Length + 1),
            ContentSha256 = revision == null ? null : revision.ContentSha256,
            LastActivityAtUtc = latestEvent ?? record.RegisteredAtUtc,
            LatestActivityState = latestActivity == null ? null : (int?)latestActivity.State,
            LatestActivityResultingPipelineRecordId = latestActivity == null ? null : latestActivity.ResultingPipelineRecordId,
            LatestActivityReason = latestActivity == null ? null : latestActivity.Reason
        };

    private static IQueryable<Row> ApplyActivityFilter(IQueryable<Row> rows, string status) => status switch
    {
        "indexed" => rows.Where(row => row.SourceRevisionId == null || (row.LatestActivityState == (int)SourceActivityState.Completed && row.LatestActivityResultingPipelineRecordId == row.PipelineRecordId)),
        "deferred" => rows.Where(row => row.LatestActivityState == (int)SourceActivityState.DeferredUnsupported),
        "blocked" => rows.Where(row => row.LatestActivityState == (int)SourceActivityState.DeferredPolicy),
        "failed" => rows.Where(row => row.LatestActivityState == (int)SourceActivityState.FailedTerminal),
        _ => rows.Where(row => row.SourceRevisionId != null && row.LatestActivityState != null && row.LatestActivityState != (int)SourceActivityState.Completed && row.LatestActivityState != (int)SourceActivityState.DeferredUnsupported && row.LatestActivityState != (int)SourceActivityState.DeferredPolicy && row.LatestActivityState != (int)SourceActivityState.FailedTerminal)
    };

    private static CorpusEntry ToEntry(Row row) => new(row.PipelineRecordId, row.Entry, row.SourceKind, row.SourceClassification,
        row.SourceRootId is null ? "Direct" : Location(row.RootDisplayName, row.RelativePath), ((PipelineStage)row.CurrentStage).ToString(),
        SourceActivityStatus(row.LatestActivityState, row.LatestActivityResultingPipelineRecordId, row.PipelineRecordId, row.SourceRevisionId), row.LastActivityAtUtc,
        row.SourceRootId, row.SourceRevisionId, row.LatestActivityResultingPipelineRecordId);

    private static string SourceActivityStatus(int? state, Guid? resultingRecordId, Guid pipelineRecordId, Guid? sourceRevisionId) =>
        sourceRevisionId is null ? "Indexed" : state switch
        {
            (int)SourceActivityState.Completed when resultingRecordId == pipelineRecordId => "Indexed",
            (int)SourceActivityState.DeferredUnsupported => "Deferred",
            (int)SourceActivityState.DeferredPolicy => "Blocked",
            (int)SourceActivityState.FailedTerminal => "Failed",
            _ => "Pending"
        };

    private static string FolderOf(string? relativePath) => relativePath is null ? string.Empty :
        relativePath.LastIndexOf('\\') is var separator && separator >= 0 ? relativePath[..separator] : string.Empty;

    private static string Location(string? rootDisplayName, string? relativePath) => string.IsNullOrEmpty(FolderOf(relativePath))
        ? rootDisplayName ?? "Source"
        : (rootDisplayName ?? "Source") + "\\" + FolderOf(relativePath);

    private static async ValueTask<string?> ReadPreviewAsync(FluxKnowledgeDbContext context, Guid pipelineRecordId, long revision, CancellationToken cancellationToken)
    {
        var text = await context.Artifacts.AsNoTracking().Where(artifact => artifact.PipelineRecordId == pipelineRecordId && artifact.SourceRevision == revision && artifact.ContentType.StartsWith("text/"))
            .OrderByDescending(artifact => artifact.CreatedAtUtc).Select(artifact => artifact.SearchText).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(text))
            return text[..Math.Min(PreviewLimit, text.Length)];
        var chunk = await context.TextChunks.AsNoTracking().Where(chunk => context.Artifacts.Any(artifact => artifact.Id == chunk.ArtifactId && artifact.PipelineRecordId == pipelineRecordId && artifact.SourceRevision == revision && artifact.ContentType.StartsWith("text/")))
            .OrderBy(chunk => chunk.Ordinal).Select(chunk => chunk.Content).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return chunk is null ? null : chunk[..Math.Min(PreviewLimit, chunk.Length)];
    }

    private static async ValueTask<Guid[]?> SearchCandidatesAsync(FluxKnowledgeDbContext context, string? search, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;
        var candidates = await context.Database.SqlQuery<SearchCandidate>($"""
            SELECT DISTINCT [record].[Id] AS [PipelineRecordId]
            FROM FREETEXTTABLE([Artifacts], [SearchText], {search}) AS [match]
            JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [match].[KEY]
            JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
              AND [record].[Revision] = [artifact].[SourceRevision]
            """).Select(candidate => candidate.PipelineRecordId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Length > 0)
            return candidates;

        // SQL Server can populate a full-text catalogue asynchronously.  Preserve SQL as
        // the authority while giving a newly persisted indexed-text row deterministic
        // discovery before its full-text population catches up.
        return await context.Artifacts.AsNoTracking()
            .Where(artifact => artifact.SearchText != null && artifact.SearchText.Contains(search))
            .Join(context.PipelineRecords.AsNoTracking(), artifact => new { artifact.PipelineRecordId, artifact.SourceRevision }, record => new { PipelineRecordId = record.Id, SourceRevision = record.Revision }, (artifact, record) => record.Id)
            .Distinct()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SanitiseEventDetails(string details) => details.Length <= 2_048 && details is "{}" or "{\"truncated\":true}" ? details : "{\"sanitised\":true}";

    private sealed class Row
    {
        public Guid PipelineRecordId { get; init; }
        public long RecordRevision { get; init; }
        public bool IsDeleted { get; init; }
        public int CurrentStage { get; init; }
        public DateTimeOffset RegisteredAtUtc { get; init; }
        public Guid RootLineageRecordId { get; init; }
        public Guid? ParentPipelineRecordId { get; init; }
        public string SourceKind { get; init; } = string.Empty;
        public string SafeSourceIdentity { get; init; } = string.Empty;
        public Guid? SourceRevisionId { get; init; }
        public Guid? SourceRootId { get; init; }
        public string? RootDisplayName { get; init; }
        public string? SourceClassification { get; init; }
        public DateTimeOffset? SuppressedAtUtc { get; init; }
        public Guid? ParentSourceRevisionId { get; init; }
        public string? RelativePath { get; init; }
        public string? ContentSha256 { get; init; }
        public DateTimeOffset LastActivityAtUtc { get; init; }
        public int? LatestActivityState { get; init; }
        public Guid? LatestActivityResultingPipelineRecordId { get; init; }
        public string? LatestActivityReason { get; init; }
        public string SafeDisplayIdentity => System.IO.Path.GetFileName(SafeSourceIdentity);
        public string Entry => RelativePath is { Length: > 0 } ? RelativePath : SafeDisplayIdentity;
    }
    private sealed class FolderRow { public string RelativePath { get; init; } = string.Empty; public int CurrentCount { get; init; } public int DeferredCount { get; init; } public int BlockedCount { get; init; } public int FailedCount { get; init; } }
    private sealed class SearchCandidate { public Guid PipelineRecordId { get; init; } }
}
