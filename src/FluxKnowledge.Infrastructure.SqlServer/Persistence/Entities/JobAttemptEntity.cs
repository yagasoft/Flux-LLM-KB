namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class JobAttemptEntity
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public long LeaseGeneration { get; set; }
    public string LeaseOwner { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? Outcome { get; set; }
    public string? ErrorDetails { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public JobEntity Job { get; set; } = null!;
}
