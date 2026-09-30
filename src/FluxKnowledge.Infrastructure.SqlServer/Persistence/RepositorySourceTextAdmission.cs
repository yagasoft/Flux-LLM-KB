using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class RepositorySourceTextAdmission
{
    public static bool IsAdmitted(SourceRootConfigurationEntity root, SourceRevisionEntity revision)
    {
        if (root.CrawlMode != (int)SourceDiscoveryMode.GitTracked || root.State != (int)SourceRootState.Enabled ||
            revision.OriginKind != 0 || revision.SuppressedAtUtc is not null || revision.Classification != "AcceptedUtf8Text" ||
            !SourceClassifier.IsSourceTextExtension(revision.CanonicalPath)) return false;
        try
        {
            var policy = JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson) ?? [];
            using var evidence = JsonDocument.Parse(revision.DiscoveryEvidenceJson ?? "{}");
            if (!policy.Contains("text/x-source-code", StringComparer.OrdinalIgnoreCase) || !policy.Contains("text/plain", StringComparer.OrdinalIgnoreCase) ||
                !evidence.RootElement.TryGetProperty("gitInventory", out var inventory)) return false;
            var admitted = inventory.Deserialize<GitInventoryEvidence>();
            return admitted is not null && admitted.RepositoryIdentity == SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson) &&
                admitted.Generation.Length == 64 && admitted.Generation.All(char.IsAsciiHexDigit);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    public static bool IsIntentional(SourceActivityEntity activity, SourceRevisionEntity revision) =>
        activity.ActivityKind == (int)SourceActivityKind.TextExtraction && activity.ExecutionClass == (int)ExecutionClass.InProcess &&
        activity.RequiredCapability is null && activity.InputFingerprint == revision.ContentSha256 &&
        RepositorySourceTextPolicy.MatchesActivity(activity.ProcessorVersion, activity.DescriptorFingerprint);
}
