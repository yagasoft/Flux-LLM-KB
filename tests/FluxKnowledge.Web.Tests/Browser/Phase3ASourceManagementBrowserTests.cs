using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Domain.Sources;
using Microsoft.Playwright;
using Microsoft.EntityFrameworkCore;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class Phase3ASourceManagementBrowserTests
{
    [BrowserFact]
    public async Task Source_filter_editor_reconciles_existing_files_and_can_clear_saved_rules()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeSourceRulesIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeSourceRulesIndexes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(indexRoot);
        Directory.CreateDirectory(Path.Combine(ingressRoot, ".work"));
        Directory.CreateDirectory(Path.Combine(ingressRoot, "nested", ".history"));
        await File.WriteAllTextAsync(Path.Combine(ingressRoot, ".work", "root-hidden.txt"), "root hidden content");
        await File.WriteAllTextAsync(Path.Combine(ingressRoot, "nested", ".history", "nested-hidden.txt"), "nested hidden content");
        await File.WriteAllTextAsync(Path.Combine(ingressRoot, ".keep.txt"), "ordinary eligible dot file");
        await File.WriteAllTextAsync(Path.Combine(ingressRoot, "notes.md"), "ordinary initially included content");
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                sql.ConnectionString, ingressRoot, indexRoot, sourceWorkersEnabled: true);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();

            await page.GotoAsync(new Uri(host.BaseAddress, "sources").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add folder", Exact = true }).ClickAsync();
            await page.GetByLabel("Folder path").FillAsync(ingressRoot);
            await page.GetByLabel("Display name").FillAsync("Scope browser source");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save and scan", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "View", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Scope browser source", Exact = true }).WaitForAsync();

            await using var db = new FluxKnowledgeDbContext(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options);
            await WaitForReconciliationAsync(db, requestKind: 0, completedCount: 1);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            var files = page.GetByRole(AriaRole.Region, new() { Name = "Files", Exact = true });
            ILocator FileRow(string name) => files.GetByRole(AriaRole.Row)
                .Filter(new() { Has = page.GetByRole(AriaRole.Cell, new() { Name = name, Exact = true }) });
            await Assertions.Expect(FileRow("root-hidden.txt")).ToBeVisibleAsync();
            await Assertions.Expect(FileRow("nested-hidden.txt")).ToBeVisibleAsync();

            var edit = page.GetByRole(AriaRole.Button, new() { Name = "Edit source", Exact = true });
            var save = page.GetByRole(AriaRole.Button, new() { Name = "Save and reconcile", Exact = true });
            var includes = page.GetByLabel("Include rules", new() { Exact = true });
            var excludes = page.GetByLabel("Exclude rules", new() { Exact = true });
            var extraRules = Enumerable.Range(0, 900).Select(i => $"never-match-{i:D4}-{new string('x', 50)}/**").ToArray();
            await edit.ClickAsync();
            await includes.FillAsync("*.txt\n**/*.txt");
            await excludes.FillAsync(string.Join('\n', new[] { ".*/**", "**/.*/**" }.Concat(extraRules)));
            await save.ClickAsync();
            await page.GetByText("Rules saved. Reconciliation will update the file list.", new() { Exact = true }).WaitForAsync();

            await WaitForReconciliationAsync(db, requestKind: 1, completedCount: 1);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(FileRow("root-hidden.txt")).ToHaveCountAsync(0);
            await Assertions.Expect(FileRow("nested-hidden.txt")).ToHaveCountAsync(0);
            await Assertions.Expect(FileRow("notes.md")).ToHaveCountAsync(0);
            await Assertions.Expect(FileRow(".keep.txt")).ToBeVisibleAsync();

            await edit.ClickAsync();
            await Assertions.Expect(includes).ToHaveValueAsync("**/*.txt\n*.txt");
            await Assertions.Expect(excludes).ToHaveValueAsync(string.Join('\n', new[] { "**/.*/**", ".*/**" }.Concat(extraRules)));
            await includes.ClearAsync();
            await excludes.ClearAsync();
            await save.ClickAsync();
            await page.GetByText("Rules saved. Reconciliation will update the file list.", new() { Exact = true }).WaitForAsync();

            await WaitForReconciliationAsync(db, requestKind: 1, completedCount: 2);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(FileRow("root-hidden.txt")).ToBeVisibleAsync();
            await Assertions.Expect(FileRow("nested-hidden.txt")).ToBeVisibleAsync();
            await Assertions.Expect(FileRow("notes.md")).ToBeVisibleAsync();
        }
        finally
        {
            Directory.Delete(ingressRoot, recursive: true);
            Directory.Delete(indexRoot, recursive: true);
        }
    }

    private static async Task WaitForReconciliationAsync(FluxKnowledgeDbContext db, int requestKind, int completedCount)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (await db.SourceScanRequests.CountAsync(value => value.RequestKind == requestKind &&
                   value.State == (int)SourceScanRequestState.Completed, deadline.Token) < completedCount)
            await Task.Delay(100, deadline.Token);
    }

    [BrowserFact]
    public async Task Repository_form_sends_large_configuration_over_HTTP_and_MCP_transport_preserves_the_same_payload()
    {
        await using var sql = new NativeSqlServerFixture(); await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxRepositoryBrowser_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxRepositoryBrowserIndex_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot); Directory.CreateDirectory(indexRoot);
        try
        {
            var start = new ProcessStartInfo(@"C:\Program Files\Git\cmd\git.exe") { WorkingDirectory = ingressRoot, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("init"); start.ArgumentList.Add("--quiet");
            using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString, ingressRoot, indexRoot);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();
            var rules = Enumerable.Range(0, 900).Select(i => $"never-match-{i:D4}-{new string('x', 50)}/**").ToArray();
            await page.GotoAsync(new Uri(host.BaseAddress, "sources").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByText("Configure a repository", new() { Exact = true }).ClickAsync();
            var form = page.Locator("form").Filter(new() { Has = page.Locator("[name=path]") });
            await form.Locator("[name=path]").FillAsync(ingressRoot);
            await form.Locator("[name=displayName]").FillAsync("Large browser repository");
            await form.Locator("[name=excludePatterns]").FillAsync(string.Join('\n', rules));
            await form.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
            await form.Locator("[data-source-commit]:enabled").WaitForAsync();
            await form.GetByRole(AriaRole.Button, new() { Name = "Configure and scan" }).ClickAsync();
            await page.GetByText("Large browser repository", new() { Exact = true }).WaitForAsync();
            await using var db = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options);
            var root = await db.SourceRootConfigurations.SingleAsync(value => value.DisplayName == "Large browser repository");
            Assert.Equal(rules, JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson)); Assert.Equal(1, root.CrawlMode);
            using var client = new HttpClient { BaseAddress = host.BaseAddress };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json"); client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
            var rpc = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new {
                name = "corpus.write", arguments = new { mode = "preview", action = "root_create", payload = new {
                    path = ingressRoot, displayName = "Large browser repository", discoveryMode = "git-tracked", indexSourceText = true, excludePatterns = rules } } } });
            using var response = await client.PostAsync("mcp", new StringContent(rpc, System.Text.Encoding.UTF8, "application/json"));
            var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, text);
            Assert.Contains("confirmationId", text); Assert.DoesNotContain("body-too-large", text);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(ingressRoot, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(ingressRoot, true); Directory.Delete(indexRoot, true);
        }
    }
    [BrowserFact]
    public async Task Strict_production_composition_renders_the_sources_page()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ingressRoot = BrowserTestRoots.Create($"FluxKnowledgeStrictSourcesIngress_{Guid.NewGuid():N}");
        var indexRoot = BrowserTestRoots.Create($"FluxKnowledgeStrictSourcesIndexes_{Guid.NewGuid():N}");
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

            using var response = await client.GetAsync("/sources");

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(ingressRoot)) Directory.Delete(ingressRoot, recursive: true);
            if (Directory.Exists(indexRoot)) Directory.Delete(indexRoot, recursive: true);
        }
    }

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
            await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" })
                .GetByRole(AriaRole.Link, new() { Name = "Sources and indexing", Exact = true }).ClickAsync();
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
