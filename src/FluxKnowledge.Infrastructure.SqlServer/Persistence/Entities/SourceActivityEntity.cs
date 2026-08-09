namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceActivityEntity
{
    public Guid Id { get; set; }
    public Guid SourceRevisionId { get; set; }
    public int ActivityKind { get; set; }
    public int ExecutionClass { get; set; }
    public string ProcessorVersion { get; set; } = string.Empty;
    public string InputFingerprint { get; set; } = string.Empty;
    public string? RequiredCapability { get; set; }
    public int State { get; set; }
    public string? Reason { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public string? AttemptEvidenceJson { get; set; }
    public Guid? ResultingPipelineRecordId { get; set; }
    public long? ResultingPipelineRecordRevision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceRevisionEntity SourceRevision { get; set; } = null!;
    public PipelineRecordEntity? ResultingPipelineRecord { get; set; }
}
