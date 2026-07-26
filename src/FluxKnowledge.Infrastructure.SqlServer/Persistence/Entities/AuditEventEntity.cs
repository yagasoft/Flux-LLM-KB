namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public Guid? PipelineRecordId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public PipelineRecordEntity? PipelineRecord { get; set; }
}
