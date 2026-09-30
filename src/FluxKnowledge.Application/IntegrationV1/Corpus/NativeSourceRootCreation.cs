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
        var sourceText = Bool(payload, "indexSourceText", false);
        if (sourceText && mode != SourceDiscoveryMode.GitTracked) throw new NativeOperationException("invalid-discovery-mode");
        return new SourceRootCreateRequest(payload.GetProperty("path").GetString()!, payload.GetProperty("displayName").GetString()!,
            Bool(payload, "recursive", true), Rules(payload, "includePatterns"), Rules(payload, "excludePatterns"),
            Bool(payload, "followLinks", false), Long(payload, "maximumFileBytes", 16L * 1024 * 1024),
            sourceText ? ["text/plain", "text/x-source-code"] : [],
            TimeSpan.FromSeconds(Long(payload, "reconciliationSeconds", 900)), actor, DiscoveryMode: mode);
    }

    private static IReadOnlyList<string> Rules(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)) return [];
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
