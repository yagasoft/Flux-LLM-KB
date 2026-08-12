namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceProcessorAttemptEntity
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string? OutcomeCode { get; set; }
    public string? EvidenceJson { get; set; }
}
