using System.Text.Json;
using System.Text;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Knowledge;
using Microsoft.AspNetCore.Http;

namespace FluxKnowledge.Web.NativeV1;

/// <summary>Maps the deliberately small native v1 wire contract to Application-owned requests.</summary>
public sealed class NativeV1RequestMapper
{
    public const int MaximumBodyBytes = NativeV1ContractLimits.MaximumRequestBytes;

    public object MapQuery(string toolName, JsonElement arguments)
    {
        var input = Input(arguments);
        return toolName switch
        {
            "knowledge.search" => new NativeKnowledgeQuery(NativeV1ContractLimits.CanonicalizeKnowledgeQuery(QueryString(input, "query")), Limit(input)),
            "knowledge.graph" => new NativeGraphQuery(NativeV1ContractLimits.CanonicalizeGraphNode(QueryString(input, "node")), RequiredInt(input, "max_depth", 1, 8), RequiredInt(input, "max_results", 1, 100)),
            "code.query" => new NativeCodeQuery(RequiredString(input, "view"), NativeV1ContractLimits.CanonicalizeOptionalCodeQuery(QueryString(input, "query", required: false)), OptionalGuid(input, "branch_id"), Limit(input), OptionalCursor(input)),
            "corpus.query" => new NativeCorpusQuery(RequiredString(input, "view"), OptionalGuid(input, "root_id"), OptionalGuid(input, "branch_id"), OptionalGuid(input, "job_id"), Limit(input), OptionalCursor(input)),
            "operations.status" => new NativeOperationsStatus(RequiredString(input, "view"), OptionalGuid(input, "root_id"), OptionalGuid(input, "job_id"), Limit(input)),
            "operations.audit" => new NativeAuditQuery(RequiredString(input, "view"), OptionalGuid(input, "root_id"), OptionalGuid(input, "job_id"), Limit(input), OptionalCursor(input)),
            _ => throw new NativeOperationException("tool-not-allowed")
        };
    }

    public object MapAction(string toolName, JsonElement arguments)
    {
        var input = Input(arguments);
        return toolName switch
        {
            "knowledge.write" => new KnowledgeMutation(
                RequiredString(input, "action"), OptionalString(input, "item_id"), OptionalString(input, "title"), OptionalString(input, "body"),
                OptionalString(input, "subject"), OptionalString(input, "predicate"), OptionalString(input, "object_text"),
                OptionalString(input, "transition"), OptionalString(input, "related_claim_id"), OptionalString(input, "reason"), OptionalDecimal(input, "confidence")),
            "code.write" => new NativeCodeFeedbackMutation(RequiredPayload(input)),
            "corpus.write" => new NativeCorpusMutation(RequiredString(input, "action"), RequiredPayload(input)),
            _ => throw new NativeOperationException("tool-not-allowed")
        };
    }

    public string ActionFamily(string toolName, object command) => toolName switch
    {
        "knowledge.write" when command is KnowledgeMutation mutation => KnowledgeActionFamily(mutation),
        "code.write" when command is NativeCodeFeedbackMutation => "code",
        "corpus.write" when command is NativeCorpusMutation => "corpus",
        _ => throw new NativeOperationException("tool-not-allowed")
    };

    public string? ConfirmationId(JsonElement arguments) => OptionalString(Input(arguments), "confirmation_id");

    public JsonElement FromQuery(IQueryCollection query)
    {
        _ = int.TryParse(query["limit"].ToString(), out var limit);
        return JsonSerializer.SerializeToElement(new
        {
            view = query["view"].ToString(),
            root_id = NullIfEmpty(query["root_id"].ToString()),
            job_id = NullIfEmpty(query["job_id"].ToString()),
            limit
        });
    }

    private static JsonElement Input(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new NativeOperationException("invalid-request");
        }

        if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumBodyBytes)
        {
            throw new NativeOperationException("body-too-large");
        }

        return arguments;
    }

    private static string RequiredString(JsonElement input, string property)
    {
        var value = OptionalString(input, property);
        if (string.IsNullOrWhiteSpace(value)) throw new NativeOperationException("invalid-request");
        return value;
    }

    private static string? OptionalString(JsonElement input, string property)
    {
        if (!input.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new NativeOperationException("invalid-request");
        var text = value.GetString();
        if (text is { Length: > 4096 }) throw new NativeOperationException("invalid-request");
        return text;
    }

    private static string? QueryString(JsonElement input, string property, bool required = true)
    {
        if (!input.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new NativeOperationException("invalid-query");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) throw new NativeOperationException("invalid-query");
        return value.GetString();
    }

    private static Guid? OptionalGuid(JsonElement input, string property)
    {
        var value = OptionalString(input, property);
        if (value is null) return null;
        if (!Guid.TryParse(value, out var parsed)) throw new NativeOperationException("invalid-request");
        return parsed;
    }

    private static int Limit(JsonElement input) => RequiredInt(input, "limit", 1, 100);

    private static int RequiredInt(JsonElement input, string property, int minimum, int maximum)
    {
        if (!input.TryGetProperty(property, out var value) || !value.TryGetInt32(out var parsed) || parsed is < 1 or > 100 || parsed < minimum || parsed > maximum)
        {
            throw new NativeOperationException(property == "limit" ? "invalid-limit" : "invalid-request");
        }

        return parsed;
    }

    private static decimal? OptionalDecimal(JsonElement input, string property)
    {
        if (!input.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetDecimal(out var parsed)) throw new NativeOperationException("invalid-request");
        return parsed;
    }

    private static string? OptionalCursor(JsonElement input)
    {
        var cursor = OptionalString(input, "cursor");
        if (cursor is { Length: > 2048 }) throw new NativeOperationException("cursor-invalid");
        return cursor;
    }

    private static JsonElement RequiredPayload(JsonElement input)
    {
        if (!input.TryGetProperty("payload", out var payload) || payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new NativeOperationException("invalid-request");
        }

        return payload.Clone();
    }

    private static string KnowledgeActionFamily(KnowledgeMutation mutation)
    {
        var action = mutation.Action?.Trim().ToLowerInvariant();
        return action switch
        {
            "note_create" or "forget" => "knowledge",
            "claim_upsert" or "claim_transition" => "graph",
            _ => throw new NativeOperationException("action-not-allowed")
        };
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
