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
    public async Task Existing_source_page_persists_verification_controls_without_starting_a_scan()
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
            await panel.GetByText("Verification needs a complete baseline before it can start.", new() { Exact = true }).WaitForAsync();
            Assert.False(await panel.GetByRole(AriaRole.Button, new() { Name = "Verify now", Exact = true }).IsEnabledAsync());
            Assert.Equal("Source root | Flux Knowledge", await page.TitleAsync());
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());

            await panel.GetByRole(AriaRole.Button, new() { Name = "Edit verification schedule", Exact = true }).ClickAsync();
            await panel.GetByLabel("Time zone", new() { Exact = true }).FillAsync("Not-A-Time-Zone");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Save verification schedule", Exact = true }).ClickAsync();
            await panel.GetByText("Enter a valid time zone, quiet window and positive read limit.", new() { Exact = true }).WaitForAsync();
            await panel.GetByLabel("Time zone", new() { Exact = true }).FillAsync("UTC");
            await panel.GetByLabel("Weekly start day", new() { Exact = true }).SelectOptionAsync("Friday");
            await panel.GetByLabel("Local start time", new() { Exact = true }).FillAsync("23:15");
            await panel.GetByLabel("Quiet window (hours)", new() { Exact = true }).FillAsync("4");
            await panel.GetByLabel("Combined read limit (bytes/s)", new() { Exact = true }).FillAsync("524288");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Save verification schedule", Exact = true }).ClickAsync();
            await panel.GetByText("Verification schedule was saved.", new() { Exact = true }).WaitForAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await panel.GetByText("Schedule: Friday at 23:15, UTC; 4 hours per window.", new() { Exact = true }).WaitForAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Pause verification", Exact = true }).ClickAsync();
            await panel.GetByText("Verification was paused.", new() { Exact = true }).WaitForAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Resume verification", Exact = true }).ClickAsync();
            await panel.GetByText("Verification was resumed.", new() { Exact = true }).WaitForAsync();

            await panel.GetByRole(AriaRole.Button, new() { Name = "Edit verification schedule", Exact = true }).ClickAsync();
            await panel.GetByLabel("Local start time", new() { Exact = true }).FillAsync("22:10");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Refresh verification status", Exact = true }).ClickAsync();
            Assert.Equal("22:10", await panel.GetByLabel("Local start time", new() { Exact = true }).InputValueAsync());
            await panel.GetByRole(AriaRole.Button, new() { Name = "Cancel schedule edit", Exact = true }).ClickAsync();

            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "verification-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await panel.GetByRole(AriaRole.Heading, new() { Name = "Integrity verification", Exact = true }).ScrollIntoViewIfNeededAsync();
            Assert.True(await panel.GetByRole(AriaRole.Button, new() { Name = "Edit verification schedule", Exact = true }).IsVisibleAsync());
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "verification-mobile.png"), FullPage = true });
            Assert.Empty(errors);

            await using var db = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options);
            Assert.Equal((int)SourceRootState.Enabled, await db.SourceRootConfigurations.Select(x => x.State).SingleAsync());
            Assert.Equal(1, await db.SourceRootConfigurations.Select(x => x.ConfigurationRevision).SingleAsync());
            Assert.Empty(await db.SourceVerificationRuns.ToArrayAsync());
            Assert.False(await db.SourceScanRequests.AnyAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind));
            Assert.False(await db.SourceArtifacts.AnyAsync());
            var state = await db.SourceRootScanStates.SingleAsync();
            Assert.False(state.VerificationPaused);
            Assert.Contains("524288", state.VerificationPolicyJson, StringComparison.Ordinal);
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
