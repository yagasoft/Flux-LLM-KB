using System.Collections.Concurrent;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class PipelineOverviewBrowserTests
{
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
            await page.GetByRole(AriaRole.Button, new() { Name = "View pipeline job records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(25);

            var failure = new InvalidOperationException("Synthetic private provider diagnostic");
            reader.AttentionFailure = failure;
            await page.GetByRole(AriaRole.Button, new() { Name = "View GPU task records" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Attention records could not be refreshed");
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
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
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
            await page.GetByRole(AriaRole.Button, new() { Name = "View pipeline job records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(25);
            await page.GetByRole(AriaRole.Button, new() { Name = "Next records" }).ClickAsync();
            await Assertions.Expect(page.Locator("#pipeline-attention tbody tr")).ToHaveCountAsync(5);

            var delayed = reader.DelayNextAttention();
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
        public Exception? AttentionFailure { get; set; }
        public bool RemoveFirstRoot { get; set; }
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
            if (FailReads) throw new InvalidOperationException("Synthetic status failure");
            var now = DateTimeOffset.UtcNow;
            var projection = new OverviewProjection(0, 0, 8, 31, 7139, 35, 1100, "synthetic-generation",
                new("Healthy", "synthetic-generation", now, null, null, 0))
            {
                Activity = new(now, now,
                    [new(FirstRoot, "Example workspace", "C:\\example-workspace", "Enabled", 1100, 1096, 4, 0, 0, 0, 0, 0,
                        now.AddMinutes(-2), now.AddHours(-1), now.AddMinutes(-1), false, null),
                     new(SecondRoot, "Acceptance source", "C:\\acceptance-source", "Enabled", 10, 9, 0, 0, 1, 0, 0, 0,
                        now.AddMinutes(-2), now.AddHours(-1), null, false, null)],
                    [new(FirstRoot, "Example workspace", "Publish", PipelineWorkState.Running, false, "Owned", 1),
                     new(FirstRoot, "Example workspace", "Embed", PipelineWorkState.Historical, true, "Suppressed", 31)],
                    [new(null, "Interactive search", "Outcome uncertain", 179)], 0, 0, 0)
            };
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
        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);
        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken ct) => ValueTask.FromResult<PipelineRecordProjection?>(null);
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
