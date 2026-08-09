using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Application.Contracts;

public sealed record CorpusFilters(
    string? Search = null,
    string? SourceKind = null,
    Guid? SourceRootId = null,
    string? Folder = null,
    string? SourceClassification = null,
    string? PipelineStatus = null,
    string? SourceActivityStatus = null,
    DateTimeOffset? UpdatedFromUtc = null,
    DateTimeOffset? UpdatedToUtc = null)
{
    public CorpusFilters Normalised() => this with
    {
        Search = NormaliseValue(Search), SourceKind = NormaliseValue(SourceKind), Folder = NormaliseFolder(Folder),
        SourceClassification = NormaliseValue(SourceClassification), PipelineStatus = NormaliseValue(PipelineStatus), SourceActivityStatus = NormaliseValue(SourceActivityStatus)
    };
    public string ToCanonicalString() => string.Join("&",
        $"search={Normalise(Search)}",
        $"sourceKind={Normalise(SourceKind)}",
        $"sourceRootId={SourceRootId?.ToString("D") ?? string.Empty}",
        $"folder={Normalise(Folder)}",
        $"sourceClassification={Normalise(SourceClassification)}",
        $"pipelineStatus={Normalise(PipelineStatus)}",
        $"sourceActivityStatus={Normalise(SourceActivityStatus)}",
        $"updatedFromUtc={UpdatedFromUtc?.ToUniversalTime().ToString("O") ?? string.Empty}",
        $"updatedToUtc={UpdatedToUtc?.ToUniversalTime().ToString("O") ?? string.Empty}");

    private static string Normalise(string? value) => Uri.EscapeDataString(NormaliseValue(value) ?? string.Empty);
    public static string? NormaliseValue(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    public static string? NormaliseFolder(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('\\', '/').Replace('/', '\\').ToLowerInvariant();
}

public static class CorpusFilterFingerprint
{
    public static string Compute(string canonicalFilter)
    {
        ArgumentNullException.ThrowIfNull(canonicalFilter);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalFilter)));
    }
}

public sealed record CorpusCursor(DateTimeOffset LastActivityAtUtc, Guid PipelineRecordId, string FilterFingerprint)
{
    public static CorpusCursor Create(DateTimeOffset lastActivityAtUtc, Guid pipelineRecordId, string canonicalFilter) =>
        new(lastActivityAtUtc, pipelineRecordId, CorpusFilterFingerprint.Compute(canonicalFilter));

    public void ValidateFor(string canonicalFilter)
    {
        if (!string.Equals(FilterFingerprint, CorpusFilterFingerprint.Compute(canonicalFilter), StringComparison.Ordinal))
        {
            throw new ArgumentException("The cursor does not match the current filters.", nameof(canonicalFilter));
        }
    }
}

public sealed record CorpusQuery
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;

    public CorpusQuery(CorpusFilters? Filters = null, bool IncludeHistorical = false, int PageSize = DefaultPageSize, CorpusCursor? Cursor = null)
    {
        this.Filters = (Filters ?? new CorpusFilters()).Normalised();
        if (this.Filters.PipelineStatus is { } pipelineStatus && !Enum.TryParse<FluxKnowledge.Domain.Pipeline.PipelineStage>(pipelineStatus, true, out _))
            throw new ArgumentException("The pipeline status filter is not recognised.", nameof(Filters));
        if (this.Filters.SourceActivityStatus is { } activityStatus && activityStatus is not ("indexed" or "pending" or "deferred" or "blocked" or "failed"))
            throw new ArgumentException("The source activity status filter is not recognised.", nameof(Filters));
        this.IncludeHistorical = IncludeHistorical;
        this.PageSize = Math.Clamp(PageSize, 1, MaximumPageSize);
        this.Cursor = Cursor;
        Cursor?.ValidateFor(CanonicalFilter);
    }

    public CorpusFilters Filters { get; }
    public bool IncludeHistorical { get; }
    public int PageSize { get; }
    public CorpusCursor? Cursor { get; }
    public string CanonicalFilter => $"history={IncludeHistorical.ToString().ToLowerInvariant()}&{Filters.ToCanonicalString()}";
}

public sealed record CorpusEntry(
    Guid PipelineRecordId,
    string Entry,
    string SourceKind,
    string? SourceClassification,
    string Location,
    string PipelineStatus,
    string SourceActivityState,
    DateTimeOffset LastActivityAtUtc,
    Guid? SourceRootId,
    Guid? SourceRevisionId,
    Guid? ResultingPipelineRecordId);

public sealed record CorpusPage(IReadOnlyList<CorpusEntry> Items, CorpusCursor? NextCursor);

public sealed record CorpusFolder(
    Guid SourceRootId,
    string RelativePath,
    int CurrentCount,
    int DeferredCount,
    int BlockedCount,
    int FailedCount);

public sealed record CorpusEntryDetail(
    Guid PipelineRecordId,
    string Entry,
    string SourceKind,
    string PipelineStatus,
    string SourceActivityState,
    string? DeferredOrFailureReason,
    string? IndexedTextPreview,
    Guid? SourceRootId,
    Guid? SourceRevisionId,
    string? SourceIdentity,
    string? Checksum,
    IReadOnlyList<string> PipelineActivity,
    IReadOnlyList<long> RelatedEventIds,
    CorpusLineage Lineage,
    IReadOnlyList<CorpusActivityEvidence> SourceActivities,
    IReadOnlyList<CorpusEventEvidence> RelatedEvents);

public sealed record CorpusLineage(Guid RootPipelineRecordId, Guid? ParentPipelineRecordId, Guid? ParentSourceRevisionId);
public sealed record CorpusActivityEvidence(Guid Id, string State, string? Reason, Guid? ResultingPipelineRecordId, DateTimeOffset UpdatedAtUtc);
public sealed record CorpusEventEvidence(long Id, DateTimeOffset OccurredAtUtc, string EventType, string Details);
