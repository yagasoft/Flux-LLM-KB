namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class IndexStateEntity
{
    public int Id { get; set; }
    public Guid CorpusEpoch { get; set; }
    public Guid? CorpusRebuildOperationId { get; set; }
    public long CorpusVersion { get; set; }
    public Guid? ActiveIndexGenerationId { get; set; }
    public DateTimeOffset? EmptyCatalogueValidatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public IndexGenerationEntity? ActiveIndexGeneration { get; set; }
}
