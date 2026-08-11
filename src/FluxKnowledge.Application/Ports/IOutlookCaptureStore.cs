using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

/// <summary>
/// SQL-authoritative Outlook control plane. Implementations retain private Outlook identities only
/// in access-restricted persistence and reject stale or unsolicited host work by fence and revision.
/// </summary>
public interface IOutlookCaptureStore
{
    ValueTask<OutlookOperationReceipt> SaveProfileAsync(OutlookProfileSaveRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> PauseProfileAsync(OutlookProfilePauseRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RemoveProfileAsync(OutlookProfileRemoveRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RecordHintAsync(OutlookHintRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RequestCatchUpAsync(OutlookCatchUpRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookCatchUpClaimReceipt> ClaimCatchUpAsync(OutlookCatchUpClaimRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RenewCatchUpLeaseAsync(OutlookCatchUpLeaseRenewalRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> CompleteCatchUpAsync(OutlookCatchUpCompletionRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> FailCatchUpAsync(OutlookCatchUpFailureRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RequeueCatchUpAsync(OutlookCatchUpRequeueRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> ReleaseStaleCatchUpLeaseAsync(OutlookStaleCatchUpLeaseReleaseRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RequestBrowseAsync(OutlookBrowseRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookBrowseClaimReceipt> ClaimBrowseAsync(OutlookBrowseClaimRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> CompleteBrowseAsync(OutlookBrowseCompletionRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> FailBrowseAsync(OutlookBrowseFailureRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> ReleaseStaleBrowseClaimsAsync(Guid operationId, string requestFingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<OutlookProfileProjection>> ReadLocalProjectionAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<OutlookBrowseFolderProjection>> ReadBrowseResultAsync(Guid correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Recovery-only durable boundary. It can release a specifically fenced stale lease and replay
/// an existing hint receipt; it cannot claim host work, access Outlook, advance a cursor or
/// activate deferred processing.
/// </summary>
public interface IOutlookCaptureRecoveryStore
{
    ValueTask<OutlookCaptureRecoverySnapshot> ReadRecoverySnapshotAsync(
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset pendingHintBeforeUtc,
        CancellationToken cancellationToken);

    ValueTask<OutlookOperationReceipt> ReleaseStaleCatchUpLeaseAsync(
        OutlookStaleCatchUpLeaseReleaseRequest request,
        CancellationToken cancellationToken);

    ValueTask<OutlookOperationReceipt> ReplayHintAsync(
        OutlookHintRequest request,
        CancellationToken cancellationToken);
}
