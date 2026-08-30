namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class KnowledgeItemEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SafeBody { get; set; } = string.Empty;
    /// <summary>Safe-only search projection, cleared with the source content on forget.</summary>
    public string SafeSearchText { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ForgottenAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class KnowledgeClaimEntity
{
    public Guid Id { get; set; }
    public string CanonicalIdentity { get; set; } = string.Empty;
    public string CanonicalIdentityHash { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Predicate { get; set; } = string.Empty;
    public string ObjectText { get; set; } = string.Empty;
    /// <summary>Safe-only canonical claim text used by bounded native SQL search.</summary>
    public string SafeSearchText { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public int Revision { get; set; }
    public string LifecycleState { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ForgottenAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Immutable evidence of every claim lifecycle revision without raw forgotten content.</summary>
public sealed class KnowledgeClaimHistoryEntity
{
    public Guid Id { get; set; }
    public Guid ClaimId { get; set; }
    public int Revision { get; set; }
    public string LifecycleState { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class KnowledgeRelationEntity
{
    public Guid ClaimId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Predicate { get; set; } = string.Empty;
    public string ObjectText { get; set; } = string.Empty;
}

/// <summary>Minimum durable evidence retained after forgetting; no title, note, claim or reason text is retained.</summary>
public sealed class KnowledgeTombstoneEntity
{
    public Guid Id { get; set; }
    public string TargetKind { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public DateTimeOffset ForgottenAtUtc { get; set; }
}
