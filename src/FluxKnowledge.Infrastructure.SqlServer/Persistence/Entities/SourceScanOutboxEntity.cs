namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Control-plane scan dispatch record; it has no public executor route.</summary>
public sealed class SourceScanOutboxEntity
{
    public Guid Id { get; set; }
    public Guid SourceScanRequestId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset DueAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long LeaseGeneration { get; set; }
    public int AttemptCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceScanRequestEntity SourceScanRequest { get; set; } = null!;
}
