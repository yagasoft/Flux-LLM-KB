namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>One durable background attempt; canonical text is never duplicated here.</summary>
public sealed class EmbeddingGpuRequestEntity
{
    public Guid MiniTaskId { get; set; }
    public Guid ParentJobId { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public Guid GenerationId { get; set; }
    public Guid CorpusEpoch { get; set; }
    public string ModelFingerprint { get; set; } = "";
    public int Dimensions { get; set; }
    public string InputsJson { get; set; } = "";
    public string InputDigest { get; set; } = "";
    public int State { get; set; }
    public Guid? ExecutorInstanceId { get; set; }
    public Guid? ClaimOperationId { get; set; }
    public int? OwnerProcessId { get; set; }
    public DateTimeOffset? OwnerStartedAtUtc { get; set; }
    public string? OwnerMachineFingerprint { get; set; }
    public Guid? DispatchId { get; set; }
    public bool NativeCleanupConfirmed { get; set; }
    public DateTimeOffset? CleanupConfirmedAtUtc { get; set; }
    public byte[]? ResultDigest { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
