namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Control-plane scan claim; deliberately separate from public pipeline jobs.</summary>
public sealed class SourceScanJobEntity
{
    public Guid Id { get; set; }
    public Guid SourceScanRequestId { get; set; }
    public int State { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long LeaseGeneration { get; set; }
    public int AttemptCount { get; set; }
    public string? Reason { get; set; }
    public string? ErrorDetails { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceScanRequestEntity SourceScanRequest { get; set; } = null!;
}
