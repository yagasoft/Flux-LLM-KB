using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.IntegrationV1.Corpus;

/// <summary>The same creation policy is used for admission and durable commit.</summary>
public static class NativeSourceRootCreation
{
    public static SourceRootCreateRequest Parse(JsonElement payload, string actor)
    {
        if (payload.TryGetProperty("discoveryMode", out var suppliedMode) && suppliedMode.ValueKind != JsonValueKind.String)
            throw new NativeOperationException("invalid-discovery-mode");
        var mode = !payload.TryGetProperty("discoveryMode", out var modeValue) ? SourceDiscoveryMode.Filesystem : modeValue.GetString() switch
        {
            "filesystem" => SourceDiscoveryMode.Filesystem,
            "git-tracked" => SourceDiscoveryMode.GitTracked,
            _ => throw new NativeOperationException("invalid-discovery-mode")
        };
        var sourceText = Bool(payload, "indexSourceText", true);
        _ = Long(payload, "maximumFileBytes", SourcePolicyDefaults.MaximumTextBytes);
        return new SourceRootCreateRequest(payload.GetProperty("path").GetString()!, payload.GetProperty("displayName").GetString()!,
            Bool(payload, "recursive", true), ReadRules(payload, "includePatterns") ?? [], ReadRules(payload, "excludePatterns") ?? [],
            Bool(payload, "followLinks", false), SourcePolicyDefaults.MaximumTextBytes,
            sourceText ? ["text/plain", "text/x-source-code"] : [],
            TimeSpan.FromSeconds(Long(payload, "reconciliationSeconds", SourcePolicyDefaults.MetadataReconciliationSeconds)), actor, DiscoveryMode: mode);
    }

    public static IReadOnlyList<string>? ReadRules(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) throw new NativeOperationException("invalid-payload");
        var result = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString())) throw new NativeOperationException("invalid-payload");
            result.Add(entry.GetString()!);
        }
        return result;
    }

    private static bool Bool(JsonElement payload, string name, bool fallback) => !payload.TryGetProperty(name, out var value) ? fallback :
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new NativeOperationException("invalid-payload");
    private static long Long(JsonElement payload, string name, long fallback) => !payload.TryGetProperty(name, out var value) ? fallback :
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number is >= 1 and <= 1_073_741_824 ? number : throw new NativeOperationException("invalid-payload");
}
