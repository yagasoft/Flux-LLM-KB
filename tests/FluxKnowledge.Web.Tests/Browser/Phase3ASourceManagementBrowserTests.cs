using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class Phase3ASourceManagementBrowserTests
{
    [BrowserFact]
    public async Task Sources_navigation_exposes_the_local_add_folder_operator_surface()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgePhase3ASourcesIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgePhase3ASourcesIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(host.BaseAddress.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Link, new() { Name = "Sources and indexing" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Sources and indexing" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Add folder", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Add folder" }).WaitForAsync();
            await page.GetByLabel("Folder path").FillAsync(ingressRoot);
            await page.GetByLabel("Display name").FillAsync("Browser source root");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Read-only preview" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await page.GetByText("durable scan request is held", new PageGetByTextOptions { Exact = false }).WaitForAsync();
        }
        finally
        {
            if (Directory.Exists(ingressRoot))
            {
                Directory.Delete(ingressRoot, recursive: true);
            }

            if (Directory.Exists(indexRoot))
            {
                Directory.Delete(indexRoot, recursive: true);
            }
        }
    }
}
