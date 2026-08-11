namespace FluxKnowledge.Domain.Outlook;

public sealed record OutlookCaptureProfileId(Guid Value)
{
    public static OutlookCaptureProfileId New() => new(Guid.NewGuid());
}

public sealed record OutlookCaptureFolderId(Guid Value)
{
    public static OutlookCaptureFolderId New() => new(Guid.NewGuid());
}

public sealed record OutlookCaptureExportId(Guid Value)
{
    public static OutlookCaptureExportId New() => new(Guid.NewGuid());
}

public enum OutlookIncrementalBasis
{
    LastModificationTime,
    ReceivedTime
}

public enum OutlookCaptureState
{
    Disabled,
    AwaitingHost,
    CatchUpPending,
    CatchingUp,
    Ready,
    Blocked,
    Stale
}

public enum OutlookExportState
{
    Inflight,
    ReadyForIngestion,
    Ingested,
    Deferred,
    Blocked
}

internal static class OutlookCaptureValidation
{
    public static void RequireOpaqueValue(string? value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{parameterName} must be canonical and at most {maximumLength} characters.", parameterName);
        }
    }

    public static void RequireDisplayName(string? value, string parameterName)
    {
        RequireOpaqueValue(value, parameterName, 256);
        if (value!.Any(char.IsControl))
        {
            throw new ArgumentException("Display names cannot contain control characters.", parameterName);
        }
    }

    public static void RequireCanonicalSha256(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new ArgumentException("A canonical lower-case SHA-256 fingerprint is required.", parameterName);
        }
    }

    public static void RequireNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }
    }
}
