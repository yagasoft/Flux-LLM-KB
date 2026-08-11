namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Private canonical COM folder identity used exclusively for reconciliation.</summary>
public sealed class OutlookCaptureFolderEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string StoreId { get; set; } = string.Empty;
    public string FolderEntryId { get; set; } = string.Empty;
    public string CanonicalIdentityFingerprint { get; private set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Basis { get; set; }
    public DateTimeOffset? CursorUtc { get; set; }
    public string? CursorFingerprint { get; set; }
    public int State { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
