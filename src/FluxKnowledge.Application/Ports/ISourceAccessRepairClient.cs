namespace FluxKnowledge.Application.Ports;

public sealed record SourceAccessRepairRequest(
    string RootPath, string? RootIdentityFingerprint, string RelativePath, bool IsDirectory);

public enum SourceAccessRepairOutcome
{
    Repaired, AlreadyReadable, NotApproved, Unavailable, ExplicitDeny, UnsupportedPermissions,
    OutsideScope, IdentityChanged, ContentUnavailable, Unconfirmed
}

/// <summary>Requests a fixed read grant from a separately authorised broker; conveys no ACL authority.</summary>
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
    public string ReasonCode { get; } = reasonCode;
}
