namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Permanent evidence of a dispatch returning the native model to its resident owner.</summary>
public sealed class GpuModelDetachmentReceiptEntity
{
    public Guid DispatchId { get; set; }
    public Guid ResidencyId { get; set; }
    public long Version { get; set; }
    public Guid BatchId { get; set; }
    public long AdmissionGeneration { get; set; }
    public string ExecutorKey { get; set; } = string.Empty;
    public Guid ExecutionInstanceId { get; set; }
    public DateTimeOffset DetachedAtUtc { get; set; }
}
