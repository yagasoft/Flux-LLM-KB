namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceArtifactEntity
{
    public Guid Id { get; set; }
    public Guid SourceRevisionId { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string StoreRelativePath { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public DateTimeOffset ChecksumVerifiedAtUtc { get; set; }
    public DateTimeOffset? RetainUntilUtc { get; set; }
    public long ReferenceCount { get; set; }
    public string? RetentionEvidenceJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceRevisionEntity SourceRevision { get; set; } = null!;
}
