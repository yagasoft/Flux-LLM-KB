using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Playwright;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class SourceLifecycleBrowserTests
{
    [BrowserFact]
    public async Task Source_list_and_detail_pause_resume_controls_persist_the_root_state()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeSourceLifecycleIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeSourceLifecycleIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        await File.WriteAllTextAsync(Path.Combine(ingressRoot, "source.txt"), "source lifecycle browser coverage");
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "sources").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add folder", Exact = true }).ClickAsync();
            await page.GetByLabel("Folder path").FillAsync(ingressRoot);
            await page.GetByLabel("Display name").FillAsync("Lifecycle browser source");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await page.GetByText("durable scan request is held", new PageGetByTextOptions { Exact = false }).WaitForAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).ClickAsync();
            await page.GetByText("The source was paused.", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await using (var verification = new FluxKnowledgeDbContext(
                             new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options))
            {
                Assert.Equal(
                    (int)SourceRootState.Paused,
                    await verification.SourceRootConfigurations
                        .Where(value => value.DisplayName == "Lifecycle browser source")
                        .Select(value => value.State)
                        .SingleAsync());
            }
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var pausedPageText = await page.Locator("body").InnerTextAsync();
            Assert.Contains("Paused", pausedPageText, StringComparison.Ordinal);
            await page.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }).WaitForAsync();

            await page.GetByRole(AriaRole.Link, new() { Name = "View", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Lifecycle browser source", Exact = true }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }).ClickAsync();
            await page.GetByText("The source was resumed.", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).WaitForAsync();
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

    [BrowserFact]
    public async Task Source_list_requires_explicit_delete_confirmation_and_removes_only_the_selected_root()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeSourceDeleteIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeSourceDeleteIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot,
                sourceWorkersEnabled: true);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();
            await page.GotoAsync(new Uri(host.BaseAddress, "sources").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add folder", Exact = true }).ClickAsync();
            await page.GetByLabel("Folder path").FillAsync(ingressRoot);
            await page.GetByLabel("Display name").FillAsync("Delete browser source");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await page.GetByText("This removes its owned pipeline, index and retained app storage.", new PageGetByTextOptions { Exact = false }).WaitForAsync();
            var confirm = page.GetByRole(AriaRole.Button, new() { Name = "Confirm delete", Exact = true });
            Assert.False(await confirm.IsEnabledAsync());
            await page.GetByLabel("Type DELETE to confirm").FillAsync("DELETE");
            await confirm.ClickAsync();

            await using var verification = new FluxKnowledgeDbContext(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options);
            for (var attempt = 0; attempt < 50 && await verification.SourceRootConfigurations.AnyAsync(); attempt++)
            {
                await Task.Delay(100);
            }

            Assert.Empty(await verification.SourceRootConfigurations.ToListAsync());
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
