namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Durable, immutable action-version request for one bounded OOXML retry attempt.</summary>
public sealed class SourceProcessorForceRequestEntity
{
    public Guid Id { get; set; }
    public string ActionId { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyRevision { get; set; }
    public string DescriptorVersion { get; set; } = string.Empty;
    public string SafetyContractId { get; set; } = string.Empty;
    public string HandlerId { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string PolicyReasonCode { get; set; } = string.Empty;
    public Guid SourceActivityId { get; set; }
    public Guid SourceProcessorBranchId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public Guid DescriptorId { get; set; }
    public string DescriptorFingerprint { get; set; } = string.Empty;
    public string ExpectedInputSha256 { get; set; } = string.Empty;
    public long OriginalBlockedLeaseGeneration { get; set; }
    public byte[] OriginalBlockedRowVersion { get; set; } = [];
    public string OriginalOutcomeCode { get; set; } = string.Empty;
    public byte State { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset ClaimExpiresAtUtc { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public DateTimeOffset? TerminalAtUtc { get; set; }
    public Guid? ForceAttemptBranchId { get; set; }
    public long? ForceAttemptLeaseGeneration { get; set; }
    public string? TerminalReceiptFingerprint { get; set; }
    public string? TerminalReasonCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
