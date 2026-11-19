using FluxKnowledge.Application.Contracts;
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
    public async Task Strict_production_composition_renders_the_Outlook_operator_page()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeStrictOutlookIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeStrictOutlookIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot,
                strictProductionComposition: true);
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
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/outlook").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add profile" }).ClickAsync();
            await page.GetByLabel("Profile name").FillAsync("Browser-created mailbox");
            var markup = await page.ContentAsync();
            Assert.DoesNotContain(ingressRoot, markup, StringComparison.OrdinalIgnoreCase);
            await page.GetByRole(AriaRole.Button, new() { Name = "Save disabled" }).ClickAsync();
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
    public async Task Verify_and_enable_submits_the_folder_once_and_updates_after_durable_host_completion()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookTarget_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookTargetIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        const string target = "Mailbox/PrivateAction";
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString, ingressRoot, indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/outlook").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add profile" }).ClickAsync();
            await page.GetByLabel("Profile name").FillAsync("Target privacy profile");
            await page.GetByLabel("Exact Outlook folder path").FillAsync(target);
            await page.GetByRole(AriaRole.Button, new() { Name = "Verify and enable" }).ClickAsync();
            await page.GetByText("Waiting for Outlook", new() { Exact = true }).WaitForAsync();
            Assert.DoesNotContain(target, await page.ContentAsync(), StringComparison.Ordinal);

            // A fresh page can observe the same durable verification, without the original form.
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("Waiting for Outlook", new() { Exact = true }).WaitForAsync();
            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
            await using var context = new FluxKnowledgeDbContext(options);
            var browse = await context.OutlookBrowseRequests.SingleAsync();
            Assert.Equal(target, browse.TargetPath);
            Assert.False((await context.OutlookCaptureProfiles.SingleAsync()).IsEnabled);

            var store = new SqlOutlookCaptureStore(new TestDbContextFactory(options));
            var fingerprint = new string('a', 64);
            var identity = new OutlookHostIdentity("S-1-5-21-100", 4, "browser-test-host");
            var claim = await store.ClaimBrowseAsync(
                new(Guid.NewGuid(), fingerprint, browse.Id, identity, DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
            var folderId = OutlookCaptureFolderId.New();
            var result = await store.CompleteBrowseAsync(
                new(Guid.NewGuid(), fingerprint, browse.Id, identity, claim.Claim!.FencingToken,
                    [new OutlookBrowseFolderProjection(folderId, "PrivateAction")], browse.ConfigurationRevision,
                    [new OutlookBrowseFolderResult(folderId, "private-store", "private-entry", "PrivateAction")]),
                CancellationToken.None);
            Assert.True(result.Accepted);

            await page.GetByText("Enabled", new() { Exact = true }).WaitForAsync();
            await page.GetByText("PrivateAction", new() { Exact = true }).WaitForAsync();
            Assert.DoesNotContain(target, await page.ContentAsync(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Switching_profiles_recreates_the_one_shot_target_input_without_inheriting_the_prior_profile_value()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookSwitch_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeOutlookSwitchIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        Directory.CreateDirectory(indexRoot);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString, ingressRoot, indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();
            await page.GotoAsync(new Uri(host.BaseAddress, "/outlook").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            foreach (var name in new[] { "First target profile", "Second target profile" })
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Add profile" }).ClickAsync();
                await page.GetByLabel("Profile name").FillAsync(name);
                await page.GetByRole(AriaRole.Button, new() { Name = "Save disabled" }).ClickAsync();
                await page.GetByText("Profile saved disabled", new PageGetByTextOptions { Exact = false }).WaitForAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit" }).First.ClickAsync();
            await page.GetByLabel("Exact Outlook folder path").FillAsync("Mailbox/FirstOnly");
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit" }).Nth(1).ClickAsync();

            await Assertions.Expect(page.GetByLabel("Profile name")).ToHaveValueAsync("Second target profile");
            await Assertions.Expect(page.GetByLabel("Exact Outlook folder path")).ToHaveValueAsync(string.Empty);
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
        var watchedRoot = Path.Combine(ingressRoot, "watch");
        var stagingRoot = Path.Combine(ingressRoot, "stage");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var publishedAt = now.AddHours(-2);
            var profileId = Guid.NewGuid();
            var sourceRootId = Guid.NewGuid();
            var watchedSourceId = Guid.NewGuid();
            var folderId = Guid.NewGuid();
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
                context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = watchedSourceId,
                    CanonicalPath = watchedRoot,
                    DisplayName = "Watched Outlook mail",
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
                    WatchedSourceRootId = watchedSourceId,
                    WatchedRootPath = watchedRoot,
                    WatchedStagingPath = stagingRoot,
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
                    Id = folderId,
                    ProfileId = profileId,
                    StoreId = storeId,
                    FolderEntryId = folderEntryId,
                    DisplayName = "Selected inbox",
                    Basis = (int)OutlookIncrementalBasis.ReceivedTime,
                    State = (int)OutlookCaptureState.Ready
                });
                context.OutlookCaptureExports.Add(new OutlookCaptureExportEntity
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    FolderId = folderId,
                    EntryId = "browser-test-message",
                    SourceFingerprint = new string('c', 64),
                    State = (int)OutlookExportState.Ingested,
                    FencingToken = 1,
                    WatchedPublishedAtUtc = publishedAt
                });
                await context.SaveChangesAsync();
            }

            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString,
                ingressRoot,
                indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "/outlook").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new() { Name = "Outlook capture" }).WaitForAsync();
            await page.GetByText("Browser mailbox", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await page.GetByText("Selected inbox", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            await Assertions.Expect(page.GetByText(publishedAt.LocalDateTime.ToString(), new() { Exact = true })).ToBeVisibleAsync();
            var watchedSource = page.GetByRole(AriaRole.Link, new() { Name = "Open source and indexing progress" });
            await Assertions.Expect(watchedSource).ToHaveAttributeAsync("href", $"/sources/{watchedSourceId:D}");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add profile" }).ClickAsync();
            await page.Locator("form input[name='__RequestVerificationToken']").WaitForAsync(new() { State = WaitForSelectorState.Attached });
            var markup = await page.ContentAsync();
            Assert.Contains("Received-time capture may miss older moved messages", markup, StringComparison.Ordinal);
            Assert.DoesNotContain(privateSpool, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(watchedRoot, markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stagingRoot, markup, StringComparison.OrdinalIgnoreCase);
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

    private sealed class TestDbContextFactory(DbContextOptions<FluxKnowledgeDbContext> options)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }
}
