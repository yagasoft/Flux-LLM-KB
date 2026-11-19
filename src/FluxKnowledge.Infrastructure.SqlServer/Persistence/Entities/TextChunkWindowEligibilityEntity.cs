namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>A rebuildable window decision bound to its exact canonical and chunk inputs.</summary>
public sealed class TextChunkWindowEligibilityEntity
{
    public long TextChunkId { get; set; }
    public Guid ArtifactId { get; set; }
    public long CanonicalSourceRevision { get; set; }
    public string CanonicalContentHash { get; set; } = string.Empty;
    public long CanonicalLength { get; set; }
    public long ChunkSourceRevision { get; set; }
    public string ChunkContentHash { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int Length { get; set; }
    public string RuleVersion { get; set; } = string.Empty;
    public bool Available { get; set; }
    public DateTimeOffset CalculatedAtUtc { get; set; }
    public TextChunkEntity TextChunk { get; set; } = null!;
}

public sealed class TextChunkWindowBackfillStateEntity
{
    public int Id { get; set; }
    public string RuleVersion { get; set; } = string.Empty;
    public long LastChunkId { get; set; }
    public long EndChunkId { get; set; }
    public long? RevisitFromChunkId { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
