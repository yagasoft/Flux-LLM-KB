using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class SourceVerificationBrowserTests
{
    [BrowserFact]
    public async Task Existing_source_page_explains_metadata_policy_and_only_queues_full_content_when_explicitly_requested()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ground = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", $"VerificationBrowser-{Guid.NewGuid():N}");
        var source = Path.Combine(ground, "Source");
        var index = Path.Combine(ground, "Index");
        var retained = Path.Combine(ground, "Retained");
        var runtime = Path.Combine(ground, "Runtime");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(index);
        Directory.CreateDirectory(runtime);
        if (OperatingSystem.IsWindows()) File.WriteAllBytes(Path.Combine(runtime, WindowsSourceVerificationReaderSlot.FileName), []);
        await File.WriteAllTextAsync(Path.Combine(source, "sample.txt"), "Synthetic verification settings browser fixture.");
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString, source, index,
                services =>
                {
                    services.RemoveAll<SourceJournalOptions>();
                    services.AddSingleton(new SourceJournalOptions { Enabled = true, ApprovedVolumes = [@"I:\"] });
                    services.RemoveAll<ISourceArtifactStore>();
                    services.AddSingleton<ISourceArtifactStore>(_ => new ContentAddressedSourceArtifactStore(retained, [source]));
                    services.RemoveAll<ISourceVerificationReaderSlot>();
                    services.AddSingleton<ISourceVerificationReaderSlot>(_ => OperatingSystem.IsWindows()
                        ? new WindowsSourceVerificationReaderSlot(runtime) : throw new PlatformNotSupportedException());
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
            var evidence = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EVIDENCE_ROOT") ??
                Path.Combine(Path.GetTempPath(), "FluxKnowledgeBrowserEvidence", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            try
            {
            await page.GotoAsync(new Uri(host.BaseAddress, "sources").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
            Assert.Contains("Flux Knowledge", await page.TitleAsync(), StringComparison.OrdinalIgnoreCase);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add folder", Exact = true }).ClickAsync();
            await page.GetByLabel("Folder path").FillAsync(source);
            await page.GetByLabel("Display name").FillAsync("Weekly verification fixture");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await page.GetByText("durable scan request is held", new() { Exact = false }).WaitForAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "View", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Weekly verification fixture", Exact = true }).WaitForAsync();
            var panel = page.Locator(".source-verification");
            await panel.GetByText("Automatic checksum verification is disabled.", new() { Exact = false }).WaitForAsync();
            Assert.Equal(0, await panel.GetByRole(AriaRole.Button, new() { Name = "Verify now", Exact = true }).CountAsync());
            Assert.Equal(0, await panel.GetByRole(AriaRole.Button, new() { Name = "Edit verification schedule", Exact = true }).CountAsync());
            await panel.GetByRole(AriaRole.Button, new() { Name = "Full content rescan", Exact = true }).ClickAsync();
            await panel.GetByText("Full content rescan was requested.", new() { Exact = true }).WaitForAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await panel.GetByText("Quiet, unchanged files are not reread.", new() { Exact = false }).WaitForAsync();
            Assert.Equal("Source root | Flux Knowledge", await page.TitleAsync());
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "verification-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await panel.GetByRole(AriaRole.Heading, new() { Name = "Source updates", Exact = true }).ScrollIntoViewIfNeededAsync();
            Assert.True(await panel.GetByRole(AriaRole.Button, new() { Name = "Full content rescan", Exact = true }).IsVisibleAsync());
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "verification-mobile.png"), FullPage = true });
            Assert.Empty(errors);

            await using var db = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options);
            Assert.Equal((int)SourceRootState.Enabled, await db.SourceRootConfigurations.Select(x => x.State).SingleAsync());
            Assert.Equal(1, await db.SourceRootConfigurations.Select(x => x.ConfigurationRevision).SingleAsync());
            Assert.Empty(await db.SourceVerificationRuns.ToArrayAsync());
            Assert.False(await db.SourceScanRequests.AnyAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind));
            Assert.False(await db.SourceArtifacts.AnyAsync());
            Assert.Equal(1, await db.SourceScanRequests.CountAsync(x => x.RequestKind == 5));
            }
            catch
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "verification-failure.png"), FullPage = true });
                await File.WriteAllTextAsync(Path.Combine(evidence, "verification-failure.html"), await page.ContentAsync());
                await File.WriteAllLinesAsync(Path.Combine(evidence, "verification-console-errors.txt"), errors);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(ground)) Directory.Delete(ground, true);
        }
    }
}
