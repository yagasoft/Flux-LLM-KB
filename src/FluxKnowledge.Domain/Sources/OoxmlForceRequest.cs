namespace FluxKnowledge.Domain.Sources;

/// <summary>Durable lifecycle states for a one-generation OOXML force request.</summary>
public enum OoxmlForceRequestState : byte
{
    Requested,
    Claimed,
    Completed,
    Blocked,
    Transient,
    Cancelled,
    Expired
}

/// <summary>Internal, opaque summary of an exact current OOXML blocked action-version.</summary>
public sealed record OoxmlForceActionSummary(
    Guid BranchId,
    Guid SourceActivityId,
    SourceRevisionId SourceRevisionId,
    string ActionId,
    string RequestFingerprint,
    string BlockedRowVersionToken,
    long OriginalBlockedLeaseGeneration,
    string OutcomeCode,
    bool CanForce,
    OoxmlForceRequestState? RequestState = null);

/// <summary>Internal command only. Task 4B-B owns all transport exposure.</summary>
public sealed record OoxmlForceRequestCommand(
    string ActionId,
    Guid OperationId,
    string RequestFingerprint,
    string ExpectedBlockedRowVersion);

/// <summary>Durable, replayable receipt returned for a force request.</summary>
public sealed record OoxmlForceRequestReceipt(
    Guid RequestId,
    string ActionId,
    Guid OperationId,
    OoxmlForceRequestState State,
    string? TerminalReasonCode,
    long? ForceAttemptLeaseGeneration)
{
    /// <summary>True only when the serialisable mutation resolved an already-recorded operation.</summary>
    public bool WasReplay { get; init; }

    /// <summary>Database timestamp of the durable operation ledger row.</summary>
    public DateTimeOffset CommittedAtUtc { get; init; }
}

public sealed class OoxmlForceRequestRejectedException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
