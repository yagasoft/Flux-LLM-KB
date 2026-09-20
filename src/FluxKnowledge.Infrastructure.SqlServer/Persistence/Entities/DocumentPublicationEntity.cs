namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// One durable successful document publication per physical source revision.
/// It deliberately selects execution identity rather than relying on completion time.
/// </summary>
public sealed class DocumentPublicationEntity
{
    public Guid OwnerSourceRevisionId { get; set; }
    public Guid DocumentInputSourceRevisionId { get; set; }
    public Guid SourceProcessorBranchId { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long PipelineRecordRevision { get; set; }
    public string ProcessorFingerprint { get; set; } = string.Empty;
    public DateTimeOffset PublishedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
