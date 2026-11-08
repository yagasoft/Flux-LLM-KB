namespace FluxKnowledge.Domain.Sources;

/// <summary>Bounded defaults for inert workspace text; caller-supplied restrictions remain explicit.</summary>
public static class SourcePolicyDefaults
{
    public const string DetectionPolicy = "events-weekly-metadata-v1";
    public const long MetadataReconciliationSeconds = 7 * 24 * 60 * 60;
    public static TimeSpan MetadataReconciliationCadence => TimeSpan.FromSeconds(MetadataReconciliationSeconds);
    public static bool ChecksumVerificationEnabled => false;
    public const long MaximumTextBytes = 32L * 1024 * 1024;
    public static IReadOnlyList<string> AllowedClassifications { get; } =
        Array.AsReadOnly(new[] { "text/plain", "text/x-source-code" });
}
