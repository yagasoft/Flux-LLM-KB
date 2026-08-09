namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceScanRequestEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public int RequestKind { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public bool IsReleased { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
    public int State { get; set; }
    public int DiscoveredFileCount { get; set; }
    public int IndexedFileCount { get; set; }
    public int DeferredFileCount { get; set; }
    public int BlockedFileCount { get; set; }
    public int ErrorFileCount { get; set; }
    public string? AuditEvidenceJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceRootConfigurationEntity SourceRoot { get; set; } = null!;
}
