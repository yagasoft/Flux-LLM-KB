namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceActivityRelationEntity
{
    public Guid Id { get; set; }
    public Guid PredecessorActivityId { get; set; }
    public Guid SuccessorActivityId { get; set; }
    public string RelationshipKind { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
