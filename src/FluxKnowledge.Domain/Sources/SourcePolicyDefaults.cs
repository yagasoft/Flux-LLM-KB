namespace FluxKnowledge.Domain.Sources;

/// <summary>Bounded defaults for inert workspace text; caller-supplied restrictions remain explicit.</summary>
public static class SourcePolicyDefaults
{
    public const long MaximumTextBytes = 32L * 1024 * 1024;
    public static IReadOnlyList<string> AllowedClassifications { get; } =
        Array.AsReadOnly(new[] { "text/plain", "text/x-source-code" });
}
