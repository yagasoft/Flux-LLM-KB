using System.Text;

namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Canonical, transport-neutral hostile-input and envelope bounds for native v1.</summary>
public static class NativeV1ContractLimits
{
    public const int MaximumRequestBytes = 32 * 1024;
    public const int MaximumKnowledgeQueryCharacters = 2048;
    public const int MaximumGraphNodeCharacters = 2048;
    public const int MaximumCodeQueryCharacters = 2048;

    // Audit is the largest native result family: 100 rows, each with a 16,384-character
    // disclosed details value plus 256 + 128 + 64 schema-bounded metadata characters.
    // That also bounds knowledge (256 + 16,384), code (4,096 + 4,096), graph
    // (512 + 128 + 2,048), corpus/status projections, and 128-target action previews.
    // System.Text.Json can escape each UTF-16 code unit to six UTF-8 bytes; the remainder
    // covers the canonical envelope, disclosure wrappers, cursors, and fixed row metadata.
    public const int MaximumResponseBytes =
        100 * ((16 * 1024) + 256 + 128 + 64) * 6 + (256 * 1024);

    public static string CanonicalizeKnowledgeQuery(string? value) =>
        CanonicalizeRequired(value, MaximumKnowledgeQueryCharacters);

    public static string CanonicalizeGraphNode(string? value) =>
        CanonicalizeRequired(value, MaximumGraphNodeCharacters);

    public static string CanonicalizeCodeQuery(string? value) =>
        CanonicalizeRequired(value, MaximumCodeQueryCharacters);

    public static string? CanonicalizeOptionalCodeQuery(string? value)
    {
        if (value is null) return null;
        var canonical = value.Trim().Normalize(NormalizationForm.FormC);
        if (canonical.Length > MaximumCodeQueryCharacters)
        {
            throw new NativeOperationException("invalid-query");
        }
        return canonical;
    }

    private static string CanonicalizeRequired(string? value, int maximumCharacters)
    {
        var canonical = value?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(canonical) || canonical.Length > maximumCharacters)
        {
            throw new NativeOperationException("invalid-query");
        }
        return canonical;
    }
}
