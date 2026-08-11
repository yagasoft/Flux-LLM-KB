namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Private immutable operation receipt used to fail closed on divergent retries.</summary>
public sealed class OutlookCaptureOperationEntity
{
    public Guid Id { get; set; }
    public Guid? ProfileId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid? ResourceId { get; set; }
    public bool Accepted { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
