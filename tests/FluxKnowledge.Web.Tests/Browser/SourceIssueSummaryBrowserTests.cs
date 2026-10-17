using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class SourceIssueSummaryBrowserTests
{
    [BrowserFact]
    public async Task Grouped_causes_expand_to_items_and_actions_at_desktop_and_mobile_widths()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<SourceIssueSummaryPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ScanIssues"] = new SourceScanIssueSummary(25000, false, [new("permission", "SourcePathDenied", 100, 100, ["documents/older-scan-sample.docx"])]),
                ["ProcessingIssues"] = new[]
                {
                    new SourceActivityReasonProjection("Failed", "document-ocr-runtime-failed", 2) { AffectedPaths = ["images/a.jpg", "images/b.jpg"] },
                    new SourceActivityReasonProjection("Blocked", "pdf-document-encrypted", 1) { AffectedPaths = ["documents/locked.pdf"] }
                }
            }));
            return result.ToHtmlString();
        });
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var css = await File.ReadAllTextAsync(Path.Combine(repository.FullName, "src", "FluxKnowledge.Web", "wwwroot", "css", "app.css"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
        await page.SetContentAsync($"<!doctype html><html><head><title>Source issues fixture</title><style>{css}</style></head><body><main>{markup}</main></body></html>");
        Assert.Equal("Source issues fixture", await page.TitleAsync());
        Assert.True(await page.GetByRole(AriaRole.Heading, new() { Name = "Current processing issues" }).IsVisibleAsync());
        Assert.False(await page.GetByText("images/a.jpg", new() { Exact = true }).IsVisibleAsync());
        await page.GetByText("Failed: document-ocr-runtime-failed (2 items)", new() { Exact = true }).ClickAsync();
        Assert.True(await page.GetByText("images/a.jpg", new() { Exact = true }).IsVisibleAsync());
        await page.GetByText("Blocked: pdf-document-encrypted (1 items)", new() { Exact = true }).ClickAsync();
        Assert.Contains("authorised unlocked copy", await page.Locator("body").InnerTextAsync());
        Assert.Contains("sample counts", await page.Locator("body").InnerTextAsync());
        Assert.Empty(errors);
        var evidence = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_EVIDENCE_ROOT");
        if (!string.IsNullOrWhiteSpace(evidence)) await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "source-issues-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.GetByText("images/a.jpg", new() { Exact = true }).IsVisibleAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        if (!string.IsNullOrWhiteSpace(evidence)) await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "source-issues-mobile.png"), FullPage = true });
    }
}
