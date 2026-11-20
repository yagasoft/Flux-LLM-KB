using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class SourceJournalOperatorBrowserTests
{
    [BrowserFact]
    public async Task Operator_checks_partial_and_unavailable_journals_requests_reconciliation_and_exports_reviewed_allowlist()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var ground = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", $"UsnOperator_{Guid.NewGuid():N}");
        var ingress = Path.Combine(ground, "Sources");
        var alpha = Path.Combine(ingress, "Alpha");
        var beta = Path.Combine(ingress, "Beta");
        var gamma = Path.Combine(ingress, "Gamma");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        Directory.CreateDirectory(gamma);
        try
        {
            await using var db = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(ingress);
            var otherVolume = $@"\\?\VOLUME{{{Guid.NewGuid():D}}}\".ToUpperInvariant();
            var now = DateTimeOffset.UtcNow;
            var first = Root(alpha, "Alpha workspace", now);
            var second = Root(beta, "Beta workspace", now);
            var paused = Root(gamma, "Paused workspace", now);
            paused.State = (int)SourceRootState.Paused;
            db.SourceRootConfigurations.AddRange(first, second, paused);
            db.SourceRootScanStates.Add(new() { SourceRootId = paused.Id, VolumeIdentity = volume, BaselineEpoch = 1, Mode = "JournalPartial" });
            db.SourceRootScanStates.Add(new()
            {
                SourceRootId = first.Id, VolumeIdentity = volume, BaselineEpoch = 1, Generation = 1,
                Mode = "JournalPartial", Reason = "deferred-or-incomplete-namespace", CoveredUsn = 101, UnresolvedThroughUsn = 101
            });
            var checkpoint = new SourceVolumeJournalCheckpointEntity { VolumeIdentity = volume, JournalId = "0000000000000011", NextUsn = 101 };
            db.SourceVolumeJournalCheckpoints.Add(checkpoint);
            db.SourcePendingChanges.Add(new()
            {
                Id = Guid.NewGuid(), SourceRootId = first.Id, RelativePath = ".", PathFingerprint = "fixture-namespace",
                ReasonFlags = SourcePendingReasons.NamespaceMetadata, IsSubtree = true, Generation = 1, NotBeforeUtc = now.AddDays(1)
            });
            await db.SaveChangesAsync();

            var reader = new JournalReader();
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(sql.ConnectionString,
                ingress, Path.Combine(ground, "Indexes"), services =>
                {
                    services.RemoveAll<SourceJournalOptions>();
                    services.AddSingleton(new SourceJournalOptions { Enabled = true, PipeName = "FluxKnowledge.SourceJournal.browser",
                        ApprovedVolumes = [volume] });
                    services.RemoveAll<ISourceChangeJournalReader>();
                    services.AddSingleton<ISourceChangeJournalReader>(reader);
                    services.RemoveAll<ISourceJournalVolumeInventory>();
                    services.AddSingleton<ISourceJournalVolumeInventory>(new VolumeInventory(volume, otherVolume));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync(new() { AcceptDownloads = true, ViewportSize = new() { Width = 1280, Height = 900 } });
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(new Uri(host.BaseAddress, $"sources/{first.Id:D}").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
            var panel = page.Locator(".source-verification");
            await Assertions.Expect(panel).ToContainTextAsync("Partial journal coverage");
            await Assertions.Expect(panel).ToContainTextAsync("Namespace history remains unresolved through USN 101");
            await Assertions.Expect(panel).ToContainTextAsync("Pending namespace reconciliation items: 1");
            var check = panel.GetByRole(AriaRole.Button, new() { Name = "Check journal now" });
            await check.ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Journal query and read succeeded.");
            checkpoint.UnavailableSinceUtc = DateTimeOffset.UtcNow;
            checkpoint.LastFailureCode = "journal-unavailable";
            await db.SaveChangesAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Refresh source status" }).ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Unavailable at the last recorded attempt.");
            checkpoint.UnavailableSinceUtc = null;
            checkpoint.LastFailureCode = null;
            await db.SaveChangesAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Refresh source status" }).ClickAsync();
            reader.HelperRefuses = true;
            await check.ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("The protected helper refused this query.");
            reader.HelperRefuses = false;
            await check.ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Journal query and read succeeded.");
            await Assertions.Expect(panel).ToContainTextAsync("Partial journal coverage");
            await CaptureAsync(page, "usn-source-diagnostics.png");

            await page.GotoAsync(host.BaseAddress.ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
            var warning = page.Locator(".source-journal-warning").Filter(new() { HasText = "Alpha workspace" });
            await warning.GetByRole(AriaRole.Button, new() { Name = "Reconcile", Exact = true }).ClickAsync();
            await warning.GetByRole(AriaRole.Button, new() { Name = "Confirm reconcile" }).ClickAsync();
            await Assertions.Expect(warning).ToContainTextAsync("Operation receipt");
            await CaptureAsync(page, "usn-overview-reconcile.png");
            var pausedWarning = page.Locator(".source-journal-warning").Filter(new() { HasText = "Paused workspace" });
            await Assertions.Expect(pausedWarning).ToContainTextAsync("Updates remain blocked while this source is paused.");
            await Assertions.Expect(pausedWarning.GetByRole(AriaRole.Button, new() { Name = "Reconcile", Exact = true })).ToHaveCountAsync(0);
            Assert.Equal(1, await db.SourceScanRequests.AsNoTracking().CountAsync(x => x.SourceRootId == first.Id && x.RequestKind == 1));
            Assert.False(await db.SourceScanRequests.AsNoTracking().AnyAsync(x => x.RequestKind == 5));

            await page.GotoAsync(new Uri(host.BaseAddress, $"sources/{second.Id:D}").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(panel).ToContainTextAsync("No journal checkpoint has been recorded");
            await Assertions.Expect(panel).ToContainTextAsync("Not measured");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Reconcile source metadata", Exact = true }).ClickAsync();
            await Assertions.Expect(panel).ToContainTextAsync("Metadata reconciliation was requested.");
            Assert.Equal(1, await db.SourceScanRequests.AsNoTracking().CountAsync(x => x.SourceRootId == second.Id && x.RequestKind == 1));
            await page.GetByRole(AriaRole.Link, new() { Name = "Manage USN journal allowlist" }).ClickAsync();
            var current = page.GetByRole(AriaRole.Row).Filter(new() { HasText = volume });
            var candidate = page.GetByRole(AriaRole.Row).Filter(new() { HasText = otherVolume });
            await candidate.GetByRole(AriaRole.Checkbox).CheckAsync();
            await current.GetByRole(AriaRole.Checkbox).UncheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Review allowlist change" }).ClickAsync();
            var preview = page.GetByRole(AriaRole.Region, new() { Name = "Allowlist change preview" });
            await Assertions.Expect(preview).ToContainTextAsync("Alpha workspace");
            await Assertions.Expect(preview).ToContainTextAsync("Beta workspace");
            await Assertions.Expect(preview).ToContainTextAsync("Will use watcher and metadata fallback");

            second.DisplayName = "Beta workspace renamed";
            await db.SaveChangesAsync();
            var confirm = page.GetByRole(AriaRole.Button, new() { Name = "Confirm proposal for download" });
            await confirm.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("source impact changed");
            await Assertions.Expect(preview).ToContainTextAsync("Beta workspace renamed");
            await confirm.ClickAsync();
            var download = await page.RunAndWaitForDownloadAsync(() =>
                preview.GetByRole(AriaRole.Link, new() { Name = "Download allowlist proposal" }).ClickAsync());
            var packetPath = Path.Combine(ground, "allowlist-proposal.json");
            await download.SaveAsAsync(packetPath);
            var bytes = await File.ReadAllBytesAsync(packetPath);
            using var packet = JsonDocument.Parse(bytes);
            Assert.Equal(volume, Assert.Single(packet.RootElement.GetProperty("ExpectedApprovedVolumes").EnumerateArray()).GetString());
            Assert.Equal(otherVolume, Assert.Single(packet.RootElement.GetProperty("ApprovedVolumes").EnumerateArray()).GetString());
            await Assertions.Expect(preview).ToContainTextAsync(Convert.ToHexString(SHA256.HashData(bytes)));
            await Assertions.Expect(current).ToContainTextAsync("Approved");
            await Assertions.Expect(candidate).ToContainTextAsync("Outside allowlist");
            Assert.Equal(new[] { volume, volume, volume }, reader.QueriedVolumes);
            await CaptureAsync(page, "usn-allowlist-preview.png");
            await page.SetViewportSizeAsync(390, 844);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
            await CaptureAsync(page, "usn-allowlist-mobile.png");
            Assert.Empty(errors);
        }
        finally
        {
            if (Directory.Exists(ground)) Directory.Delete(ground, true);
        }
    }

    private static async Task CaptureAsync(IPage page, string fileName)
    {
        if (Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EVIDENCE_ROOT") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, fileName), FullPage = true });
    }

    private static SourceRootConfigurationEntity Root(string path, string name, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), CanonicalPath = path, DisplayName = name, State = (int)SourceRootState.Enabled,
        ConfigurationRevision = 1, Recursive = true, ReconciliationCadenceSeconds = 604800,
        MaximumFileBytes = 1024, CreatedAtUtc = now, UpdatedAtUtc = now
    };

    private sealed class VolumeInventory(string volume, string otherVolume) : ISourceJournalVolumeInventory
    {
        public IReadOnlyList<JournalVolume> ReadLocalVolumes() => [new(@"I:\", volume), new(@"E:\", otherVolume)];
    }

    private sealed class JournalReader : ISourceChangeJournalReader
    {
        public bool HelperRefuses { get; set; }
        public List<string> QueriedVolumes { get; } = [];

        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken)
        {
            QueriedVolumes.Add(volumeIdentity);
            if (HelperRefuses) throw new IOException("SourceJournalHelperRefused");
            return ValueTask.FromResult(new JournalCapability(volumeIdentity, 17, 1, 101, 1, 1024, 128));
        }

        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JournalPage(cursor, cursor, []));
    }
}
