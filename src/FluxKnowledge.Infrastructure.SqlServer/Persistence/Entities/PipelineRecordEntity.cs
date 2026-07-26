namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class PipelineRecordEntity
{
    public Guid Id { get; set; }
    public Guid SourceIdentityId { get; set; }
    public long Revision { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public Guid RootLineageRecordId { get; set; }
    public Guid? ParentRevisionRecordId { get; set; }
    public int CurrentStage { get; set; }
    public bool CompletionCriteriaMet { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceIdentityEntity SourceIdentity { get; set; } = null!;
    public PipelineRecordEntity? ParentRevisionRecord { get; set; }
}
