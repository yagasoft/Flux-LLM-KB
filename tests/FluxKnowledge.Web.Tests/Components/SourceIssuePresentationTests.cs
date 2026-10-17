using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceIssuePresentationTests
{
    [Fact]
    public void Current_processing_groups_count_files_and_remove_a_later_resolved_item()
    {
        SourceFileProjection[] files = [
            new("a.jpg", "images/a.jpg", "Image", "Failed", "document-ocr-runtime-failed", null),
            new("b.jpg", "images/b.jpg", "Image", "Failed", "document-ocr-runtime-failed", null),
            new("c.pdf", "c.pdf", "PDF", "Blocked", "pdf-document-encrypted", null),
            new("d.txt", "d.txt", "Text", "Indexed", "old-failure-must-not-reappear", Guid.NewGuid())];

        var before = SourceIssuePresentation.GroupCurrentFiles(files);
        Assert.Equal(2, Assert.Single(before, group => group.Reason == "document-ocr-runtime-failed").Count);
        Assert.Equal(["images/a.jpg", "images/b.jpg"], Assert.Single(before, group => group.Reason == "document-ocr-runtime-failed").AffectedPaths);
        files[0] = files[0] with { Status = "Indexed", Reason = null };
        var after = SourceIssuePresentation.GroupCurrentFiles(files);
        Assert.Equal(1, Assert.Single(after, group => group.Reason == "document-ocr-runtime-failed").Count);
        Assert.DoesNotContain(after.SelectMany(group => group.AffectedPaths), path => path is "images/a.jpg" or "d.txt");
        Assert.Single(after, group => group.Reason == "pdf-document-encrypted");
    }

    [Fact]
    public void Old_scan_samples_do_not_claim_complete_cause_counts_and_a_clean_scan_clears_them()
    {
        const string oldAudit = "{\"enumerationErrors\":[{\"Kind\":\"permission\",\"RelativePath\":\"private/item.txt\",\"Detail\":\"SourcePathDenied\"}]}";
        var summary = SourceScanIssueSummary.FromAudit(oldAudit, 25000);
        Assert.False(summary.CauseCountsComplete);
        Assert.Equal(25000, summary.ReportedCount);
        Assert.Equal(1, Assert.Single(summary.Groups).Count);
        Assert.Empty(SourceScanIssueSummary.FromAudit(oldAudit, 0).Groups);
        Assert.Empty(SourceScanIssueSummary.FromAudit("{bad-json", 2).Groups);
    }

    [Fact]
    public void Source_issue_item_paths_remain_local_UI_only_in_both_JSON_formats()
    {
        var reason = new SourceActivityReasonProjection("Failed", "document-ocr-runtime-failed", 1) { AffectedPaths = ["private-ocr-item.jpg"] };
        var detail = new SourceRootDetailProjection(Guid.NewGuid(), "Test", "E:\\Test", "Enabled", "Completed", null, 1, 0, 0, 0, 1, [reason], [])
        {
            CurrentProcessingIssues = [reason],
            ScanIssues = SourceScanIssueSummary.Create([new("permission", "private-scan-item.txt", "SourceAccessDenied")])
        };
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            var json = JsonSerializer.Serialize(detail, options);
            Assert.DoesNotContain("private-ocr-item", json);
            Assert.DoesNotContain("private-scan-item", json);
        }
    }

    [Fact]
    public async Task Rendered_issue_panel_names_causes_items_and_next_actions_with_truthful_sample_coverage()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<SourceIssueSummaryPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ScanIssues"] = new SourceScanIssueSummary(25000, false, [new("permission", "SourcePathDenied", 100, 100, ["private/item.txt"])]),
                ["ProcessingIssues"] = new[] { new SourceActivityReasonProjection("Failed", "pdf-document-encrypted", 1) { AffectedPaths = ["locked.pdf"] } }
            }));
            return result.ToHtmlString();
        });
        Assert.Contains("Current processing issues", html);
        Assert.Contains("locked.pdf", html);
        Assert.Contains("authorised unlocked copy", html);
        Assert.Contains("sample counts", html);
        Assert.Contains("private/item.txt", html);
        Assert.Contains("25000", html);
        Assert.Contains("<details", html);
    }
}
