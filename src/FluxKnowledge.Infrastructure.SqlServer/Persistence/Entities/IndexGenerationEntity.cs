namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class IndexGenerationEntity
{
    public Guid Id { get; set; }
    public string ModelFingerprint { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string IndexPath { get; set; } = string.Empty;
    public string MetadataChecksum { get; set; } = string.Empty;
    public long VectorCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ValidatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
