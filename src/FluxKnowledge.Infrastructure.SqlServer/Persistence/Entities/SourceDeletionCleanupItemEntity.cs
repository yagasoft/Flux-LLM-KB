namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// One exact app-owned physical target retained across the SQL/file cutover of a
/// source deletion. It stores metadata only; source bytes never enter this table.
/// </summary>
public sealed class SourceDeletionCleanupItemEntity
{
    public Guid Id { get; set; }
    public Guid SourceDeletionOperationId { get; set; }
    public int StorageKind { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? ContentSha256 { get; set; }
    public long? ByteLength { get; set; }
    public int State { get; set; }
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceDeletionOperationEntity SourceDeletionOperation { get; set; } = null!;
}
