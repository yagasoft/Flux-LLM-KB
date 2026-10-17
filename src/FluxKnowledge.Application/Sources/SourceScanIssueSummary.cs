using System.Text.Json;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Sources;

public sealed record SourceScanIssueGroup(string Kind, string Reason, int Count, int AffectedPathCount, IReadOnlyList<string> SamplePaths)
{
    public int? NativeErrorCode { get; init; }
}

/// <summary>Counts every cause while retaining a bounded set of complete, local-operator path samples.</summary>
public sealed record SourceScanIssueSummary(int ReportedCount, bool CauseCountsComplete, IReadOnlyList<SourceScanIssueGroup> Groups)
{
    private const int MaximumGroups = 64;
    private const int MaximumSamplePaths = 10;
    private const int MaximumSamplePathLength = 2048;
    public static SourceScanIssueSummary Empty { get; } = new(0, true, []);

    public static SourceScanIssueSummary Create(IReadOnlyList<SourceEnumerationEvidence> evidence)
    {
        var causes = evidence.GroupBy(issue => (issue.Kind, issue.Detail, issue.NativeErrorCode))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Detail, StringComparer.Ordinal).ToArray();
        var groups = causes.Take(MaximumGroups).Select(group =>
        {
            var paths = group.Select(issue => issue.RelativePath).Distinct(StringComparer.Ordinal).ToArray();
            return new SourceScanIssueGroup(group.Key.Kind, group.Key.Detail, group.Count(), paths.Length,
                paths.Where(path => path.Length <= MaximumSamplePathLength).Order(StringComparer.Ordinal).Take(MaximumSamplePaths).ToArray())
                { NativeErrorCode = group.Key.NativeErrorCode };
        }).ToArray();
        return new(evidence.Count, causes.Length <= MaximumGroups, groups);
    }

    public static SourceScanIssueSummary FromAudit(string? auditJson, int reportedCount)
    {
        if (reportedCount == 0) return Empty; // A clean later scan must not resurrect old evidence.
        var missing = new SourceScanIssueSummary(reportedCount, false, []);
        if (string.IsNullOrWhiteSpace(auditJson) || auditJson.Length > 8 * 1024 * 1024) return missing;
        try
        {
            using var audit = JsonDocument.Parse(auditJson);
            if (audit.RootElement.TryGetProperty("enumerationIssueSummary", out var stored))
            {
                var summary = stored.Deserialize<SourceScanIssueSummary>();
                if (summary is null || summary.ReportedCount != reportedCount || summary.Groups is null || summary.Groups.Count > MaximumGroups ||
                    summary.Groups.Any(group => group is null || string.IsNullOrWhiteSpace(group.Kind) || string.IsNullOrWhiteSpace(group.Reason) ||
                        group.Count <= 0 || group.AffectedPathCount <= 0 || group.AffectedPathCount > group.Count || group.SamplePaths is null ||
                        group.SamplePaths.Count > Math.Min(MaximumSamplePaths, group.AffectedPathCount) ||
                        group.SamplePaths.Any(path => path is null || path.Length > MaximumSamplePathLength))) return missing;
                var total = summary.Groups.Sum(group => (long)group.Count);
                if (total > reportedCount || summary.CauseCountsComplete && total != reportedCount) return missing;
                return summary;
            }
            // Old releases saved only the first hundred reports. Their counts are samples, never a full inventory.
            if (!audit.RootElement.TryGetProperty("enumerationErrors", out var samples)) return missing;
            var evidence = samples.Deserialize<SourceEnumerationEvidence[]>();
            if (evidence is null || evidence.Length > 100 || evidence.Length > reportedCount ||
                evidence.Any(issue => issue is null || issue.Kind is null || issue.Detail is null || issue.RelativePath is null)) return missing;
            return Create(evidence) with { ReportedCount = reportedCount, CauseCountsComplete = evidence.Length == reportedCount };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            return missing;
        }
    }
}
