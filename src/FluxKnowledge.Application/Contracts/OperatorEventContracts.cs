using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Application.Contracts;

public sealed record OperatorEventFilters(
    string? Family = null,
    string? Severity = null,
    Guid? SourceRootId = null,
    Guid? PipelineRecordId = null,
    Guid? SourceRevisionId = null,
    string? CorrelationId = null,
    DateTimeOffset? OccurredFromUtc = null,
    DateTimeOffset? OccurredToUtc = null)
{
    public OperatorEventFilters Normalised() => this with { Family = NormaliseValue(Family), Severity = NormaliseValue(Severity), CorrelationId = NormaliseValue(CorrelationId) };
    public string ToCanonicalString() => string.Join("&",
        $"family={Normalise(Family)}",
        $"severity={Normalise(Severity)}",
        $"sourceRootId={SourceRootId?.ToString("D") ?? string.Empty}",
        $"pipelineRecordId={PipelineRecordId?.ToString("D") ?? string.Empty}",
        $"sourceRevisionId={SourceRevisionId?.ToString("D") ?? string.Empty}",
        $"correlationId={Normalise(CorrelationId)}",
        $"occurredFromUtc={OccurredFromUtc?.ToUniversalTime().ToString("O") ?? string.Empty}",
        $"occurredToUtc={OccurredToUtc?.ToUniversalTime().ToString("O") ?? string.Empty}");

    private static string Normalise(string? value) => Uri.EscapeDataString(NormaliseValue(value) ?? string.Empty);
    public static string? NormaliseValue(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

public static class OperatorEventFilterFingerprint
{
    public static string Compute(string canonicalFilter)
    {
        ArgumentNullException.ThrowIfNull(canonicalFilter);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalFilter)));
    }
}

public sealed record OperatorEventCursor(DateTimeOffset OccurredAtUtc, long EventId, string FilterFingerprint)
{
    public static OperatorEventCursor Create(DateTimeOffset occurredAtUtc, long eventId, string canonicalFilter) =>
        new(occurredAtUtc, eventId, OperatorEventFilterFingerprint.Compute(canonicalFilter));

    public void ValidateFor(string canonicalFilter)
    {
        if (!string.Equals(FilterFingerprint, OperatorEventFilterFingerprint.Compute(canonicalFilter), StringComparison.Ordinal))
        {
            throw new ArgumentException("The cursor does not match the current filters.", nameof(canonicalFilter));
        }
    }
}

public sealed record OperatorEventQuery
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;

    public OperatorEventQuery(OperatorEventFilters? Filters = null, int PageSize = DefaultPageSize, OperatorEventCursor? Cursor = null)
    {
        this.Filters = (Filters ?? new OperatorEventFilters()).Normalised();
        this.PageSize = Math.Clamp(PageSize, 1, MaximumPageSize);
        this.Cursor = Cursor;
        Cursor?.ValidateFor(CanonicalFilter);
    }

    public OperatorEventFilters Filters { get; }
    public int PageSize { get; }
    public OperatorEventCursor? Cursor { get; }
    public string CanonicalFilter => Filters.ToCanonicalString();
}

public sealed record OperatorEventEntry(
    long Id,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string Family,
    string Severity,
    string Message,
    Guid? PipelineRecordId,
    Guid? SourceRootId,
    Guid? SourceRevisionId,
    Guid? SourceActivityId,
    Guid? SourceScanRequestId,
    string? CorrelationId,
    string Details);

public sealed record OperatorEventPage(IReadOnlyList<OperatorEventEntry> Items, OperatorEventCursor? NextCursor);
