namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>Private Outlook configuration; the spool root must never be projected outside this store.</summary>
public sealed class OutlookCaptureProfileEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string SpoolRoot { get; set; } = string.Empty;
    public int IncrementalBasis { get; set; }
    public int State { get; set; }
    public bool IsEnabled { get; set; }
    public long ConfigurationRevision { get; set; }
    public long CadenceTicks { get; set; }
    public long MaximumOverlapTicks { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
