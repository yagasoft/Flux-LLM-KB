namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class ArtifactEntity
{
    public Guid Id { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public int Stage { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public PipelineRecordEntity PipelineRecord { get; set; } = null!;
}
