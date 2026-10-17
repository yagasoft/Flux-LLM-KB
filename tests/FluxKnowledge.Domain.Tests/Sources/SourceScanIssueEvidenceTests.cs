using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceScanIssueEvidenceTests
{
    [Fact]
    public void Scan_audit_retains_counts_for_causes_beyond_the_first_hundred_samples()
    {
        var evidence = Enumerable.Range(0, 125).Select(index =>
                new SourceEnumerationEvidence("permission", $"folder/item{index}.txt", "SourceAccessDenied"))
            .Concat([new("security", "link.txt", "SourceReparseTypeNotSupported"),
                new("unavailable", "offline.txt", "SourceContentNotAvailableLocally")]).ToArray();

        using var audit = JsonDocument.Parse(SqlSourceScanStore.MergeEvidence("{\"control\":\"preserved\"}", evidence));

        Assert.Equal("preserved", audit.RootElement.GetProperty("control").GetString());
        Assert.Equal(100, audit.RootElement.GetProperty("enumerationErrors").GetArrayLength());
        var summary = audit.RootElement.GetProperty("enumerationIssueSummary");
        Assert.Equal(127, summary.GetProperty("ReportedCount").GetInt32());
        Assert.True(summary.GetProperty("CauseCountsComplete").GetBoolean());
        var groups = summary.GetProperty("Groups").EnumerateArray().ToArray();
        Assert.Equal(3, groups.Length);
        var denied = Assert.Single(groups, group => group.GetProperty("Reason").GetString() == "SourceAccessDenied");
        Assert.Equal(125, denied.GetProperty("Count").GetInt32());
        Assert.Equal(125, denied.GetProperty("AffectedPathCount").GetInt32());
        Assert.Equal(10, denied.GetProperty("SamplePaths").GetArrayLength());
        Assert.False(audit.RootElement.GetProperty("enumerationComplete").GetBoolean());
    }

    [Fact]
    public void Replacing_scan_evidence_removes_resolved_causes_without_removing_other_audit_fields()
    {
        var previous = SqlSourceScanStore.MergeEvidence("{\"leaseEvidence\":true}",
            [new("permission", "resolved.txt", "SourceAccessDenied")]);

        using var current = JsonDocument.Parse(SqlSourceScanStore.MergeEvidence(previous, []));

        Assert.True(current.RootElement.GetProperty("leaseEvidence").GetBoolean());
        Assert.True(current.RootElement.GetProperty("enumerationComplete").GetBoolean());
        Assert.Empty(current.RootElement.GetProperty("enumerationErrors").EnumerateArray());
        var summary = current.RootElement.GetProperty("enumerationIssueSummary");
        Assert.Equal(0, summary.GetProperty("ReportedCount").GetInt32());
        Assert.Empty(summary.GetProperty("Groups").EnumerateArray());
    }

    [Fact]
    public void Multiple_reports_for_one_path_do_not_become_multiple_affected_items()
    {
        using var audit = JsonDocument.Parse(SqlSourceScanStore.MergeEvidence(null,
            [new("partial", ".", "CloudDirectoryNamespaceIncomplete"),
                new("partial", ".", "CloudDirectoryNamespaceIncomplete")]));

        var group = Assert.Single(audit.RootElement.GetProperty("enumerationIssueSummary").GetProperty("Groups").EnumerateArray());
        Assert.Equal(2, group.GetProperty("Count").GetInt32());
        Assert.Equal(1, group.GetProperty("AffectedPathCount").GetInt32());
        Assert.Equal(".", Assert.Single(group.GetProperty("SamplePaths").EnumerateArray()).GetString());
    }

    [Fact]
    public void A_later_scan_removes_the_resolved_item_and_keeps_an_unresolved_sibling()
    {
        var previous = SqlSourceScanStore.MergeEvidence(null,
            [new("permission", "resolved.txt", "SourceAccessDenied"), new("permission", "remaining.txt", "SourceAccessDenied")]);
        using var current = JsonDocument.Parse(SqlSourceScanStore.MergeEvidence(previous,
            [new("permission", "remaining.txt", "SourceAccessDenied")]));

        var group = Assert.Single(current.RootElement.GetProperty("enumerationIssueSummary").GetProperty("Groups").EnumerateArray());
        Assert.Equal(1, group.GetProperty("AffectedPathCount").GetInt32());
        Assert.Equal("remaining.txt", Assert.Single(group.GetProperty("SamplePaths").EnumerateArray()).GetString());
        Assert.DoesNotContain("resolved.txt", current.RootElement.GetRawText());
    }
}
