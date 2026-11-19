using System.Collections.Concurrent;
using System.Diagnostics;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class PipelineOverviewBrowserTests(ITestOutputHelper output)
{
    [BrowserFact]
    public async Task An_open_overview_updates_its_displayed_sample_after_a_background_source_event()
    {
        var reader = new ControlledOverviewReader();
        var events = new StatusEventFeed();
        var root = BrowserTestRoots.Create($"FluxSourceEvent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                    services.RemoveAll<StatusEventFeed>();
                    services.AddSingleton(events);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            await using var page = await browser.NewPageAsync();
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            var sample = page.Locator(".pipeline-overview > .status-freshness time");
            await Assertions.Expect(sample).ToBeVisibleAsync();
            var initialSample = Assert.IsType<string>(await sample.GetAttributeAsync("datetime"));
            Assert.Equal(2, await page.Locator(".source-progress-card").CountAsync());

            reader.RemoveFirstRoot = true;
            var elapsed = Stopwatch.StartNew();
            await events.PublishAsync(new StatusChanged(null, "source", DateTimeOffset.UtcNow), default);
            await Assertions.Expect(sample).Not.ToHaveAttributeAsync("datetime", initialSample, new() { Timeout = 5_000 });
            await Assertions.Expect(page.Locator(".source-progress-card")).ToHaveCountAsync(1);
            output.WriteLine($"Background source event to displayed sample: {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [BrowserFact]
    public async Task A_current_issue_opens_its_source_files_and_restores_keyboard_focus_when_closed()
    {
        var reader = new ControlledOverviewReader { ShowUnavailableFile = true };
        var root = BrowserTestRoots.Create($"FluxIssueInspector_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            foreach (var width in new[] { 1440, 390 })
            {
                await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 1000 } });
                await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
                var needsAttention = page.Locator("#pipeline-attention");
                await Assertions.Expect(needsAttention.GetByText("Files unavailable on this device")).ToBeVisibleAsync();
                var review = needsAttention.GetByRole(AriaRole.Button, new() { Name = "Review files", Exact = true });
                await review.FocusAsync();
                await review.PressAsync("Enter");
                await Assertions.Expect(page.Locator("#affected-files-heading")).ToBeFocusedAsync();
                await Assertions.Expect(needsAttention.GetByText("report.pdf", new() { Exact = true })).ToBeVisibleAsync();
                Assert.Equal(ControlledOverviewReader.SecondRoot, reader.LastFileScope);
                var reasons = needsAttention.GetByRole(AriaRole.Group, new() { Name = "Filter by reason" });
                await Assertions.Expect(reasons.GetByRole(AriaRole.Button, new() { Name = "Unavailable locally (1)" }))
                    .ToHaveAttributeAsync("aria-pressed", "true");
                await reasons.GetByRole(AriaRole.Button, new() { Name = "Document is corrupt (2)" }).ClickAsync();
                await Assertions.Expect(reasons.GetByRole(AriaRole.Button, new() { Name = "Document is corrupt (2)" }))
                    .ToHaveAttributeAsync("aria-pressed", "true");
                Assert.Equal("pdf-document-invalid", reader.LastFileFilter?.Reason);
                await reasons.GetByRole(AriaRole.Button, new() { Name = "All reasons (3)" }).ClickAsync();
                await Assertions.Expect(reasons.GetByRole(AriaRole.Button, new() { Name = "All reasons (3)" }))
                    .ToHaveAttributeAsync("aria-pressed", "true");
                Assert.Null(reader.LastFileFilter?.Reason);
                await ScreenshotAsync(page, $"action-inspector-{width}.png");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
                await needsAttention.GetByRole(AriaRole.Button, new() { Name = "Close file details" }).PressAsync("Enter");
                await Assertions.Expect(page.Locator("#attention-heading")).ToBeFocusedAsync();
                await Assertions.Expect(page.Locator("#affected-files-heading")).ToBeHiddenAsync();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [BrowserFact]
    public async Task File_and_category_preferences_update_attention_and_can_be_removed()
    {
        var preferences = new TestFileAttentionPreferences();
        var reader = new ControlledOverviewReader { ShowUnavailableFile = true, FilePreferences = preferences };
        var root = BrowserTestRoots.Create($"FluxFilePreferences_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                    services.RemoveAll<IFileAttentionPreferences>();
                    services.AddSingleton<IFileAttentionPreferences>(preferences);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            var attention = page.Locator("#pipeline-attention");
            var review = attention.GetByRole(AriaRole.Button, new() { Name = "Review files", Exact = true });
            var ignored = attention.GetByRole(AriaRole.Button, new() { Name = "Ignored files and rules" });
            await review.ClickAsync();
            await attention.GetByRole(AriaRole.Button, new() { Name = "Ignore file", Exact = true }).ClickAsync();
            await Assertions.Expect(review).ToHaveCountAsync(0);
            await Assertions.Expect(ignored).ToHaveTextAsync("Ignored files and rules (1)");
            await ignored.ClickAsync();
            await attention.GetByRole(AriaRole.Button, new() { Name = "Unignore file" }).ClickAsync();
            await Assertions.Expect(review).ToBeVisibleAsync();

            await review.ClickAsync();
            await attention.GetByRole(AriaRole.Button, new() { Name = "Ignore binary file issues" }).ClickAsync();
            var preview = attention.GetByRole(AriaRole.Group, new() { Name = "Confirm ignore rule" });
            await Assertions.Expect(preview).ToContainTextAsync("Applies to Acceptance source");
            Assert.Empty(preferences.Rules);
            await preview.GetByRole(AriaRole.Button, new() { Name = "Ignore category" }).ClickAsync();
            await Assertions.Expect(review).ToHaveCountAsync(0);
            await ignored.ClickAsync();
            await Assertions.Expect(attention.GetByText("Ignored by a rule")).ToBeVisibleAsync();
            await attention.GetByRole(AriaRole.Button, new() { Name = "Remove rule" }).ClickAsync();
            await Assertions.Expect(review).ToBeVisibleAsync();
            Assert.Empty(preferences.Rules);
            await attention.GetByRole(AriaRole.Button, new() { Name = "Add binary file rule" }).ClickAsync();
            await Assertions.Expect(preview).ToContainTextAsync("Applies to all configured sources");
            await preview.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Assertions.Expect(preview).ToHaveCountAsync(0);
            await ScreenshotAsync(page, "file-preferences-narrow.png");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [BrowserFact]
    public async Task Journal_failure_is_visible_with_retry_schedule_and_pause_state_after_filter_and_refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var reader = new ControlledOverviewReader { FirstRootLifecycle = "Paused", JournalHealth = new("Fallback", true, true,
            "journal-recovery-paused", now.AddDays(-2), now.AddSeconds(10), now.AddMinutes(15), 900) };
        var root = BrowserTestRoots.Create($"FluxJournalWarning_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>(); services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            foreach (var width in new[] { 1440, 390 })
            {
                await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 1000 } });
                var errors = new List<string>();
                page.PageError += (_, message) => errors.Add(message);
                page.Console += (_, message) => { if (message.Type is "error" or "warning") errors.Add(message.Text); };
                await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
                Assert.Equal(host.BaseAddress.AbsoluteUri, page.Url);
                Assert.Equal("Overview | Flux Knowledge", await page.TitleAsync());
                var warning = page.Locator(".source-journal-warning");
                await Assertions.Expect(warning).ToContainTextAsync("Change journal unavailable");
                await Assertions.Expect(warning).ToContainTextAsync("Example workspace");
                await Assertions.Expect(warning).ToContainTextAsync("Journal checks keep retrying automatically");
                await Assertions.Expect(warning).ToContainTextAsync("Metadata reconciliation runs weekly");
                await Assertions.Expect(warning).ToContainTextAsync("Updates remain blocked while this source is paused");
                Assert.Equal($"/sources/{ControlledOverviewReader.FirstRoot}", await warning.GetByRole(AriaRole.Link).GetAttributeAsync("href"));
                await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.SecondRoot.ToString());
                await Assertions.Expect(warning).ToHaveCountAsync(0);
                await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.FirstRoot.ToString());
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
                await Assertions.Expect(warning).ToContainTextAsync("Updates remain blocked while this source is paused");
                Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
                Assert.Empty(errors);
                await ScreenshotAsync(page, $"journal-warning-{width}.png");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [BrowserFact]
    public async Task Blocked_OCR_owner_stays_visible_after_source_filter_and_refresh_at_desktop_and_narrow_widths()
    {
        var reader = new ControlledOverviewReader { OcrOwner = new(Guid.NewGuid(), null, "PaddleOCR-VL", "Blocked", 5400, null,
            "document-ocr-legacy-owner-unproven", "Operator verification of native cleanup is required before GPU work can resume.") };
        var root = BrowserTestRoots.Create($"FluxOcrOwner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>(); services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            foreach (var width in new[] { 1440, 390 })
            {
                await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 1000 } });
                var errors = new List<string>();
                page.PageError += (_, message) => errors.Add(message);
                page.Console += (_, message) => { if (message.Type is "error" or "warning") errors.Add(message.Text); };
                await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
                Assert.Equal(host.BaseAddress.AbsoluteUri, page.Url);
                Assert.Contains("Flux Knowledge", await page.TitleAsync(), StringComparison.Ordinal);
                var owner = page.GetByRole(AriaRole.Complementary, new() { Name = "GPU capacity owner" });
                await Assertions.Expect(owner).ToContainTextAsync("5,400 minutes");
                await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.SecondRoot.ToString());
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
                await Assertions.Expect(owner).ToContainTextAsync(reader.OcrOwner.NextAction);
                await owner.GetByText("Capacity diagnostics", new() { Exact = true }).ClickAsync();
                await Assertions.Expect(owner).ToContainTextAsync(reader.OcrOwner.BatchId.ToString());
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
                Assert.Empty(errors);
                await ScreenshotAsync(page, $"ocr-owner-{width}.png");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [BrowserFact]
    public async Task Refresh_notices_do_not_move_the_timestamp_or_activity_during_failure_and_recovery()
    {
        var reader = new ControlledOverviewReader();
        var root = BrowserTestRoots.Create($"FluxPipelineNotice_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            foreach (var viewport in new[] { (Width: 1440, Height: 1000, Zoom: 1), (Width: 390, Height: 844, Zoom: 1), (Width: 1280, Height: 900, Zoom: 2) })
            {
                await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = viewport.Width, Height = viewport.Height } });
                var errors = new List<string>();
                page.PageError += (_, message) => errors.Add(message);
                page.Console += (_, message) => { if (message.Type is "error" or "warning") errors.Add($"{message.Type}: {message.Text}"); };
                await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
                await Assertions.Expect(page.Locator(".activity-state")).ToHaveTextAsync("Catching up");
                await page.EvaluateAsync("zoom => document.body.style.zoom = zoom", viewport.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var positions = await ContentTopsAsync(page);
                await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
                Assert.DoesNotContain("Status changed during the read", await page.Locator(".pipeline-overview").InnerTextAsync(), StringComparison.Ordinal);
                var timestamp = await page.Locator(".status-freshness time").GetAttributeAsync("datetime");
                await ScreenshotAsync(page, $"banner-{viewport.Width}-{viewport.Zoom}x-fresh.png");

                reader.InconsistentReads = true;
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Status changed during the read");
                await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("last successful read");
                await ScreenshotAsync(page, $"banner-{viewport.Width}-{viewport.Zoom}x-stale.png");
                await AssertContentTopsAsync(page, positions);
                await Assertions.Expect(page.Locator(".status-freshness time")).ToHaveAttributeAsync("datetime", timestamp!);

                reader.InconsistentReads = false;
                reader.ReadFailure = new OverviewReadBudgetExceededException(new OperationCanceledException());
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Status is busy");
                await AssertContentTopsAsync(page, positions);

                reader.ReadFailure = new TimeoutException("Synthetic status timeout");
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Status refresh failed");
                await AssertContentTopsAsync(page, positions);

                reader.ReadFailure = null;
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
                await AssertContentTopsAsync(page, positions);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
                Assert.Empty(errors);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Task<double[]> ContentTopsAsync(IPage page) => page.EvaluateAsync<double[]>(
        "['.pipeline-overview > .status-freshness', '.activity-banner', '.source-scope'].map(selector => document.querySelector(selector).getBoundingClientRect().top + window.scrollY)");

    private static async Task AssertContentTopsAsync(IPage page, double[] expected)
    {
        var actual = await ContentTopsAsync(page);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(actual[i], expected[i] - .5, expected[i] + .5);
    }

    [BrowserFact]
    public async Task Attention_failures_log_the_requested_scope_without_exposing_errors_or_replacing_a_new_scope()
    {
        var reader = new ControlledOverviewReader();
        using var logs = new AttentionLogCapture();
        var root = BrowserTestRoots.Create($"FluxPipelineAttention_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                    services.AddLogging(logging => logging.AddProvider(logs));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync();
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.FirstRoot.ToString());
            await page.Locator("details:has(> summary:text-is('Job and GPU diagnostics'))").EvaluateAsync("element => element.open = true");
            await page.GetByRole(AriaRole.Button, new() { Name = "View pipeline job records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(25);

            var failure = new InvalidOperationException("Synthetic private provider diagnostic");
            reader.AttentionFailure = failure;
            await page.GetByRole(AriaRole.Button, new() { Name = "View GPU task records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention").GetByRole(AriaRole.Alert)).ToContainTextAsync("Attention records could not be refreshed");
            await Assertions.Expect(page.Locator("#pipeline-attention caption")).ToContainTextAsync("pipeline job records");
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(25);
            await Assertions.Expect(page.GetByText(failure.Message)).ToHaveCountAsync(0);
            var first = Assert.Single(logs.Failures);
            Assert.Same(failure, first.Exception);
            Assert.Equal("gpu", first.Fields["Unit"]);
            Assert.Equal(ControlledOverviewReader.FirstRoot, first.Fields["SourceRootId"]);
            Assert.Equal(1, first.Fields["Page"]);
            Assert.NotEqual(Guid.Empty, Assert.IsType<Guid>(first.Fields["RequestId"]));

            reader.AttentionFailure = null;
            var delayed = reader.DelayNextAttention();
            await page.GetByRole(AriaRole.Button, new() { Name = "View GPU task records" }).ClickAsync();
            await reader.AttentionStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.SecondRoot.ToString());
            var lateFailure = new TimeoutException("Synthetic late provider diagnostic");
            delayed.SetException(lateFailure);
            var second = await logs.SecondFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(lateFailure, second.Exception);
            Assert.Equal("gpu", second.Fields["Unit"]);
            Assert.Equal(ControlledOverviewReader.FirstRoot, second.Fields["SourceRootId"]);
            Assert.NotEqual(first.Fields["RequestId"], second.Fields["RequestId"]);
            await Assertions.Expect(page.Locator("#pipeline-attention").GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".source-progress-card")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".source-progress-card")).ToContainTextAsync("Acceptance source");
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [BrowserFact]
    public async Task Source_scope_attention_paging_and_failed_refresh_remain_usable_at_desktop_and_narrow_widths()
    {
        var reader = new ControlledOverviewReader();
        var root = BrowserTestRoots.Create($"FluxPipelineOverview_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await PhaseOneVerticalSliceBrowserTests.BrowserHost.StartAsync(
                "Server=unreachable.invalid;Database=unreachable;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                root, Path.Combine(root, "indexes"), services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IProjectionReader>();
                    services.AddSingleton<IProjectionReader>(reader);
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
            var errors = new List<string>();
            page.PageError += (_, message) => errors.Add(message);
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Current activity" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".activity-state")).ToHaveTextAsync("Catching up");
            await Assertions.Expect(page.Locator(".source-progress-card")).ToHaveCountAsync(2);
            Assert.Contains("1,100", await page.ContentAsync(), StringComparison.Ordinal);
            await ScreenshotAsync(page, "05-overview-desktop-after.png");

            await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.SecondRoot.ToString());
            await Assertions.Expect(page.Locator(".activity-state")).ToHaveTextAsync("Blocked");
            await Assertions.Expect(page.Locator(".source-progress-card")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".source-progress-card")).ToContainTextAsync("Acceptance source");
            await page.GetByLabel("Source scope").SelectOptionAsync("");
            await page.Locator("details:has(> summary:text-is('Job and GPU diagnostics'))").EvaluateAsync("element => element.open = true");
            await page.GetByRole(AriaRole.Button, new() { Name = "View pipeline job records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(25);
            await page.GetByRole(AriaRole.Button, new() { Name = "Next records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(5);

            var delayed = reader.DelayNextAttention();
            await page.Locator("details:has(> summary:text-is('Job and GPU diagnostics'))").EvaluateAsync("element => element.open = true");
            await page.GetByRole(AriaRole.Button, new() { Name = "View pipeline job records" }).ClickAsync();
            await reader.AttentionStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.SecondRoot.ToString());
            delayed.SetResult(new PipelineAttentionPage(1, 0, 25,
                [new(Guid.NewGuid(), ControlledOverviewReader.FirstRoot, null, "Wrong previous source", "Pipeline job",
                    "Failed", "Historical", "Late response", null)]));
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(0);
            await page.GetByLabel("Source scope").SelectOptionAsync("");

            reader.FailReads = true;
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("last successful read");
            await Assertions.Expect(page.Locator(".source-progress-card")).ToHaveCountAsync(2);
            reader.FailReads = false;
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

            await page.GetByText("Local GPU scheduler and retained totals", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".scheduler-details[open]")).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(390, 844);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" })).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            await page.Locator(".activity-banner").ScrollIntoViewIfNeededAsync();
            await ScreenshotAsync(page, "06-overview-narrow-after.png");
            await page.GetByLabel("Source scope").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await page.EvaluateAsync<bool>("document.activeElement !== document.body"));
            await page.SetViewportSizeAsync(1280, 900);
            await page.EvaluateAsync("document.body.style.zoom = '2'");
            var overflow = await page.EvaluateAsync<string>("JSON.stringify([...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > innerWidth + 1).slice(0,12).map(e => ({tag:e.tagName, class:e.className, right:e.getBoundingClientRect().right})))");
            await ScreenshotAsync(page, "08-overview-zoom-after.png");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), overflow);
            await page.EvaluateAsync("document.body.style.zoom = ''");

            await page.GetByLabel("Source scope").SelectOptionAsync(ControlledOverviewReader.FirstRoot.ToString());
            await page.Locator("details:has(> summary:text-is('Job and GPU diagnostics'))").EvaluateAsync("element => element.open = true");
            await page.GetByRole(AriaRole.Button, new() { Name = "View pipeline job records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(25);
            reader.RemoveFirstRoot = true;
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Source scope")).ToHaveValueAsync("");
            await Assertions.Expect(page.Locator(".source-progress-card")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(0);
            Assert.Empty(errors);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task ScreenshotAsync(IPage page, string name)
    {
        var directory = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_PIPELINE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name), FullPage = true });
        if (name.Contains("narrow", StringComparison.Ordinal))
        {
            await page.EvaluateAsync("window.scrollTo(0,0)");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "07-overview-narrow-viewport.png") });
        }
    }

    internal sealed class ControlledOverviewReader : IProjectionReader
    {
        public static readonly Guid FirstRoot = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid SecondRoot = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public bool FailReads { get; set; }
        public bool InconsistentReads { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? AttentionFailure { get; set; }
        public bool RemoveFirstRoot { get; set; }
        public GpuOcrOwnerStatus? OcrOwner { get; set; }
        public string FirstRootLifecycle { get; set; } = "Enabled";
        public SourceJournalHealth? JournalHealth { get; set; }
        public bool ShowUnavailableFile { get; set; }
        public TestFileAttentionPreferences? FilePreferences { get; set; }
        public Guid? LastFileScope { get; private set; }
        public PipelineFileFilter? LastFileFilter { get; private set; }
        private TaskCompletionSource<PipelineAttentionPage>? _delayedAttention;
        private TaskCompletionSource _attentionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AttentionStarted => _attentionStarted.Task;
        public TaskCompletionSource<PipelineAttentionPage> DelayNextAttention()
        {
            _attentionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _delayedAttention = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
        {
            if (ReadFailure is { } failure) throw failure;
            if (FailReads) throw new InvalidOperationException("Synthetic status failure");
            var now = DateTimeOffset.UtcNow;
            var projection = new OverviewProjection(0, 0, 8, 31, 7139, 35, 1100, "synthetic-generation",
                new("Healthy", "synthetic-generation", now, null, null, 0))
            {
                GpuSchedulerStatus = GpuSchedulerStatusProjection.Empty with { OcrOwner = OcrOwner },
                Activity = new(now, now,
                    [new(FirstRoot, "Example workspace", "C:\\example-workspace", FirstRootLifecycle, 1100, 1096, 4, 0, 0, 0, 0, 0,
                        now.AddMinutes(-2), now.AddHours(-1), now.AddMinutes(-1), false, null) { JournalHealth = JournalHealth },
                     new(SecondRoot, "Acceptance source", "C:\\acceptance-source", "Enabled", 10, 9, 0, 0, 1, 0, 0, 0,
                        now.AddMinutes(-2), now.AddHours(-1), null, false, null)],
                    [new(FirstRoot, "Example workspace", "Publish", PipelineWorkState.Running, false, "Owned", 1),
                     new(FirstRoot, "Example workspace", "Embed", PipelineWorkState.Historical, true, "Suppressed", 31)],
                    [new(null, "Interactive search", "Outcome uncertain", 179)], 0, 0, 0)
            };
            if (ShowUnavailableFile) projection = projection with { Activity = projection.Activity with
            {
                FileCauses = [new(SecondRoot, "Blocked", "SourceContentNotAvailableLocally",
                    "Make this file available in the sync client, then reconcile metadata.", 1)]
            } };
            if (FilePreferences is { } preferences) projection = projection with { Activity = projection.Activity with
            {
                FileCauses = preferences.IsIgnored ? [] : projection.Activity.FileCauses,
                FileAttention = [new(SecondRoot, preferences.IsIgnored ? 0 : 1, 0, 0, 0, preferences.IsIgnored ? 1 : 0)]
            } };
            if (InconsistentReads) projection = projection with { Activity = projection.Activity with { IsConsistent = false } };
            return ValueTask.FromResult(RemoveFirstRoot ? projection with { Activity = projection.Activity with
            {
                Sources = projection.Activity.Sources.Where(s => s.Id != FirstRoot).ToArray(),
                Jobs = projection.Activity.Jobs.Where(j => j.SourceRootId != FirstRoot).ToArray()
            } } : projection);
        }
        public ValueTask<PipelineAttentionPage> ReadPipelineAttentionAsync(string unit, Guid? sourceRootId, int page, CancellationToken ct)
        {
            if (AttentionFailure is { } failure) throw failure;
            if (_delayedAttention is { } delayed)
            {
                _delayedAttention = null;
                _attentionStarted.SetResult();
                return new(delayed.Task.WaitAsync(ct));
            }
            return ValueTask.FromResult(new PipelineAttentionPage(30, page, 25, Enumerable.Range(page * 25, page == 0 ? 25 : 5)
                .Select(i => new PipelineAttentionItem(Guid.NewGuid(), FirstRoot, null, "Example workspace", "Pipeline job",
                    "Failed", "Historical", $"Retained outcome {i}", DateTimeOffset.UtcNow)).ToArray()));
        }
        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(CancellationToken ct) => ValueTask.FromResult(GpuSchedulerStatusProjection.Empty);
        public ValueTask<PipelineFilePage> ReadPipelineFilesAsync(PipelineFileFilter filter, Guid? sourceRootId, int page, CancellationToken ct)
        {
            LastFileScope = sourceRootId;
            LastFileFilter = filter;
            var isIgnored = FilePreferences?.IsIgnored == true;
            var includeFile = FilePreferences is null || (filter.Attention == PipelineFileAttentionScope.Ignored) == isIgnored;
            var items = !includeFile ? Array.Empty<PipelineFileItem>() :
                [new(SecondRoot, TestFileAttentionPreferences.Revision, "Acceptance source", "C:\\acceptance-source\\report.pdf", null,
                    "Blocked", "Extract", "Blocked", "SourceContentNotAvailableLocally",
                    "Make this file available locally, then reconcile metadata.", [], [])
                    { CanIgnore = true, IsBinaryIssue = true, IsIgnored = isIgnored,
                        IsIndividuallyIgnored = FilePreferences?.FileIgnored == true }];
            return ValueTask.FromResult(new PipelineFilePage(items.Length, 0, 25, DateTimeOffset.UtcNow, items) { HasBinaryIssues = includeFile, Reasons =
                FilePreferences is null
                    ? [new("SourceContentNotAvailableLocally", "Unavailable locally", 1), new("pdf-document-invalid", "Document is corrupt", 2)]
                    : includeFile ? [new("SourceContentNotAvailableLocally", "Unavailable locally", 1, "SourceContentNotAvailableLocally")] : [] });
        }
        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);
        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken ct) => ValueTask.FromResult<PipelineRecordProjection?>(null);
    }

    internal sealed class TestFileAttentionPreferences : IFileAttentionPreferences
    {
        public static readonly Guid Revision = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public bool CanManage => true;
        public bool FileIgnored { get; private set; }
        public List<PipelineAttentionRule> Rules { get; } = [];
        public bool IsIgnored => FileIgnored || Rules.Count > 0;
        public ValueTask SetFileIgnoredAsync(Guid sourceRootId, Guid sourceRevisionId, bool ignored, string? expectedVersion, CancellationToken cancellationToken)
        {
            Assert.Equal(ControlledOverviewReader.SecondRoot, sourceRootId);
            Assert.Equal(Revision, sourceRevisionId);
            FileIgnored = ignored;
            return ValueTask.CompletedTask;
        }
        public ValueTask SetRuleIgnoredAsync(Guid? sourceRootId, PipelineAttentionRuleKind kind, string? reason, bool ignored,
            string? expectedVersion, CancellationToken cancellationToken)
        {
            Assert.Equal(ControlledOverviewReader.SecondRoot, sourceRootId);
            Assert.Equal(PipelineAttentionRuleKind.Binary, kind);
            Assert.Null(reason);
            if (ignored) Rules.Add(new(sourceRootId, kind, null, "Binary file issues", "test-rule-version"));
            else
            {
                Assert.Equal("test-rule-version", expectedVersion);
                Rules.Clear();
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<PipelineAttentionRule>> ReadRulesAsync(Guid? sourceRootId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineAttentionRule>>(Rules.ToArray());
    }

    private sealed class AttentionLogCapture : ILoggerProvider
    {
        public ConcurrentQueue<AttentionFailureLog> Failures { get; } = new();
        public TaskCompletionSource<AttentionFailureLog> SecondFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void Dispose() { }

        private sealed class CaptureLogger(AttentionLogCapture capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (level != LogLevel.Warning || exception is null ||
                    !formatter(state, exception).StartsWith("Pipeline attention read ", StringComparison.Ordinal)) return;
                var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                    .ToDictionary(item => item.Key, item => item.Value);
                var entry = new AttentionFailureLog(exception, fields);
                capture.Failures.Enqueue(entry);
                if (capture.Failures.Count == 2) capture.SecondFailure.TrySetResult(entry);
            }
        }
    }

    private sealed record AttentionFailureLog(Exception Exception, IReadOnlyDictionary<string, object?> Fields);
}
