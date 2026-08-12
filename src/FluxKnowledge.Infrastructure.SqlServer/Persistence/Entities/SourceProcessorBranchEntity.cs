namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceProcessorBranchEntity
{
    public Guid Id { get; set; }
    public Guid SourceActivityId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public string InputSha256 { get; set; } = string.Empty;
    public string ProcessorVersion { get; set; } = string.Empty;
    public string ProcessorFingerprint { get; set; } = string.Empty;
    public int State { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long LeaseGeneration { get; set; }
    public int AttemptCount { get; set; }
    public string? CompletionReceiptFingerprint { get; set; }
    public int CompletedMemberCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
