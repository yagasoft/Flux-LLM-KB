namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Confirmation-safe operation intent. The raw request payload is deliberately not stored.</summary>
public sealed class NativeOperationIntentEntity
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ActorSurface { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string ConfirmationHash { get; set; } = string.Empty;
    public string TargetMetadataJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One immutable durable result for a caller surface and idempotency key.</summary>
public sealed class NativeOperationReceiptEntity
{
    public Guid OperationId { get; set; }
    public Guid IntentId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ActorSurface { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Versioned safe native state used by closed operation-family mutations.</summary>
public sealed class NativeOperationFenceTargetEntity
{
    public string TargetId { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
