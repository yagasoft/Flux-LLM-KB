namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Durable, root-scoped deletion progress; retained until the operation is terminal.</summary>
public sealed class SourceDeletionOperationEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public int State { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int PipelineRecordCount { get; set; }
    public int SourceArtifactCount { get; set; }
    public int SharedArtifactCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
