namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class OutlookBrowseRequestEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public Guid CorrelationId { get; set; }
    public long ConfigurationRevision { get; set; }
    public int State { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long FencingToken { get; set; }
    public int? FailureCode { get; set; }
    /// <summary>Private root-to-leaf display path; never returned by status or public reads.</summary>
    public string? TargetPath { get; set; }
    /// <summary>Private SHA-256 provenance for a completed targeted browse; the raw path is cleared terminally.</summary>
    public string? TargetPathFingerprint { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class OutlookBrowseResultEntity
{
    public Guid Id { get; set; }
    public Guid BrowseRequestId { get; set; }
    public Guid FolderId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class OutlookCatchUpEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string CoalescingKey { get; set; } = string.Empty;
    public int Provenance { get; set; }
    public int State { get; set; }
    public int RetryCount { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public long FencingToken { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Immutable, private retained-artifact capability evidence. No original watched path or Outlook handle is stored.</summary>
public sealed class DeferredCapabilityEntity
{
    public Guid Id { get; set; }
    public Guid SourceRevisionId { get; set; }
    public string ArtifactFingerprint { get; set; } = string.Empty;
    public string RequiredCapability { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public string? ClaimedProcessorVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
