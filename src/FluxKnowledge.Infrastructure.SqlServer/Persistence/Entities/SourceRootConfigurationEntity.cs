namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceRootConfigurationEntity
{
    public Guid Id { get; set; }
    public string CanonicalPath { get; set; } = string.Empty;
    public string CanonicalPathFingerprint { get; private set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int State { get; set; }
    public bool Recursive { get; set; }
    public string IncludePatternsJson { get; set; } = "[]";
    public string ExcludePatternsJson { get; set; } = "[]";
    public bool FollowLinks { get; set; }
    public long MaximumFileBytes { get; set; }
    public string AllowedClassificationsJson { get; set; } = "[]";
    public int CrawlMode { get; set; }
    public long ReconciliationCadenceSeconds { get; set; }
    public DateTimeOffset? LastScanStartedAtUtc { get; set; }
    public DateTimeOffset? LastScanCompletedAtUtc { get; set; }
    public string? LastScanEvidenceJson { get; set; }
    public string? PermissionEvidenceJson { get; set; }
    public string? HealthEvidenceJson { get; set; }
    public long ConfigurationRevision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
