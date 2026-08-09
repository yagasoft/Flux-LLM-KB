namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceRevisionEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public string StableSourceIdentity { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string CanonicalPath { get; set; } = string.Empty;
    public string CanonicalPathFingerprint { get; private set; } = string.Empty;
    public Guid? ParentSourceRevisionId { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public DateTimeOffset? FileCreatedAtUtc { get; set; }
    public DateTimeOffset? FileLastWriteAtUtc { get; set; }
    public DateTimeOffset DiscoveredAtUtc { get; set; }
    public string? DiscoveryEvidenceJson { get; set; }
    public DateTimeOffset? SuppressedAtUtc { get; set; }
    public DateTimeOffset? RetainUntilUtc { get; set; }
    public string? RetentionEvidenceJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceRootConfigurationEntity SourceRoot { get; set; } = null!;
    public SourceRevisionEntity? ParentSourceRevision { get; set; }
}
