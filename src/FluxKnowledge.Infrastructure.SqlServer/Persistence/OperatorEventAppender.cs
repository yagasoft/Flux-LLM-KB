using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Adds an operator-visible event to the caller-owned EF unit of work.</summary>
public static class OperatorEventAppender
{
    public static void Add(FluxKnowledgeDbContext context, OperatorEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        context.AuditEvents.Add(Create(draft));
    }

    public static AuditEventEntity Create(OperatorEventDraft draft) => new()
    {
        PipelineRecordId = draft.PipelineRecordId,
        SourceRootId = draft.SourceRootId,
        SourceScanRequestId = draft.SourceScanRequestId,
        SourceRevisionId = draft.SourceRevisionId,
        SourceActivityId = draft.SourceActivityId,
        CorrelationId = Limit(draft.CorrelationId, 256),
        EventFamily = Limit(draft.EventFamily, 128),
        Severity = Limit(draft.Severity, 64),
        EventType = Limit(draft.EventType, 256) ?? "operator.event",
        Actor = Limit(draft.Actor, 256) ?? "system",
        DetailsJson = SanitiseDetails(draft.Details),
        OccurredAtUtc = draft.OccurredAtUtc
    };

    private static string SanitiseDetails(object? details)
    {
        var node = JsonSerializer.SerializeToNode(details) as JsonObject;
        if (node is null)
        {
            return "{}";
        }

        var allowed = new JsonObject();
        foreach (var key in AllowedDetailKeys)
        {
            if (node[key] is not JsonValue value || !TrySanitiseScalar(key, value, out var sanitised))
            {
                continue;
            }

            allowed[key] = sanitised;
        }

        var json = allowed.ToJsonString();
        return json.Length <= 2048 ? json : "{\"truncated\":true}";
    }

    private static readonly string[] AllowedDetailKeys = ["revision", "classification", "kind", "executionClass", "stage", "sourceActivity", "reasonCode", "descriptor", "action", "state"];

    private static bool TrySanitiseScalar(string key, JsonValue value, out JsonNode? sanitised)
    {
        if (key == "revision" && value.TryGetValue<long>(out var revision) && revision >= 0)
        {
            sanitised = JsonValue.Create(revision);
            return true;
        }
        if (key == "sourceActivity" && value.TryGetValue<bool>(out var boolean)) { sanitised = JsonValue.Create(boolean); return true; }
        if (value.TryGetValue<string>(out var text) && text.Length <= 128 && text.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            sanitised = JsonValue.Create(text);
            return true;
        }
        sanitised = null;
        return false;
    }

    private static string? Limit(string? value, int length) => value is null ? null : value[..Math.Min(value.Length, length)];
}

public sealed record OperatorEventDraft(
    string EventType,
    string EventFamily,
    string Severity,
    string Actor,
    DateTimeOffset OccurredAtUtc,
    Guid? PipelineRecordId = null,
    Guid? SourceRootId = null,
    Guid? SourceScanRequestId = null,
    Guid? SourceRevisionId = null,
    Guid? SourceActivityId = null,
    string? CorrelationId = null,
    object? Details = null)
{
    public static OperatorEventDraft SourceAdded(Guid rootId, Guid? requestId, Guid revisionId, string? correlationId, object? details) =>
        new("source.added", "source", "information", "source-reconciliation", DateTimeOffset.UtcNow, SourceRootId: rootId, SourceScanRequestId: requestId, SourceRevisionId: revisionId, CorrelationId: correlationId, Details: details);
    public static OperatorEventDraft SourceUpdated(Guid rootId, Guid? requestId, Guid revisionId, string? correlationId, object? details) =>
        new("source.updated", "source", "information", "source-reconciliation", DateTimeOffset.UtcNow, SourceRootId: rootId, SourceScanRequestId: requestId, SourceRevisionId: revisionId, CorrelationId: correlationId, Details: details);
    public static OperatorEventDraft SourceRemoved(Guid rootId, Guid revisionId, string? correlationId, object? details) =>
        new("source.removed", "source", "information", "source-reconciliation", DateTimeOffset.UtcNow, SourceRootId: rootId, SourceRevisionId: revisionId, CorrelationId: correlationId, Details: details);
    public static OperatorEventDraft PipelineCompleted(Guid recordId, string? correlationId, object? details) =>
        new("pipeline.completed", "pipeline", "information", "pipeline-worker", DateTimeOffset.UtcNow, PipelineRecordId: recordId, CorrelationId: correlationId, Details: details);
    public static OperatorEventDraft ActivityPlanned(Guid activityId, Guid revisionId, Guid rootId, object? details, bool deferred = false) =>
        new(deferred ? "activity.deferred" : "activity.planned", "activity", "information", "source-reconciliation", DateTimeOffset.UtcNow, SourceRootId: rootId, SourceRevisionId: revisionId, SourceActivityId: activityId, CorrelationId: $"source:{revisionId:N}", Details: details);

    /// <summary>
    /// Creates the sole operator-audit shape for private native-worker lifecycle evidence.
    /// Process attestation and protocol data remain in private SQL records.
    /// </summary>
    public static OperatorEventDraft NativeWorkerLifecycle(
        NativeWorkerLifecycleClass lifecycleClass,
        Guid instanceId,
        int? reasonCode,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(lifecycleClass))
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycleClass));
        }

        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("A native worker instance correlation is required.", nameof(instanceId));
        }

        NativeWorkerInstanceHandle.RequireUtcTimestamp(occurredAtUtc, nameof(occurredAtUtc));
        if (reasonCode is < -32768 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(reasonCode));
        }

        var kind = ToNativeWorkerEventSuffix(lifecycleClass);
        return new(
            $"native_worker.{kind}",
            "native_worker",
            "information",
            "native-worker-supervisor",
            occurredAtUtc,
            CorrelationId: $"native-worker:{instanceId:N}",
            Details: new { kind, reasonCode = reasonCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    /// <summary>Creates bounded, metadata-only evidence for a durable Outlook control-plane mutation.</summary>
    public static OperatorEventDraft OutlookMutation(
        string mutationKind,
        Guid operationId,
        bool accepted,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationKind);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An Outlook operation correlation is required.", nameof(operationId));
        }
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Outlook audit timestamps must be UTC.", nameof(occurredAtUtc));
        }

        var kind = new string(mutationKind
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_')
            .Take(64)
            .ToArray());
        return new OperatorEventDraft(
            $"outlook.{kind}",
            "outlook",
            "information",
            "outlook-control-plane",
            occurredAtUtc,
            CorrelationId: $"outlook-operation:{operationId:N}",
            Details: new { kind, reasonCode = accepted ? "accepted" : "rejected" });
    }

    private static string ToNativeWorkerEventSuffix(NativeWorkerLifecycleClass lifecycleClass) =>
        string.Concat(lifecycleClass.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
}
