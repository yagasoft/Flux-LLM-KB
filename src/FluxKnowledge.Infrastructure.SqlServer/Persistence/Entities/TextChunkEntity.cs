namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class TextChunkEntity
{
    public long Id { get; set; }
    public Guid ArtifactId { get; set; }
    public long SourceRevision { get; set; }
    public int Ordinal { get; set; }
    public int StartOffset { get; set; }
    public int Length { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public ArtifactEntity Artifact { get; set; } = null!;
}
