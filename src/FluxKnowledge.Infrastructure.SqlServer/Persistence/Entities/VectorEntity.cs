namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Stores IEEE 754 single-precision vector values as consecutive little-endian
/// four-byte values in <see cref="Values"/>.
/// </summary>
public sealed class VectorEntity
{
    public long VectorId { get; set; }
    public long TextChunkId { get; set; }
    public string ModelFingerprint { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public byte[] Values { get; set; } = [];
    public string ContentHash { get; set; } = string.Empty;
    public long SourceRevision { get; set; }
    public bool IsDeleted { get; set; }
    public Guid IndexGenerationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public TextChunkEntity TextChunk { get; set; } = null!;
    public IndexGenerationEntity IndexGeneration { get; set; } = null!;
}
