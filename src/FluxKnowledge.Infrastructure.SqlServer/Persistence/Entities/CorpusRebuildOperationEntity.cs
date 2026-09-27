namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class CorpusRebuildOperationEntity
{
    public Guid Id { get; set; }
    public Guid TargetEpoch { get; set; }
    public string ManifestHash { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Guid? SupersedesOperationId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Historical jobs retain their real state; this receipt permanently revokes execution authority.</summary>
public sealed class CorpusRebuildSupersededJobEntity
{
    public Guid JobId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ReplacementOperationId { get; set; }
    public DateTimeOffset SupersededAtUtc { get; set; }
}

public sealed class CorpusRebuildWorkItemEntity
{
    public Guid OperationId { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public Guid CanonicalArtifactId { get; set; }
    public Guid EmbeddingJobId { get; set; }
    public Guid DispatchMessageId { get; set; }
    public int State { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
