namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class IndexGenerationVectorEntity
{
    public Guid GenerationId { get; set; }
    public long VectorId { get; set; }
    public IndexGenerationEntity Generation { get; set; } = null!;
    public VectorEntity Vector { get; set; } = null!;
}
