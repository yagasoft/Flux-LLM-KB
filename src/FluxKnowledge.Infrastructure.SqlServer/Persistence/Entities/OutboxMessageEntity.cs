namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public int Stage { get; set; }
    public string Operation { get; set; } = string.Empty;
    public long DispatchGeneration { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset DueAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long LeaseGeneration { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public PipelineRecordEntity PipelineRecord { get; set; } = null!;
}
