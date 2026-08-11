namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Private durable export identity; no MIME, attachment, credentials or raw diagnostics are retained.</summary>
public sealed class OutlookCaptureExportEntity
{
    public Guid Id { get; set; }
    public Guid? ProfileId { get; set; }
    public Guid? FolderId { get; set; }
    public Guid? CatchUpId { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string EntryIdFingerprint { get; private set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string? ManifestHash { get; set; }
    public string? RelativeSpoolPath { get; set; }
    public int State { get; set; }
    public string? BlockedReasonCode { get; set; }
    public Guid? SourceRevisionId { get; set; }
    public long FencingToken { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
