using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class NativeOutlookConfigurationBrowserTests
{
    [BrowserFact]
    public async Task Anonymous_loopback_request_can_render_the_Outlook_operator_page()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookAnonymous_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookAnonymousIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot);
            using var client = new HttpClient { BaseAddress = host.BaseAddress };

            using var response = await client.GetAsync("/outlook");

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Anonymous_loopback_operator_can_create_a_disabled_profile_without_sending_a_spool_path_over_SignalR()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookMutation_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookMutationIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/outlook").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add profile" }).ClickAsync();
            await page.GetByLabel("Profile name").FillAsync("Browser-created mailbox");
            var markup = await page.ContentAsync();
            Assert.DoesNotContain(ingressRoot, markup, StringComparison.OrdinalIgnoreCase);
            await page.GetByRole(AriaRole.Button, new() { Name = "Save profile" }).ClickAsync();
            await page.GetByText("Profile saved disabled", new PageGetByTextOptions { Exact = false }).WaitForAsync();

            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
            await using var context = new FluxKnowledgeDbContext(options);
            var profile = await context.OutlookCaptureProfiles.SingleAsync();
            Assert.Equal("Browser-created mailbox", profile.DisplayName);
            Assert.False(profile.IsEnabled);
            Assert.Equal(Path.GetFullPath(ingressRoot), profile.SpoolRoot);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Outlook_page_renders_safe_SQL_status_without_private_Outlook_identifiers()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        const string privateSpool = "C:\\private\\browser-outlook-spool";
        const string storeId = "browser-private-store-id";
        const string folderEntryId = "browser-private-folder-entry-id";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var profileId = Guid.NewGuid();
            var sourceRootId = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
            await using (var context = new FluxKnowledgeDbContext(options))
            {
                context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = sourceRootId,
                    CanonicalPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}",
                    DisplayName = "Private Outlook capture",
                    State = 1,
                    IncludePatternsJson = "[]",
                    ExcludePatternsJson = "[]",
                    AllowedClassificationsJson = "[]",
                    MaximumFileBytes = 64L * 1024 * 1024,
                    ReconciliationCadenceSeconds = 86400,
                    ConfigurationRevision = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
                {
                    Id = profileId,
                    SourceRootId = sourceRootId,
                    DisplayName = "Browser mailbox",
                    SpoolRoot = privateSpool,
                    IncrementalBasis = (int)OutlookIncrementalBasis.ReceivedTime,
                    State = (int)OutlookCaptureState.Ready,
                    IsEnabled = true,
                    ConfigurationRevision = 2,
                    CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
                    MaximumOverlapTicks = TimeSpan.FromMinutes(2).Ticks,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                context.OutlookCaptureFolders.Add(new OutlookCaptureFolderEntity
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    StoreId = storeId,
                    FolderEntryId = folderEntryId,
                    DisplayName = "Selected inbox",
                    Basis = (int)OutlookIncrementalBasis.ReceivedTime,
                    State = (int)OutlookCaptureState.Ready
                });
                await context.SaveChangesAsync();
            }

            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/outlook").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new() { Name = "Outlook capture" }).WaitForAsync();
            await page.GetByText("Browser mailbox", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("Selected inbox", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Add profile" }).ClickAsync();
            await page.Locator("form input[name='__RequestVerificationToken']").WaitForAsync();
            var markup = await page.ContentAsync();
            Assert.Contains("Received-time capture may miss older moved messages", markup, StringComparison.Ordinal);
            Assert.DoesNotContain(privateSpool, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(storeId, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(folderEntryId, markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Private spool location", markup, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

}
