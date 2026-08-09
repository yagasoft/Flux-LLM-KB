namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public Guid? PipelineRecordId { get; set; }
    public Guid? SourceRootId { get; set; }
    public Guid? SourceScanRequestId { get; set; }
    public Guid? SourceRevisionId { get; set; }
    public Guid? SourceActivityId { get; set; }
    public string? CorrelationId { get; set; }
    public string? EventFamily { get; set; }
    public string? Severity { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public PipelineRecordEntity? PipelineRecord { get; set; }
    public SourceRootConfigurationEntity? SourceRoot { get; set; }
    public SourceScanRequestEntity? SourceScanRequest { get; set; }
    public SourceRevisionEntity? SourceRevision { get; set; }
    public SourceActivityEntity? SourceActivity { get; set; }
}
