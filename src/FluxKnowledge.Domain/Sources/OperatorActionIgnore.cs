namespace FluxKnowledge.Domain.Sources;

/// <summary>Internal reversible triage command for one immutable action-version; it exposes no transport.</summary>
public sealed record OperatorActionIgnoreCommand(
    string ActionId,
    Guid OperationId,
    string RequestFingerprint,
    byte[] ExpectedBlockedRowVersion,
    bool IsIgnored);

/// <summary>Durable ignore/unignore receipt. Sequence advances only for a new operation.</summary>
public sealed record OperatorActionIgnoreReceipt(
    string ActionId,
    Guid OperationId,
    long Sequence,
    bool IsIgnored)
{
    /// <summary>True only when the serialisable mutation resolved an already-recorded operation.</summary>
    public bool WasReplay { get; init; }

    /// <summary>Database timestamp of the durable operation ledger row.</summary>
    public DateTimeOffset CommittedAtUtc { get; init; }
}

public sealed class OperatorActionRejectedException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
