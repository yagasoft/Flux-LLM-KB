using FluxKnowledge.Domain.Outlook;

namespace FluxKnowledge.Application.Contracts;

public sealed record OutlookOperationReceipt(Guid OperationId, bool Accepted, bool Committed, bool IsReplay);

/// <summary>
/// Private durable recovery input. These records are consumed only by the recovery coordinator
/// and are never exposed through status, REST, MCP, CLI or SignalR projections.
/// </summary>
public sealed record OutlookCatchUpLeaseRecoveryCandidate(
    Guid CatchUpId,
    OutlookCaptureProfileId ProfileId,
    long FencingToken,
    DateTimeOffset LeaseExpiresAtUtc)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireGuid(CatchUpId, nameof(CatchUpId));
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        if (FencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FencingToken));
        }

        OutlookCaptureContractValidation.RequireUtc(LeaseExpiresAtUtc, nameof(LeaseExpiresAtUtc));
    }
}

/// <summary>Private durable hint replay input; no Outlook or spool identity is included.</summary>
public sealed record OutlookHintRecoveryCandidate(
    OutlookHintRequest Hint,
    DateTimeOffset RecordedAtUtc)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Hint);
        Hint.Validate();
        OutlookCaptureContractValidation.RequireUtc(RecordedAtUtc, nameof(RecordedAtUtc));
    }
}

public sealed record OutlookCaptureRecoverySnapshot(
    IReadOnlyList<OutlookCatchUpLeaseRecoveryCandidate> CatchUpLeases,
    IReadOnlyList<OutlookHintRecoveryCandidate> PendingHints);

/// <summary>Durable evidence for a catch-up lease claim; a null claim means no eligible work was available.</summary>
public sealed record OutlookCatchUpClaimReceipt(
    OutlookCatchUpClaim? Claim,
    bool Accepted,
    bool Committed,
    bool IsReplay);

/// <summary>Durable evidence for a browse lease claim; a null claim means no eligible request was available.</summary>
public sealed record OutlookBrowseClaimReceipt(
    OutlookBrowseClaim? Claim,
    bool Accepted,
    bool Committed,
    bool IsReplay);

public sealed record OutlookExportCommitReceipt(
    OutlookCaptureExportId ExportId,
    bool Accepted,
    bool Committed,
    bool IsReplay);

public sealed record OutlookCaptureSchedule(TimeSpan Cadence, TimeSpan MaximumOverlap)
{
    public static readonly TimeSpan MinimumCadence = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumCadence = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumOverlapWindow = TimeSpan.FromHours(4);

    public void Validate()
    {
        if (Cadence < MinimumCadence || Cadence > MaximumCadence)
        {
            throw new ArgumentOutOfRangeException(nameof(Cadence), "Outlook capture cadence must be between five minutes and twenty-four hours.");
        }

        if (MaximumOverlap < TimeSpan.Zero || MaximumOverlap > MaximumOverlapWindow || MaximumOverlap >= Cadence)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOverlap), "Outlook capture overlap must be bounded and shorter than its cadence.");
        }
    }
}

/// <summary>Sanitised save-time evidence; the configured spool root remains private persistence data.</summary>
public sealed record OutlookSpoolValidation(
    string PathFingerprint,
    bool IsLocalPath,
    bool HasRequiredAccess,
    bool HasSufficientCapacity,
    bool IsWritable,
    string? PrivateSpoolRoot = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireCanonicalSha256(PathFingerprint, nameof(PathFingerprint));
        if (!IsLocalPath || !HasRequiredAccess || !HasSufficientCapacity || !IsWritable)
        {
            throw new ArgumentException("Outlook spool validation requires a local, writable path with the required ACL and capacity.");
        }
        OutlookCaptureContractValidation.RequireOpaque(PrivateSpoolRoot, nameof(PrivateSpoolRoot), 2048);
    }
}

public enum OutlookConfigurationWarning
{
    ReceivedTimeRequiresManualReconciliation
}

public sealed record OutlookProfileSaveRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCaptureProfileId? ProfileId,
    string DisplayName,
    OutlookIncrementalBasis IncrementalBasis,
    OutlookCaptureSchedule Schedule,
    OutlookSpoolValidation SpoolValidation,
    bool Enable = false,
    long? ExpectedConfigurationRevision = null,
    Guid? BrowseCorrelationId = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        if (ProfileId is not null && ProfileId.Value == Guid.Empty)
        {
            throw new ArgumentException("An Outlook profile ID cannot be empty.", nameof(ProfileId));
        }
        if (ExpectedConfigurationRevision is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedConfigurationRevision));
        }
        if (ProfileId is not null && ExpectedConfigurationRevision is null)
        {
            throw new ArgumentException("An existing Outlook profile save requires its expected configuration revision.", nameof(ExpectedConfigurationRevision));
        }
        if (BrowseCorrelationId == Guid.Empty)
        {
            throw new ArgumentException("An Outlook browse correlation cannot be empty.", nameof(BrowseCorrelationId));
        }
        if (ProfileId is not null && Enable && BrowseCorrelationId is null)
        {
            throw new ArgumentException("Enabling an Outlook profile requires a completed browse correlation.", nameof(BrowseCorrelationId));
        }

        OutlookCaptureContractValidation.RequireDisplayName(DisplayName, nameof(DisplayName));
        if (!Enum.IsDefined(IncrementalBasis))
        {
            throw new ArgumentOutOfRangeException(nameof(IncrementalBasis));
        }

        ArgumentNullException.ThrowIfNull(Schedule);
        ArgumentNullException.ThrowIfNull(SpoolValidation);
        Schedule.Validate();
        SpoolValidation.Validate();
    }
}

public sealed record OutlookProfilePauseRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCaptureProfileId ProfileId,
    string Reason)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        OutlookCaptureContractValidation.RequireBoundedReason(Reason, nameof(Reason));
    }
}

public sealed record OutlookProfileRemoveRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCaptureProfileId ProfileId,
    string Reason)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        OutlookCaptureContractValidation.RequireBoundedReason(Reason, nameof(Reason));
    }
}

public sealed record OutlookProfileProjection(
    OutlookCaptureProfileId ProfileId,
    string DisplayName,
    OutlookCaptureState State,
    OutlookIncrementalBasis IncrementalBasis,
    long ConfigurationRevision,
    OutlookCaptureSchedule Schedule,
    IReadOnlyList<OutlookConfigurationWarning> Warnings);

public sealed record OutlookHintRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCaptureProfileId ProfileId,
    string CoalescingKey,
    string? Reason = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        OutlookCaptureContractValidation.RequireOpaque(CoalescingKey, nameof(CoalescingKey), 256);
        OutlookCaptureContractValidation.RequireOptionalBoundedReason(Reason, nameof(Reason));
    }
}

public sealed record OutlookHostIdentity(string WindowsUserSid, int SessionId, string HostInstanceId)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOpaque(WindowsUserSid, nameof(WindowsUserSid), 256);
        if (SessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SessionId));
        }

        OutlookCaptureContractValidation.RequireOpaque(HostInstanceId, nameof(HostInstanceId), 256);
    }
}

public sealed record OutlookBrowseRequest(
    Guid OperationId,
    string RequestFingerprint,
    Guid BrowseRequestId,
    Guid CorrelationId,
    long ConfigurationRevision,
    DateTimeOffset ExpiresAtUtc,
    OutlookCaptureProfileId? ProfileId = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireGuid(BrowseRequestId, nameof(BrowseRequestId));
        OutlookCaptureContractValidation.RequireGuid(CorrelationId, nameof(CorrelationId));
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        if (ConfigurationRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConfigurationRevision));
        }

        OutlookCaptureContractValidation.RequireUtc(ExpiresAtUtc, nameof(ExpiresAtUtc));
    }
}

public sealed record OutlookBrowseClaimRequest(
    Guid OperationId,
    string RequestFingerprint,
    Guid BrowseRequestId,
    OutlookHostIdentity Host,
    DateTimeOffset LeaseExpiresAtUtc)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireGuid(BrowseRequestId, nameof(BrowseRequestId));
        ArgumentNullException.ThrowIfNull(Host);
        Host.Validate();
        OutlookCaptureContractValidation.RequireUtc(LeaseExpiresAtUtc, nameof(LeaseExpiresAtUtc));
    }
}

public sealed record OutlookBrowseClaim(
    Guid BrowseRequestId,
    Guid CorrelationId,
    long ConfigurationRevision,
    OutlookHostIdentity Host,
    long FencingToken,
    DateTimeOffset LeaseExpiresAtUtc)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireGuid(BrowseRequestId, nameof(BrowseRequestId));
        OutlookCaptureContractValidation.RequireGuid(CorrelationId, nameof(CorrelationId));
        if (ConfigurationRevision <= 0 || FencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FencingToken));
        }

        ArgumentNullException.ThrowIfNull(Host);
        Host.Validate();
        OutlookCaptureContractValidation.RequireUtc(LeaseExpiresAtUtc, nameof(LeaseExpiresAtUtc));
    }
}

/// <summary>A safe local browse result: it deliberately contains no StoreId or FolderEntryId.</summary>
public sealed record OutlookBrowseFolderProjection(OutlookCaptureFolderId FolderId, string DisplayName)
{
    public void Validate()
    {
        if (FolderId is null || FolderId.Value == Guid.Empty)
        {
            throw new ArgumentException("An Outlook browse folder ID is required.", nameof(FolderId));
        }

        OutlookCaptureContractValidation.RequireDisplayName(DisplayName, nameof(DisplayName));
    }
}

/// <summary>Private browse result used only by the host-to-store boundary; it must never be projected.</summary>
public sealed record OutlookBrowseFolderResult(OutlookCaptureFolderId FolderId, string StoreId, string FolderEntryId, string DisplayName)
{
    public void Validate()
    {
        if (FolderId is null || FolderId.Value == Guid.Empty) throw new ArgumentException("A folder ID is required.", nameof(FolderId));
        OutlookCaptureContractValidation.RequireOpaque(StoreId, nameof(StoreId), 4096);
        OutlookCaptureContractValidation.RequireOpaque(FolderEntryId, nameof(FolderEntryId), 4096);
        OutlookCaptureContractValidation.RequireDisplayName(DisplayName, nameof(DisplayName));
    }
}

public sealed record OutlookBrowseCompletionRequest(
    Guid OperationId,
    string RequestFingerprint,
    Guid BrowseRequestId,
    OutlookHostIdentity Host,
    long FencingToken,
    IReadOnlyList<OutlookBrowseFolderProjection> Folders,
    long ConfigurationRevision = 1,
    IReadOnlyList<OutlookBrowseFolderResult>? PrivateFolders = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireGuid(BrowseRequestId, nameof(BrowseRequestId));
        ArgumentNullException.ThrowIfNull(Host);
        Host.Validate();
        if (FencingToken <= 0 || ConfigurationRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FencingToken));
        }

        ArgumentNullException.ThrowIfNull(Folders);
        if (Folders.Count > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(Folders), "Outlook browse completion is bounded to 500 folders.");
        }

        foreach (var folder in Folders)
        {
            ArgumentNullException.ThrowIfNull(folder);
            folder.Validate();
        }
        if (PrivateFolders is not null)
        {
            if (PrivateFolders.Count != Folders.Count) throw new ArgumentException("Private browse identities must match the projected result count.", nameof(PrivateFolders));
            foreach (var folder in PrivateFolders) { ArgumentNullException.ThrowIfNull(folder); folder.Validate(); }
        }
    }
}

public enum OutlookBrowseFailureCode
{
    Expired,
    AccessDenied,
    HostUnavailable,
    Failed
}

public sealed record OutlookBrowseFailureRequest(
    Guid OperationId,
    string RequestFingerprint,
    Guid BrowseRequestId,
    OutlookHostIdentity Host,
    long FencingToken,
    OutlookBrowseFailureCode FailureCode)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireGuid(BrowseRequestId, nameof(BrowseRequestId));
        ArgumentNullException.ThrowIfNull(Host);
        Host.Validate();
        if (FencingToken <= 0 || !Enum.IsDefined(FailureCode))
        {
            throw new ArgumentOutOfRangeException(nameof(FencingToken));
        }
    }
}

public enum OutlookCatchUpProvenance
{
    Manual,
    Schedule,
    Hint
}

public sealed record OutlookCatchUpRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCaptureProfileId ProfileId,
    string CoalescingKey,
    OutlookCatchUpProvenance Provenance,
    string? Reason = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        OutlookCaptureContractValidation.RequireOpaque(CoalescingKey, nameof(CoalescingKey), 256);
        if (!Enum.IsDefined(Provenance))
        {
            throw new ArgumentOutOfRangeException(nameof(Provenance));
        }

        OutlookCaptureContractValidation.RequireOptionalBoundedReason(Reason, nameof(Reason));
    }
}

public sealed record OutlookCatchUpClaimRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookHostIdentity Host,
    TimeSpan LeaseDuration)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        ArgumentNullException.ThrowIfNull(Host);
        Host.Validate();
        if (LeaseDuration < TimeSpan.FromMinutes(1) || LeaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        }
    }
}

public sealed record OutlookCatchUpClaim(
    Guid CatchUpId,
    OutlookCaptureProfileId ProfileId,
    string CoalescingKey,
    OutlookCatchUpProvenance Provenance,
    int RetryCount,
    string? Reason,
    OutlookHostIdentity LeaseOwner,
    DateTimeOffset LeaseExpiresAtUtc,
    DateTimeOffset LastHeartbeatAtUtc,
    long FencingToken)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireGuid(CatchUpId, nameof(CatchUpId));
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        OutlookCaptureContractValidation.RequireOpaque(CoalescingKey, nameof(CoalescingKey), 256);
        if (!Enum.IsDefined(Provenance) || RetryCount < 0 || FencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FencingToken));
        }

        OutlookCaptureContractValidation.RequireOptionalBoundedReason(Reason, nameof(Reason));
        ArgumentNullException.ThrowIfNull(LeaseOwner);
        LeaseOwner.Validate();
        OutlookCaptureContractValidation.RequireUtc(LeaseExpiresAtUtc, nameof(LeaseExpiresAtUtc));
        OutlookCaptureContractValidation.RequireUtc(LastHeartbeatAtUtc, nameof(LastHeartbeatAtUtc));
        if (LastHeartbeatAtUtc > LeaseExpiresAtUtc)
        {
            throw new ArgumentException("A catch-up lease heartbeat cannot be after its expiry.");
        }
    }
}

public sealed record OutlookCatchUpLeaseRenewalRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCatchUpClaim Claim,
    DateTimeOffset RenewedLeaseExpiresAtUtc)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        ArgumentNullException.ThrowIfNull(Claim);
        Claim.Validate();
        OutlookCaptureContractValidation.RequireUtc(RenewedLeaseExpiresAtUtc, nameof(RenewedLeaseExpiresAtUtc));
        if (RenewedLeaseExpiresAtUtc <= Claim.LastHeartbeatAtUtc)
        {
            throw new ArgumentException("A renewed catch-up lease must extend beyond its heartbeat.", nameof(RenewedLeaseExpiresAtUtc));
        }
    }
}

public sealed record OutlookCatchUpCompletionRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCatchUpClaim Claim,
    int ExportedCount)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        ArgumentNullException.ThrowIfNull(Claim);
        Claim.Validate();
        if (ExportedCount < 0 || ExportedCount > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ExportedCount));
        }
    }
}

public enum OutlookCatchUpFailureReason
{
    RetryableHostFailure,
    LeaseLost,
    AccessDenied,
    ConfigurationChanged,
    Blocked
}

public sealed record OutlookCatchUpFailureRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCatchUpClaim Claim,
    OutlookCatchUpFailureReason FailureReason)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        ArgumentNullException.ThrowIfNull(Claim);
        Claim.Validate();
        if (!Enum.IsDefined(FailureReason))
        {
            throw new ArgumentOutOfRangeException(nameof(FailureReason));
        }
    }
}

public sealed record OutlookCatchUpRequeueRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCatchUpClaim Claim,
    OutlookCatchUpFailureReason RetryReason,
    DateTimeOffset NotBeforeUtc)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        ArgumentNullException.ThrowIfNull(Claim);
        Claim.Validate();
        if (!Enum.IsDefined(RetryReason))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryReason));
        }

        OutlookCaptureContractValidation.RequireUtc(NotBeforeUtc, nameof(NotBeforeUtc));
    }
}

public sealed record OutlookStaleCatchUpLeaseReleaseRequest(
    Guid OperationId,
    string RequestFingerprint,
    Guid CatchUpId,
    OutlookCaptureProfileId ProfileId,
    long FencingToken,
    DateTimeOffset LeaseExpiresAtUtc)
{
    public void Validate(DateTimeOffset observedAtUtc)
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        if (CatchUpId == Guid.Empty)
        {
            throw new ArgumentException("A stale Outlook catch-up release requires its exact catch-up identity.", nameof(CatchUpId));
        }
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        if (FencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FencingToken));
        }

        OutlookCaptureContractValidation.RequireUtc(LeaseExpiresAtUtc, nameof(LeaseExpiresAtUtc));
        OutlookCaptureContractValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (LeaseExpiresAtUtc > observedAtUtc)
        {
            throw new ArgumentException("A non-stale Outlook catch-up lease cannot be released.", nameof(LeaseExpiresAtUtc));
        }
    }
}

public sealed record OutlookExportCommitRequest(
    Guid OperationId,
    string RequestFingerprint,
    OutlookCaptureExportId ExportId,
    Guid CatchUpId,
    long FencingToken,
    OutlookExportObservation? Observation = null)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireOperation(OperationId, RequestFingerprint);
        if (ExportId is null || ExportId.Value == Guid.Empty || CatchUpId == Guid.Empty || FencingToken <= 0)
        {
            throw new ArgumentException("An export commit requires a non-empty export ID and exact catch-up claim identity.");
        }
    }
}

/// <summary>Private ready-export observation, sufficient for durable reconciliation without reopening Outlook.</summary>
public sealed record OutlookExportObservation(
    OutlookCaptureProfileId ProfileId,
    OutlookCaptureFolderId FolderId,
    string EntryId,
    string SourceFingerprint,
    string ManifestHash,
    string RelativeSpoolPath,
    DateTimeOffset CursorUtc,
    string CursorFingerprint)
{
    public void Validate()
    {
        OutlookCaptureContractValidation.RequireProfileId(ProfileId, nameof(ProfileId));
        if (FolderId is null || FolderId.Value == Guid.Empty) throw new ArgumentException("A folder ID is required.", nameof(FolderId));
        OutlookCaptureContractValidation.RequireOpaque(EntryId, nameof(EntryId), 4096);
        OutlookCaptureContractValidation.RequireCanonicalSha256(SourceFingerprint, nameof(SourceFingerprint));
        OutlookCaptureContractValidation.RequireCanonicalSha256(ManifestHash, nameof(ManifestHash));
        OutlookCaptureContractValidation.RequireOpaque(RelativeSpoolPath, nameof(RelativeSpoolPath), 2048);
        OutlookCaptureContractValidation.RequireUtc(CursorUtc, nameof(CursorUtc));
        OutlookCaptureContractValidation.RequireCanonicalSha256(CursorFingerprint, nameof(CursorFingerprint));
    }
}

internal static class OutlookCaptureContractValidation
{
    public static void RequireOperation(Guid operationId, string requestFingerprint)
    {
        RequireGuid(operationId, nameof(operationId));
        RequireCanonicalSha256(requestFingerprint, nameof(requestFingerprint));
    }

    public static void RequireGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }
    }

    public static void RequireProfileId(OutlookCaptureProfileId? profileId, string parameterName)
    {
        if (profileId is null || profileId.Value == Guid.Empty)
        {
            throw new ArgumentException("An Outlook profile ID is required.", parameterName);
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

    public static void RequireOpaque(string? value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{parameterName} must be canonical and at most {maximumLength} characters.", parameterName);
        }
    }

    public static void RequireDisplayName(string? value, string parameterName)
    {
        RequireOpaque(value, parameterName, 256);
        if (value!.Any(char.IsControl))
        {
            throw new ArgumentException("Display names cannot contain control characters.", parameterName);
        }
    }

    public static void RequireBoundedReason(string? value, string parameterName)
    {
        RequireOpaque(value, parameterName, 1024);
    }

    public static void RequireOptionalBoundedReason(string? value, string parameterName)
    {
        if (value is not null)
        {
            RequireBoundedReason(value, parameterName);
        }
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC timestamp is required.", parameterName);
        }
    }
}
