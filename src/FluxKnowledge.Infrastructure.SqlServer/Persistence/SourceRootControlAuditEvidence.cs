using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Canonical source-root configuration and bounded control audit evidence shared by all source-control writers.</summary>
internal sealed record SourceRootControlConfiguration(
    string IncludePatternsJson,
    string ExcludePatternsJson,
    string AllowedClassificationsJson,
    string Fingerprint,
    string DisplayName,
    bool Recursive,
    bool FollowLinks,
    long MaximumFileBytes,
    long ReconciliationCadenceSeconds)
{
    public static SourceRootControlConfiguration From(SourceRootCreateRequest request) =>
        From(
            request.DisplayName,
            request.Recursive,
            request.FollowLinks,
            request.MaximumFileBytes,
            CanonicalJson(request.IncludePatterns),
            CanonicalJson(request.ExcludePatterns),
            CanonicalJson(request.AllowedClassifications),
            checked((long)request.ReconciliationCadence.TotalSeconds));

    public static SourceRootControlConfiguration From(SourceRootConfigurationEntity entity) =>
        From(
            entity.DisplayName,
            entity.Recursive,
            entity.FollowLinks,
            entity.MaximumFileBytes,
            entity.IncludePatternsJson,
            entity.ExcludePatternsJson,
            entity.AllowedClassificationsJson,
            entity.ReconciliationCadenceSeconds);

    private static SourceRootControlConfiguration From(
        string displayName,
        bool recursive,
        bool followLinks,
        long maximumFileBytes,
        string includePatternsJson,
        string excludePatternsJson,
        string allowedClassificationsJson,
        long reconciliationCadenceSeconds)
    {
        var framed = string.Join(
            "\n",
            displayName,
            includePatternsJson,
            excludePatternsJson,
            allowedClassificationsJson,
            recursive ? "1" : "0",
            followLinks ? "1" : "0",
            maximumFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reconciliationCadenceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new SourceRootControlConfiguration(
            includePatternsJson,
            excludePatternsJson,
            allowedClassificationsJson,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(framed))),
            displayName,
            recursive,
            followLinks,
            maximumFileBytes,
            reconciliationCadenceSeconds);
    }

    public bool Matches(SourceRootConfigurationEntity entity) =>
        string.Equals(DisplayName, entity.DisplayName, StringComparison.Ordinal) &&
        string.Equals(IncludePatternsJson, entity.IncludePatternsJson, StringComparison.Ordinal) &&
        string.Equals(ExcludePatternsJson, entity.ExcludePatternsJson, StringComparison.Ordinal) &&
        string.Equals(AllowedClassificationsJson, entity.AllowedClassificationsJson, StringComparison.Ordinal) &&
        Recursive == entity.Recursive &&
        FollowLinks == entity.FollowLinks &&
        MaximumFileBytes == entity.MaximumFileBytes &&
        ReconciliationCadenceSeconds == entity.ReconciliationCadenceSeconds;

    private static string CanonicalJson(IReadOnlyList<string>? values) =>
        JsonSerializer.Serialize((values ?? Array.Empty<string>()).OrderBy(static value => value, StringComparer.Ordinal).ToArray());
}

internal static class SourceRootControlAuditEvidence
{
    public static string CreateHealthEvidence(SourceRootPathValidation? validation, SourceRootControlConfiguration configuration) =>
        JsonSerializer.Serialize(new
        {
            physicalIdentity = validation is null
                ? null
                : new
                {
                    validation.PhysicalIdentity.VolumeRoot,
                    validation.PhysicalIdentity.IsFixedNtfs,
                    validation.PhysicalIdentity.IdentityFingerprint
                },
            configurationFingerprint = configuration.Fingerprint
        });

    public static string CreateRequestEvidence(
        SourceRootControlConfiguration configuration,
        string requestedBy,
        string? releasedBy,
        DateTimeOffset? releasedAtUtc = null) =>
        JsonSerializer.Serialize(new
        {
            configurationFingerprint = configuration.Fingerprint,
            requestedByFingerprint = ActorFingerprint(requestedBy),
            releasedByFingerprint = releasedBy is null ? null : ActorFingerprint(releasedBy),
            releasedAtUtc
        });

    public static string UpdateConfigurationFingerprint(string? existingEvidenceJson, SourceRootControlConfiguration configuration)
    {
        var evidence = ParseObject(existingEvidenceJson, "Source root health evidence is invalid.");
        evidence["configurationFingerprint"] = configuration.Fingerprint;
        return evidence.ToJsonString();
    }

    public static string UpdateRequestConfigurationFingerprint(string? existingEvidenceJson, SourceRootControlConfiguration configuration)
    {
        var evidence = ParseObject(existingEvidenceJson, "Source scan request audit evidence is invalid.");
        evidence["configurationFingerprint"] = configuration.Fingerprint;
        return evidence.ToJsonString();
    }

    public static string AppendReleaseEvidence(string? existingEvidenceJson, string actor, DateTimeOffset releasedAtUtc)
    {
        var evidence = ParseObject(existingEvidenceJson, "Source scan request audit evidence is invalid.");
        evidence["releasedByFingerprint"] = ActorFingerprint(actor);
        evidence["releasedAtUtc"] = releasedAtUtc;
        return evidence.ToJsonString();
    }

    private static JsonObject ParseObject(string? existingEvidenceJson, string errorMessage)
    {
        try
        {
            return JsonNode.Parse(existingEvidenceJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(errorMessage, exception);
        }
    }

    private static string ActorFingerprint(string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (actor.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(actor), "Source control actor must be at most 256 characters.");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actor)));
    }
}
