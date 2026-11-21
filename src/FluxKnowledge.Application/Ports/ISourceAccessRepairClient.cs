using System.Text.Json.Serialization;

namespace FluxKnowledge.Application.Ports;

public sealed record SourceAccessRepairRequest(
    string RootPath, string? RootIdentityFingerprint, string RelativePath, bool IsDirectory)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SourceRegistrationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeniedFileIdentity { get; init; }
}

public enum SourceAccessRepairOutcome
{
    Repaired, AlreadyReadable, NotApproved, Unavailable, ExplicitDeny, UnsupportedPermissions,
    OutsideScope, IdentityChanged, ContentUnavailable, Unconfirmed
}

public delegate ValueTask<SourceAccessRepairOutcome> SourceAccessRepairAdmission(
    Func<SourceAccessRepairOutcome> dispatch, CancellationToken cancellationToken);

/// <summary>Requests scoped read recovery from a separately authorised broker.</summary>
public interface ISourceAccessRepairClient
{
    SourceAccessRepairOutcome TryRepair(SourceAccessRepairRequest request, CancellationToken cancellationToken);
}

/// <summary>A native access denial at one known component of a guarded source open.</summary>
public sealed class SourceAccessDeniedException(
    string deniedPath, bool isDirectory, Exception innerException, string reasonCode = "SourceAccessDenied")
    : UnauthorizedAccessException("The indexing identity cannot access this source item. " + reasonCode, innerException)
{
    public string DeniedPath { get; } = deniedPath;
    public bool IsDirectory { get; } = isDirectory;
    public string? DeniedFileIdentity { get; set; }
    public string ReasonCode { get; } = reasonCode;
}
