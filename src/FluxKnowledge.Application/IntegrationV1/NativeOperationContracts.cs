using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Application.IntegrationV1;

public sealed record NativeActionPreviewRequest(
    string Action, string CanonicalPayload, string ActorSurface)
{
    public IReadOnlyList<NativeTargetVersion> Targets { get; init; } = [];
    public string RequestFingerprint { get; init; } = string.Empty;
    public string EffectSummary { get; init; } = string.Empty;
}

public sealed record NativeActionPreview(
    Guid IntentId, string ConfirmationId, string RequestFingerprint,
    DateTimeOffset ExpiresAtUtc, IReadOnlyList<NativeTargetVersion> Targets,
    string EffectSummary);

public sealed record NativeActionCommitRequest(
    string Action, string CanonicalPayload, string ConfirmationId,
    string IdempotencyKey, string ActorSurface)
{
    public IReadOnlyList<NativeTargetVersion> Targets { get; init; } = [];
    public string RequestFingerprint { get; init; } = string.Empty;
    public NativeActionCommitOperation? CommitOperation { get; init; }
}

public sealed record NativeActionReceipt(
    Guid OperationId, bool WasReplay, string Outcome, string? ReasonCode);

/// <summary>Safe concurrency metadata selected by a family-specific command handler.</summary>
public sealed record NativeTargetVersion(string TargetId, string RowVersion);

public delegate ValueTask<IReadOnlyList<NativeTargetVersion>> NativeTargetVersionResolver(
    string canonicalPayload,
    CancellationToken cancellationToken);

/// <summary>A family-specific closed action registration; unregistered actions cannot reach persistence.</summary>
public sealed record NativeActionDefinition(
    string Action,
    string EffectSummary,
    NativeTargetVersionResolver ResolveTargetsAsync,
    NativeActionCommitOperationResolver ResolveCommitOperationAsync);

public abstract record NativeActionCommitOperation;

/// <summary>A closed foundation operation which conditionally changes one versioned native target.</summary>
public sealed record NativeFenceTargetMutation(string TargetId, string NewValue) : NativeActionCommitOperation;

public delegate ValueTask<NativeActionCommitOperation> NativeActionCommitOperationResolver(
    string canonicalPayload,
    IReadOnlyList<NativeTargetVersion> targets,
    CancellationToken cancellationToken);

public sealed class NativeOperationCommitUncertainException(Exception innerException)
    : InvalidOperationException("commit-uncertain", innerException);

public sealed class NativeOperationException(string reasonCode)
    : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}

public static class NativeOperationCanonicalization
{
    public static string CanonicalizeAction(string action)
    {
        var canonical = action?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(canonical) || canonical.Length > 128)
        {
            throw new NativeOperationException("invalid-action");
        }

        return canonical;
    }

    public static string CanonicalizeJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new NativeOperationException("invalid-canonical-payload");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return CanonicalizeElement(document.RootElement);
        }
        catch (JsonException)
        {
            throw new NativeOperationException("invalid-canonical-payload");
        }
    }

    public static IReadOnlyList<NativeTargetVersion> CanonicalizeTargets(IReadOnlyList<NativeTargetVersion> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > 128)
        {
            throw new NativeOperationException("invalid-targets");
        }

        var canonical = targets.Select(target =>
        {
            var targetId = target.TargetId?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 256 ||
                string.IsNullOrWhiteSpace(target.RowVersion) || target.RowVersion.Length > 128)
            {
                throw new NativeOperationException("invalid-targets");
            }

            return new NativeTargetVersion(targetId, target.RowVersion);
        }).OrderBy(target => target.TargetId, StringComparer.Ordinal)
          .ThenBy(target => target.RowVersion, StringComparer.Ordinal)
          .ToArray();

        if (canonical.Select(target => target.TargetId).Distinct(StringComparer.Ordinal).Count() != canonical.Length)
        {
            throw new NativeOperationException("invalid-targets");
        }

        return canonical;
    }

    public static string CreateRequestFingerprint(
        string action,
        string canonicalPayload,
        IReadOnlyList<NativeTargetVersion> targets)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action,
            payload = canonicalPayload,
            targets = targets.Select(target => target.TargetId)
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string CreateConfirmationHash(string confirmationId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(confirmationId)));

    public static string CreateConfirmationId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string SerializeTargets(IReadOnlyList<NativeTargetVersion> targets) =>
        JsonSerializer.Serialize(targets.Select(target => new { target.TargetId, target.RowVersion }));

    private static string CanonicalizeElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalizeElement(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalizeElement)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => throw new NativeOperationException("invalid-canonical-payload")
    };
}
