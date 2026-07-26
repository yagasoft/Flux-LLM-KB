namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class JobEntity
{
    public Guid Id { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public int Stage { get; set; }
    public string Operation { get; set; } = string.Empty;
    public int PublicState { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long LeaseGeneration { get; set; }
    public string? Reason { get; set; }
    public string? ErrorDetails { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public PipelineRecordEntity PipelineRecord { get; set; } = null!;
}
