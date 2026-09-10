using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        var items = values.Take(query.PageSize).Select(value =>
        {
            var details = SanitiseDetails(value.EventType, value.DetailsJson);
            return new OperatorEventEntry(value.Id, value.OccurredAtUtc, value.EventType,
                value.EventFamily ?? Family(value.EventType), value.Severity ?? "information", Message(value.EventType, details), value.PipelineRecordId, value.SourceRootId,
                value.SourceRevisionId, value.SourceActivityId, value.SourceScanRequestId, value.CorrelationId, details);
        }).ToArray();
        var last = items.LastOrDefault();
        return new OperatorEventPage(items, values.Length > query.PageSize && last is not null
            ? OperatorEventCursor.Create(last.OccurredAtUtc, last.Id, query.CanonicalFilter) : null);
    }

    private static string Family(string eventType) => eventType.Split('.', 2)[0];

    // Historical audit rows pre-date the allow-listed appender.  Revalidate the
    // only diagnostic shape intentionally rendered to operators; never reflect
    // arbitrary persisted payloads into the UI.
    private static string SanitiseDetails(string eventType, string details)
    {
        var maximumLength = string.Equals(eventType, "codex_hook.processing_failed", StringComparison.Ordinal)
            ? CodexHookFailureMetadata.MaximumFailureDetailsJsonCharacters
            : 2_048;
        if (details.Length > maximumLength)
        {
            return "{\"sanitised\":true}";
        }

        if (!string.Equals(eventType, "codex_hook.processing_failed", StringComparison.Ordinal))
        {
            return details is "{}" or "{\"truncated\":true}" ? details : "{\"sanitised\":true}";
        }

        try
        {
            if (JsonNode.Parse(details) is not JsonObject source)
            {
                return "{\"sanitised\":true}";
            }

            var allowed = new JsonObject();
            AddSafeText(source, allowed, "reasonCode");
            AddSafeText(source, allowed, "phase");
            AddSafeText(source, allowed, "exceptionType");
            AddSafeExceptionText(source, allowed);
            if (TryGetSafeSqlErrorNumber(source, out var sqlErrorNumber))
            {
                allowed["sqlErrorNumber"] = sqlErrorNumber;
            }

            return allowed.Count == 0 ? "{}" : allowed.ToJsonString();
        }
        catch (JsonException)
        {
            return "{\"sanitised\":true}";
        }
    }

    private static string Message(string eventType, string details)
    {
        if (!string.Equals(eventType, "codex_hook.processing_failed", StringComparison.Ordinal))
        {
            return eventType;
        }

        try
        {
            if (JsonNode.Parse(details) is not JsonObject source)
            {
                return eventType;
            }

            var message = new List<string>();
            if (TryGetSafeText(source, "reasonCode", out var reasonCode)) message.Add($"reason: {reasonCode}");
            if (TryGetSafeText(source, "phase", out var phase)) message.Add($"phase: {phase}");
            if (TryGetSafeText(source, "exceptionType", out var exceptionType)) message.Add($"exception: {exceptionType}");
            if (TryGetSafeSqlErrorNumber(source, out var sqlErrorNumber)) message.Add($"SQL error: {sqlErrorNumber}");
            if (TryGetSafeExceptionText(source, out var exceptionText)) message.Add($"exception detail: {exceptionText}");
            return message.Count == 0 ? eventType : string.Join("; ", message);
        }
        catch (JsonException)
        {
            return eventType;
        }
    }

    private static void AddSafeText(JsonObject source, JsonObject target, string propertyName)
    {
        if (TryGetSafeText(source, propertyName, out var text))
        {
            target[propertyName] = text;
        }
    }

    private static void AddSafeExceptionText(JsonObject source, JsonObject target)
    {
        if (TryGetSafeExceptionText(source, out var exceptionText))
        {
            target["exceptionText"] = exceptionText;
        }
    }

    private static bool TryGetSafeText(JsonObject source, string propertyName, out string text)
    {
        text = string.Empty;
        if (source[propertyName] is JsonValue value &&
            value.TryGetValue<string>(out var candidate) &&
            (propertyName switch
            {
                "reasonCode" => CodexHookFailureMetadata.IsReasonCode(candidate),
                "phase" => CodexHookFailureMetadata.IsPhase(candidate),
                "exceptionType" => CodexHookFailureMetadata.IsExceptionType(candidate),
                _ => false
            }))
        {
            text = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetSafeSqlErrorNumber(JsonObject source, out int sqlErrorNumber)
    {
        sqlErrorNumber = 0;
        return source["sqlErrorNumber"] is JsonValue value &&
               value.TryGetValue<int>(out sqlErrorNumber) &&
               CodexHookFailureMetadata.IsSqlErrorNumber(sqlErrorNumber);
    }

    private static bool TryGetSafeExceptionText(JsonObject source, out string exceptionText)
    {
        exceptionText = string.Empty;
        if (source["exceptionText"] is JsonValue value &&
            value.TryGetValue<string>(out var candidate) &&
            candidate is not null &&
            CodexHookFailureMetadata.IsExceptionText(candidate))
        {
            exceptionText = candidate;
            return true;
        }

        return false;
    }
}
