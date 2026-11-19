using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Web.Components.OperatorActions;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Tests.Browser;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class GuidedRecoveryActionTests
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task Overview_previews_a_selected_source_and_recovers_its_receipt_after_reopening_the_scope()
    {
        var reader = new PipelineOverviewBrowserTests.ControlledOverviewReader { FirstRootLifecycle = "Paused" };
        var writes = new Writes();
        var root = BrowserTestRoots.Create($"FluxGuidedRecovery_{Guid.NewGuid():N}");
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
                    services.AddSingleton(new NativeCorpusCommandService(writes, writes));
                });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunchOptions.Create());
            await using var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 390, Height = 900 } });
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(host.BaseAddress.AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            Assert.Equal(host.BaseAddress.AbsoluteUri, page.Url);
            Assert.Equal("Overview | Flux Knowledge", await page.TitleAsync());

            var control = page.Locator(".guided-recovery-action");
            var trigger = control.GetByRole(AriaRole.Button, new() { Name = "Preview resume indexing" });
            var preview = control.GetByRole(AriaRole.Region, new() { Name = "Recovery action preview" });
            var confirm = preview.GetByRole(AriaRole.Button, new() { Name = "Confirm resume indexing" });
            var status = control.GetByRole(AriaRole.Status);
            await trigger.PressAsync("Enter");
            await Assertions.Expect(preview).ToBeFocusedAsync();
            await Assertions.Expect(preview).ToContainTextAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot.ToString("D"));
            Assert.Empty(writes.Commits);
            await preview.GetByRole(AriaRole.Button, new() { Name = "Cancel preview" }).PressAsync("Enter");
            await Assertions.Expect(trigger).ToBeFocusedAsync();

            writes.OnCommit = _ => throw new NativeOperationException("operation-fenced");
            await trigger.ClickAsync();
            await confirm.ClickAsync();
            await Assertions.Expect(status).ToContainTextAsync("target changed");
            await Assertions.Expect(preview).ToHaveCountAsync(0);

            writes.OnCommit = _ =>
            {
                writes.ReplayReceipt = new(writes.OperationId, true, "completed", null);
                reader.FirstRootLifecycle = "Enabled";
                throw new IOException("Lost response after recording the result");
            };
            await trigger.ClickAsync();
            await confirm.ClickAsync();
            await Assertions.Expect(status).ToContainTextAsync("result is unconfirmed");
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Recovery requests" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Indexing is paused", new() { Exact = true })).ToHaveCountAsync(0);
            var scope = page.GetByLabel("Source scope");
            await scope.SelectOptionAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.SecondRoot.ToString("D"));
            await Assertions.Expect(control).ToHaveCountAsync(0);
            await scope.SelectOptionAsync(PipelineOverviewBrowserTests.ControlledOverviewReader.FirstRoot.ToString("D"));
            await control.GetByRole(AriaRole.Button, new() { Name = "Retry same request" }).PressAsync("Enter");
            await Assertions.Expect(status).ToContainTextAsync("Recorded request accepted");
            await Assertions.Expect(control).ToContainTextAsync(writes.OperationId.ToString("D"));
            Assert.Equal(2, writes.Commits.Count);
            Assert.Equal(writes.Commits[^1].IdempotencyKey, writes.LastReplay.Key);
            Assert.Equal(3, writes.Previews.Count);
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            Assert.Empty(errors);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Rendering_offers_a_preview_without_preparing_or_submitting_a_request()
    {
        var writes = new Writes();
        var services = new ServiceCollection().AddLogging()
            .AddSingleton(new NativeCorpusCommandService(writes, writes))
            .AddSingleton<ILocalOperatorPolicy>(new MutablePolicy())
            .AddScoped<GuidedRecoveryActions>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<GuidedRecoveryAction>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Action"] = "publication_retry",
                    ["TargetId"] = Guid.NewGuid(),
                    ["ActionLabel"] = "Retry publication"
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Preview retry publication", html);
        Assert.DoesNotContain("Confirm retry publication", html);
        Assert.Empty(writes.Previews);
        Assert.Empty(writes.Commits);
    }

    [Theory]
    [InlineData("publication_retry", false, "jobId")]
    [InlineData("embedding_retry", false, "jobId")]
    [InlineData("job_retry", false, "jobId")]
    [InlineData("root_resume", false, "rootId")]
    [InlineData("source_sync", false, "rootId")]
    [InlineData("source_sync", true, "rootId")]
    public async Task Preview_then_confirmation_use_the_exact_selected_scope_and_record_acceptance(
        string action, bool reconcileMetadata, string idProperty)
    {
        var writes = new Writes();
        var target = Guid.NewGuid();
        var attempt = Create(writes).Select(action, target, reconcileMetadata)!;

        await attempt.PreviewAsync();

        var preview = Assert.Single(writes.Previews);
        Assert.Equal(action, preview.Action);
        Assert.Equal("web", preview.ActorSurface);
        using var payload = JsonDocument.Parse(preview.CanonicalPayload);
        Assert.Equal(target, payload.RootElement.GetProperty(idProperty).GetGuid());
        Assert.Equal(reconcileMetadata, payload.RootElement.TryGetProperty("reconcileMetadata", out var reconcile) && reconcile.GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("fullContent", out _));
        Assert.Empty(writes.Commits);

        Assert.True(await attempt.ConfirmAsync());
        var commit = Assert.Single(writes.Commits);
        Assert.Equal(action, commit.Action);
        Assert.Equal(preview.CanonicalPayload, commit.CanonicalPayload);
        Assert.Equal("confirmation-1", commit.ConfirmationId);
        Assert.Equal("web", commit.ActorSurface);
        Assert.Equal(writes.OperationId, attempt.Receipt?.OperationId);
        Assert.Contains("accepted", attempt.Message);
        Assert.DoesNotContain("searchable", attempt.Message);
    }

    [Fact]
    public void Unsupported_actions_and_empty_targets_cannot_be_prepared()
    {
        var writes = new Writes();
        var actions = Create(writes);
        Assert.Null(actions.Select("root_delete", Guid.NewGuid(), false));
        Assert.Null(actions.Select("publication_retry", Guid.Empty, false));
        Assert.Null(actions.Select("embedding_retry", Guid.NewGuid(), true));
        Assert.Empty(writes.Previews);
    }

    [Fact]
    public async Task Operator_authority_is_rechecked_before_preview_and_confirmation()
    {
        var writes = new Writes();
        var policy = new MutablePolicy { Allowed = false };
        var attempt = Create(writes, policy).Select("embedding_retry", Guid.NewGuid(), false)!;

        await attempt.PreviewAsync();
        Assert.Contains("local operator", attempt.Message);
        Assert.Empty(writes.Previews);

        policy.Allowed = true;
        await attempt.PreviewAsync();
        Assert.NotNull(attempt.Preview);
        policy.Allowed = false;
        Assert.False(await attempt.ConfirmAsync());
        Assert.Contains("local operator", attempt.Message);
        Assert.Empty(writes.Commits);
        Assert.Null(attempt.Receipt);
    }

    [Fact]
    public async Task A_stale_preview_cannot_commit_again_and_a_new_preview_gets_a_new_request()
    {
        var writes = new Writes { OnCommit = _ => throw new NativeOperationException("operation-fenced") };
        var attempt = Create(writes).Select("publication_retry", Guid.NewGuid(), false)!;
        await attempt.PreviewAsync();

        Assert.False(await attempt.ConfirmAsync());
        Assert.Null(attempt.Preview);
        Assert.Null(attempt.Receipt);
        Assert.Contains("changed", attempt.Message);
        Assert.False(await attempt.ConfirmAsync());
        var original = Assert.Single(writes.Commits);

        writes.OnCommit = null;
        await attempt.PreviewAsync();
        Assert.True(await attempt.ConfirmAsync());
        Assert.NotEqual(original.ConfirmationId, writes.Commits[^1].ConfirmationId);
        Assert.NotEqual(original.IdempotencyKey, writes.Commits[^1].IdempotencyKey);
    }

    [Fact]
    public async Task An_uncertain_request_reopens_and_replays_the_same_commit_even_after_a_hold()
    {
        var writes = new Writes { OnCommit = _ => throw new IOException("Response lost after recording the result") };
        var hold = new MutableHold();
        var actions = Create(writes, hold: hold);
        var target = Guid.NewGuid();
        var attempt = actions.Select("source_sync", target, true)!;
        await attempt.PreviewAsync();
        Assert.False(await attempt.ConfirmAsync());
        Assert.True(attempt.IsCommitUnconfirmed);
        Assert.Null(attempt.Receipt);
        var original = Assert.Single(writes.Commits);
        actions.Release(attempt);

        var reopened = actions.Select("source_sync", target, true)!;
        Assert.True(reopened.IsCommitUnconfirmed);
        hold.IsHeld = true;
        Assert.False(await reopened.ConfirmAsync());
        Assert.True(reopened.IsCommitUnconfirmed);
        await reopened.PreviewAsync();
        Assert.Single(writes.Previews);

        hold.IsHeld = false;
        writes.ReplayReceipt = new(writes.OperationId, true, "completed", null);
        Assert.True(await reopened.ConfirmAsync());
        Assert.Equal((original.Action, original.CanonicalPayload, original.ConfirmationId, original.IdempotencyKey, original.ActorSurface),
            writes.LastReplay);
        Assert.Single(writes.Commits);
        Assert.True(reopened.Receipt?.WasReplay);
        Assert.False(reopened.IsCommitUnconfirmed);
    }

    [Fact]
    public async Task An_in_flight_confirmation_cannot_be_submitted_twice()
    {
        var receipt = new TaskCompletionSource<NativeActionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new Writes { OnCommit = _ => new(receipt.Task) };
        var attempt = Create(writes).Select("root_resume", Guid.NewGuid(), false)!;
        await attempt.PreviewAsync();

        var first = attempt.ConfirmAsync();
        Assert.False(await attempt.ConfirmAsync());
        Assert.Single(writes.Commits);
        receipt.SetResult(new(writes.OperationId, false, "completed", null));
        Assert.True(await first);
        Assert.False(await attempt.ConfirmAsync());
        Assert.Single(writes.Commits);
    }

    [Fact]
    public async Task A_native_expiry_after_uncertain_commit_allows_a_fresh_preview()
    {
        var writes = new Writes { OnCommit = _ => throw new IOException("Uncertain commit") };
        var attempt = Create(writes).Select("embedding_retry", Guid.NewGuid(), false)!;
        await attempt.PreviewAsync();
        Assert.False(await attempt.ConfirmAsync());
        Assert.True(attempt.IsCommitUnconfirmed);

        writes.OnCommit = _ => throw new NativeOperationException("confirmation-expired");
        Assert.False(await attempt.ConfirmAsync());
        Assert.False(attempt.IsCommitUnconfirmed);
        Assert.Null(attempt.Preview);
        Assert.Contains("expired", attempt.Message);

        await attempt.PreviewAsync();
        Assert.Equal(2, writes.Previews.Count);
    }

    [Fact]
    public async Task A_receipt_without_acceptance_does_not_signal_success()
    {
        var writes = new Writes { OnCommit = _ => ValueTask.FromResult(new NativeActionReceipt(Guid.NewGuid(), false, "refused", "fixture-denied")) };
        var attempt = Create(writes).Select("job_retry", Guid.NewGuid(), false)!;
        await attempt.PreviewAsync();

        Assert.False(await attempt.ConfirmAsync());
        Assert.Equal("fixture-denied", attempt.ReasonCode);
        Assert.NotNull(attempt.Receipt);
        Assert.Contains("without confirming acceptance", attempt.Message);
    }

    private static GuidedRecoveryActions Create(Writes writes, MutablePolicy? policy = null, MutableHold? hold = null) =>
        new(new NativeCorpusCommandService(writes, writes, deploymentValidationHold: hold),
            policy ?? new MutablePolicy(), NullLogger<GuidedRecoveryActions>.Instance);

    private sealed class MutablePolicy : ILocalOperatorPolicy
    {
        public bool Allowed { get; set; } = true;

        public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken) => Allowed
            ? ValueTask.CompletedTask
            : ValueTask.FromException(new UnauthorizedAccessException());
    }

    private sealed class MutableHold : IDeploymentValidationHold
    {
        public bool IsHeld { get; set; }
        public ValueTask WaitUntilReleasedAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Writes : INativeOperationStore, INativeCorpusActionStore
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public List<NativeActionPreviewRequest> Previews { get; } = [];
        public List<NativeActionCommitRequest> Commits { get; } = [];
        public Func<NativeActionCommitRequest, ValueTask<NativeActionReceipt>>? OnCommit { get; set; }
        public NativeActionReceipt? ReplayReceipt { get; set; }
        public (string Action, string Payload, string Confirmation, string Key, string Surface) LastReplay { get; private set; }
        public ValueTask<NativeActionReceipt?> FindReceiptAsync(string key, string surface, CancellationToken ct) =>
            ValueTask.FromResult<NativeActionReceipt?>(null);

        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string payload, string confirmation,
            string key, string surface, CancellationToken ct)
        {
            LastReplay = (action, payload, confirmation, key, surface);
            return ValueTask.FromResult(ReplayReceipt);
        }

        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken ct)
        {
            Previews.Add(request);
            return ValueTask.FromResult(new NativeActionPreview(Guid.NewGuid(), $"confirmation-{Previews.Count}",
                request.RequestFingerprint, DateTimeOffset.UtcNow.AddMinutes(5), request.Targets, request.EffectSummary));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken ct)
        {
            Commits.Add(request);
            return OnCommit?.Invoke(request) ?? ValueTask.FromResult(new NativeActionReceipt(OperationId, false, "completed", null));
        }

        public ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action, string payload, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>([new("fixture-target", "version")]);

        public ValueTask<NativeActionCommitOperation> CreateCommitOperationAsync(string action, string payload,
            IReadOnlyList<NativeTargetVersion> targets, CancellationToken ct) =>
            ValueTask.FromResult<NativeActionCommitOperation>(new NativeCorpusMutationCommitOperation(action, payload));
    }
}
