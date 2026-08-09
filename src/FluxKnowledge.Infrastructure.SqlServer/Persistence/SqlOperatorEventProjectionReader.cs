using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlOperatorEventProjectionReader(IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : IOperatorEventProjectionReader
{
    public async ValueTask<OperatorEventPage> ReadPageAsync(OperatorEventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var filters = query.Filters;
        var rows = context.AuditEvents.AsNoTracking();
        if (filters.Family is { } family) rows = rows.Where(value => value.EventFamily != null && value.EventFamily == family);
        if (filters.Severity is { } severity) rows = rows.Where(value => value.Severity != null && value.Severity == severity);
        if (filters.SourceRootId is { } rootId) rows = rows.Where(value => value.SourceRootId == rootId);
        if (filters.PipelineRecordId is { } recordId) rows = rows.Where(value => value.PipelineRecordId == recordId);
        if (filters.SourceRevisionId is { } revisionId) rows = rows.Where(value => value.SourceRevisionId == revisionId);
        if (filters.CorrelationId is { } correlationId) rows = rows.Where(value => value.CorrelationId == correlationId);
        if (filters.OccurredFromUtc is { } from) rows = rows.Where(value => value.OccurredAtUtc >= from);
        if (filters.OccurredToUtc is { } to) rows = rows.Where(value => value.OccurredAtUtc <= to);
        if (query.Cursor is { } cursor) rows = rows.Where(value => value.OccurredAtUtc < cursor.OccurredAtUtc || (value.OccurredAtUtc == cursor.OccurredAtUtc && value.Id < cursor.EventId));
        var values = await rows.OrderByDescending(value => value.OccurredAtUtc).ThenByDescending(value => value.Id).Take(query.PageSize + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var items = values.Take(query.PageSize).Select(value => new OperatorEventEntry(value.Id, value.OccurredAtUtc, value.EventType,
            value.EventFamily ?? Family(value.EventType), value.Severity ?? "information", value.EventType, value.PipelineRecordId, value.SourceRootId,
            value.SourceRevisionId, value.SourceActivityId, value.SourceScanRequestId, value.CorrelationId, SanitiseDetails(value.DetailsJson))).ToArray();
        var last = items.LastOrDefault();
        return new OperatorEventPage(items, values.Length > query.PageSize && last is not null
            ? OperatorEventCursor.Create(last.OccurredAtUtc, last.Id, query.CanonicalFilter) : null);
    }

    private static string Family(string eventType) => eventType.Split('.', 2)[0];

    // Historical audit rows pre-date the allow-listed appender.  Do not reflect an
    // arbitrary legacy payload into the operator UI.
    private static string SanitiseDetails(string details) => details.Length <= 2_048 && details is "{}" or "{\"truncated\":true}" ? details : "{\"sanitised\":true}";
}
