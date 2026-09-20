namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Private, source-bound OCR hand-off state.  The result is never a corpus entry: once the
/// original Extract job is safely requeued it is consumed through the normal stage transition.
/// </summary>
public sealed class DocumentOcrRequestEntity
{
    public Guid MiniTaskId { get; set; }
    public Guid ParentJobId { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public Guid RetainedSourceRevisionId { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string RequestedPageIndexesJson { get; set; } = string.Empty;
    public string ModelRuntimeKey { get; set; } = string.Empty;
    public string SettingsFingerprint { get; set; } = string.Empty;
    public int State { get; set; }
    public string? ResultJson { get; set; }
    public byte[]? ResultDigest { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
