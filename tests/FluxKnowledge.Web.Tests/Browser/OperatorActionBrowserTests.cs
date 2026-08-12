using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class OperatorActionBrowserTests
{
    [BrowserFact]
    public async Task Direct_loopback_operator_can_open_the_sanitised_page_and_include_ignored_control()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOperatorActions_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOperatorActionIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString, ingressRoot, indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/operator-actions").ToString(),
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Operator actions" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByLabel("Include ignored")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Operator actions" })).ToBeVisibleAsync();
            var markup = await page.ContentAsync();
            Assert.DoesNotContain("branchId", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceRevision", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("canonicalPath", markup, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }
}
