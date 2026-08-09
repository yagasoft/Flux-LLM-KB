using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Contracts;

public sealed record SourceRootDraft(
    string FullPath,
    string DisplayName,
    bool Recursive,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    long MaximumFileBytes,
    string RequestedBy)
{
    public static SourceRootDraft Empty { get; } = new(
        string.Empty,
        string.Empty,
        true,
        [],
        [],
        16L * 1024 * 1024,
        "local-operator");
}

public sealed record SourceRootPreview(
    string CanonicalPath,
    int MatchedFileCount,
    int PlannedInProcessCount,
    int DeferredCount,
    int BlockedCount,
    int PermissionErrorCount,
    IReadOnlyList<string> EffectiveIncludePatterns,
    IReadOnlyList<string> EffectiveExcludePatterns,
    IReadOnlyList<string> Reasons);

public sealed record SourceRootListProjection(
    Guid Id,
    string DisplayName,
    string CanonicalPath,
    string State,
    DateTimeOffset? LastScanCompletedAtUtc,
    int IndexedCount,
    int DeferredCount,
    int BlockedCount,
    int ErrorCount);

public sealed record SourceActivityReasonProjection(string State, string Reason, int Count);

public sealed record SourceRootDetailProjection(
    Guid Id,
    string DisplayName,
    string CanonicalPath,
    string State,
    string ScanState,
    DateTimeOffset? LastReconciledAtUtc,
    int DiscoveredCount,
    int IndexedCount,
    int DeferredCount,
    int BlockedCount,
    int ErrorCount,
    IReadOnlyList<SourceActivityReasonProjection> DeferredOrBlockedReasons,
    IReadOnlyList<DeferredContentReplayRequest> ReprocessableActivities)
{
    public bool CanReprocessDeferredContent => ReprocessableActivities.Count > 0;
}
