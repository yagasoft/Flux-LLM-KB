namespace FluxKnowledge.Application.Contracts;

/// <summary>Sanitised local-operator projection. Every identifier and fence is opaque.</summary>
public sealed record OperatorActionProjection(
    string ActionId,
    string RequestFingerprint,
    string? RequestId,
    string BlockedRowVersionToken,
    string DescriptorCapability,
    string ActionState,
    string ReasonCode,
    string? ActionKind,
    DateTimeOffset BlockedAtUtc,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    bool OverrideAvailable,
    bool RetryAvailable,
    bool Ignored);

public sealed record OperatorActionMutationCommand(
    string ActionId,
    Guid OperationId,
    string RequestFingerprint,
    string ExpectedBlockedRowVersion,
    string ActionKind);

public sealed record OperatorActionMutationReceipt(
    string ActionId,
    Guid OperationId,
    string? RequestId,
    string State,
    long? IgnoreSequence,
    bool? Ignored,
    bool WasReplay,
    DateTimeOffset CommittedAtUtc);

public sealed class OperatorActionRequestRejectedException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
