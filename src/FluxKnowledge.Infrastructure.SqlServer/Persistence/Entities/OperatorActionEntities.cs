namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Closed deny-list which no operator capability policy may authorise.</summary>
public sealed class OperatorActionHardDenialEntity
{
    public string ReasonCode { get; set; } = string.Empty;
}

public static class OperatorActionHardDenialReasons
{
    public static readonly IReadOnlyList<string> All =
    [
        "retained-artifact-missing", "retained-artifact-path-invalid", "retained-artifact-checksum-invalid",
        "retained-artifact-root-unavailable", "retained-artifact-transient",
        "office-document-container-invalid", "office-document-depth-limit", "office-document-element-limit",
        "office-document-encrypted", "office-document-expanded-xml-limit", "office-document-input-too-large",
        "office-document-part-unsupported", "office-document-text-limit", "office-document-xml-invalid",
        "archive-input-too-large", "archive-entry-count-limit", "archive-expanded-total-limit",
        "archive-member-size-limit", "archive-member-size-invalid", "archive-compression-ratio-limit",
        "archive-entry-path-invalid", "archive-entry-unsupported", "archive-entry-encrypted",
        "archive-entry-compression-unsupported", "archive-signature-invalid", "archive-member-identity-conflict",
        "archive-member-not-utf8", "archive-entry-link-invalid", "nested-archive-depth-limit",
        "legacy-office-binary-parser-unavailable", "processor-parser-unavailable", "processor-provenance-invalid",
        "csharp-code-input-too-large", "csharp-code-text-limit", "csharp-code-input-not-utf8", "csharp-code-node-limit",
        "csharp-code-depth-limit", "csharp-code-symbol-limit", "csharp-code-reference-limit", "csharp-code-identifier-limit",
        "csharp-code-signature-limit", "csharp-code-diagnostic-limit", "csharp-code-syntax-invalid",
        "source-activity-cancelled", "source-activity-superseded", "lease-expired-reconciled", "processor-fence-invalid"
    ];
}

/// <summary>Immutable capability membership; a row does not itself register or activate a processor.</summary>
public sealed class OperatorActionCapabilityPolicyEntity
{
    public Guid PolicyId { get; set; }
    public long PolicyRevision { get; set; }
    public Guid DescriptorId { get; set; }
    public string DescriptorFingerprint { get; set; } = string.Empty;
    public string DescriptorVersion { get; set; } = string.Empty;
    public string SafetyContractId { get; set; } = string.Empty;
    public string HandlerId { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
}

/// <summary>One immutable blocked action-version and the durable receipt it resolves to.</summary>
public sealed class OperatorActionActionLedgerEntity
{
    public string ActionId { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyRevision { get; set; }
    public Guid DescriptorId { get; set; }
    public string DescriptorFingerprint { get; set; } = string.Empty;
    public string DescriptorVersion { get; set; } = string.Empty;
    public string SafetyContractId { get; set; } = string.Empty;
    public string HandlerId { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public Guid SourceProcessorBranchId { get; set; }
    public byte[] BlockedRowVersion { get; set; } = [];
    public Guid? SourceProcessorForceRequestId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Global operation identity, preserving exact replay and detecting payload collisions.</summary>
public sealed class OperatorActionOperationLedgerEntity
{
    public Guid OperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long? IgnoreSequence { get; set; }
    public bool? IgnoreState { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Current reversible triage state for a single immutable action-version.</summary>
public sealed class SourceProcessorActionIgnoreHeadEntity
{
    public string ActionId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public bool IsIgnored { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
