namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceProcessorBranchMemberEntity
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public string MemberFingerprint { get; set; } = string.Empty;
    public Guid? ChildSourceRevisionId { get; set; }
    public Guid? ChildSourceActivityId { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public long ByteLength { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
