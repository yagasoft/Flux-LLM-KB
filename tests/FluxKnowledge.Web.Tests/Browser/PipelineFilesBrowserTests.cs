using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

[Trait("Category", "Browser")]
public sealed class PipelineFilesBrowserTests
{
    [BrowserFact]
    public async Task File_categories_stage_details_paths_and_paging_remain_accessible_at_narrow_width_and_zoom()
    {
        var reader = new FilesReader();
        var root = BrowserTestRoots.Create($"FluxPipelineFiles_{Guid.NewGuid():N}", ["E:/Temp/FluxKnowledgeTests"]);
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
            foreach (var viewport in new[] { (Width: 1440, Zoom: 1), (Width: 390, Zoom: 1), (Width: 1280, Zoom: 2) })
            {
                await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = viewport.Width, Height = 1000 },
                    Permissions = ["clipboard-read", "clipboard-write"] });
                var errors = new List<string>();
                page.PageError += (_, message) => errors.Add(message);
                page.Console += (_, message) => { if (message.Type is "error" or "warning") errors.Add(message.Text); };
                await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
                await page.EvaluateAsync("zoom => document.body.style.zoom = zoom", viewport.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(host.BaseAddress.AbsoluteUri, page.Url);
                Assert.Contains("Overview", await page.TitleAsync(), StringComparison.Ordinal);
                await Assertions.Expect(page.GetByText("Published — ready to query", new() { Exact = true }).First).ToBeVisibleAsync();
                await page.GetByLabel("Source scope").SelectOptionAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot.ToString());
                await page.GetByRole(AriaRole.Button, new() { Name = "Embedding · Queued continuation: 4 files", Exact = true }).ClickAsync();
                var explorer = page.GetByRole(AriaRole.Region, new() { Name = "Affected files" });
                await Assertions.Expect(explorer).ToContainTextAsync("30 matching logical files");
                await Assertions.Expect(explorer).ToContainTextAsync("earlier summary counted 4");
                Assert.Equal(PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot, reader.LastRoot);
                Assert.Equal("Embedding", reader.LastFilter?.Stage);
                var path = explorer.Locator(".file-path").First;
                var reveal = path.Locator("summary");
                await reveal.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                var input = path.GetByLabel("Full path", new() { Exact = true });
                await input.FocusAsync();
                await page.Keyboard.PressAsync("Control+A");
                Assert.True(await input.EvaluateAsync<bool>("input => input.selectionEnd - input.selectionStart === input.value.length"));
                await Assertions.Expect(path).ToContainTextAsync("Containing archive");
                await path.GetByRole(AriaRole.Button, new() { Name = "Copy full path" }).ClickAsync();
                try { await Assertions.Expect(path.Locator("[role=status]")).ToHaveTextAsync("Copied full path."); }
                catch (PlaywrightException exception)
                {
                    var diagnosis = await page.EvaluateAsync<string>("async () => { try { await import('/file-path.js'); return JSON.stringify({ secure: isSecureContext, focused: document.hasFocus(), permission: (await navigator.permissions.query({name:'clipboard-write'})).state }); } catch (error) { return error.name + ': ' + error.message; } }");
                    throw new InvalidOperationException($"Clipboard diagnostic: {diagnosis}; browser errors: {string.Join(';', errors)}", exception);
                }
                Assert.Equal(await input.InputValueAsync(), await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
                var opaqueMember = explorer.Locator(".pipeline-file-item").Nth(1);
                await Assertions.Expect(opaqueMember).ToContainTextAsync("Original member path unavailable");
                await Assertions.Expect(opaqueMember).ToContainTextAsync("Retained archive member");
                await opaqueMember.GetByText("Member identity and copy", new() { Exact = true }).ClickAsync();
                await Assertions.Expect(opaqueMember.GetByLabel("Member identity", new() { Exact = true }))
                    .ToHaveValueAsync(ArchiveMemberIdentity.Create("archive-parent", "folder/member.txt").SyntheticLocator);
                await explorer.GetByText("Embedding detail", new() { Exact = true }).First.ClickAsync();
                await Assertions.Expect(explorer).ToContainTextAsync("12 saved passages");
                await Assertions.Expect(explorer).ToContainTextAsync("8 remaining known passages");
                await Assertions.Expect(explorer).ToContainTextAsync("normal batch continuation");
                var screenshotRoot = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_PIPELINE_SCREENSHOTS");
                if (!string.IsNullOrWhiteSpace(screenshotRoot))
                {
                    Directory.CreateDirectory(screenshotRoot);
                    await explorer.GetByRole(AriaRole.Heading, new() { Name = "Affected files" }).ScrollIntoViewIfNeededAsync();
                    await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotRoot, $"file-detail-{viewport.Width}-{viewport.Zoom}x.png") });
                }
                await explorer.GetByRole(AriaRole.Button, new() { Name = "Next files", Exact = true }).ClickAsync();
                await Assertions.Expect(explorer).ToContainTextAsync("Page 2");
                await Assertions.Expect(explorer.Locator(".pipeline-file-item")).ToHaveCountAsync(5);
                await page.GetByLabel("Source scope").SelectOptionAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.SecondRoot.ToString());
                await Assertions.Expect(explorer).Not.ToContainTextAsync("file-25.txt");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
                Assert.Empty(errors);
                var screenshots = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_PIPELINE_SCREENSHOTS");
                if (!string.IsNullOrWhiteSpace(screenshots))
                {
                    Directory.CreateDirectory(screenshots);
                    await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, $"files-{viewport.Width}-{viewport.Zoom}x.png"), FullPage = true });
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [BrowserFact]
    public async Task Unavailable_file_status_is_not_zero_and_late_results_cannot_replace_a_new_source_scope()
    {
        var reader = new FilesReader { Unavailable = true };
        var root = BrowserTestRoots.Create($"FluxUnavailableFiles_{Guid.NewGuid():N}", ["E:/Temp/FluxKnowledgeTests"]);
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
            await using var page = await browser.NewPageAsync();
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Source scope").SelectOptionAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot.ToString());
            var open = page.GetByRole(AriaRole.Button, new() { Name = "Pending: 4", Exact = true });
            var explorer = page.GetByRole(AriaRole.Region, new() { Name = "Affected files" });
            await open.ClickAsync();
            await Assertions.Expect(explorer.GetByRole(AriaRole.Alert)).ToContainTextAsync("zero count has not been confirmed");
            await Assertions.Expect(explorer).Not.ToContainTextAsync("0 matching logical files");
            reader.Unavailable = false;
            reader.Failure = new InvalidOperationException("Private provider failure");
            await open.ClickAsync();
            await Assertions.Expect(explorer.GetByRole(AriaRole.Alert)).ToContainTextAsync("File status unavailable");
            await Assertions.Expect(explorer).Not.ToContainTextAsync("Private provider failure");
            reader.Failure = null;
            reader.DelayedFiles = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await open.ClickAsync();
            await reader.FileReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.GetByLabel("Source scope").SelectOptionAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.SecondRoot.ToString());
            reader.DelayedFiles.SetResult(new PipelineFilePage(1, 0, 25, DateTimeOffset.UtcNow,
                [new(PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot, Guid.NewGuid(), "Previous scope",
                    "D:/wrong-previous-source.txt", null, "Pending", "Embedding", "Queued", "", "", [], [])]));
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Source scope")).ToHaveValueAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.SecondRoot.ToString());
            await Assertions.Expect(explorer).ToContainTextAsync("Select a file category");
            await Assertions.Expect(explorer).Not.ToContainTextAsync("wrong-previous-source");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FilesReader : IProjectionReader
    {
        private readonly PipelineOverviewBrowserTests.ControlledOverviewReader _base = new();
        public Guid? LastRoot { get; private set; }
        public PipelineFileFilter? LastFilter { get; private set; }
        public bool Unavailable { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource<PipelineFilePage>? DelayedFiles { get; set; }
        public TaskCompletionSource FileReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
        {
            var value = await _base.ReadOverviewAsync(cancellationToken);
            var root = PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot;
            return value with { Activity = value.Activity with
            {
                FileStages = [new(root, "Embedding", "Queued continuation", 4)],
                OcrDemand = [new(root, 0, 0, 0, 0, 0)],
                RunningTaskCount = 1,
                RunningTasks = [new(Guid.NewGuid(), root, Guid.NewGuid(), "Example workspace", "D:/test/sub1/long/path/to/file.ext", null,
                    "Embed", "Running", false, DateTimeOffset.UtcNow.AddMinutes(-3))]
            } };
        }
        public ValueTask<PipelineFilePage> ReadPipelineFilesAsync(PipelineFileFilter filter, Guid? sourceRootId, int page, CancellationToken cancellationToken)
        {
            LastRoot = sourceRootId; LastFilter = filter;
            if (Unavailable) return ValueTask.FromResult(PipelineFilePage.Unavailable);
            if (Failure is { } failure) throw failure;
            if (DelayedFiles is { } delayed)
            {
                FileReadStarted.TrySetResult();
                return new(delayed.Task.WaitAsync(cancellationToken));
            }
            var now = DateTimeOffset.UtcNow;
            var items = Enumerable.Range(page * 25, page == 0 ? 25 : 5).Select(i =>
            {
                var job = Guid.NewGuid();
                return new PipelineFileItem(sourceRootId!.Value, Guid.NewGuid(), "Example workspace",
                    i == 1 ? ArchiveMemberIdentity.Create("archive-parent", "folder/member.txt").SyntheticLocator
                        : $"D:/test/sub1/a/very/long/retained/archive/member/path/file-{i}.txt", "D:/test/container.zip", "Pending", "Embedding",
                    "Queued continuation", "embedding-batch-persisted", "Inspect details.",
                    [new(job, Guid.NewGuid(), "Embed", "Queued continuation", "embedding-batch-persisted", null)],
                    [new(job, 12, 20, now.AddMinutes(-2), "Current checkpoint evidence.")]);
            }).ToArray();
            return ValueTask.FromResult(new PipelineFilePage(30, page, 25, now, items));
        }
        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(CancellationToken ct) => _base.ReadGpuSchedulerStatusAsync(ct);
        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(CancellationToken ct) => _base.ReadPipelineRecordsAsync(ct);
        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken ct) => _base.ReadPipelineRecordAsync(id, ct);
    }
}
