using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceDeletionStatusTests
{
    [Fact]
    public async Task Running_cleanup_does_not_offer_an_attention_retry()
    {
        var html = await RenderAsync("cleanup-memberships");

        Assert.Contains("in progress", html);
        Assert.DoesNotContain("Retry cleanup", html);
    }

    [Fact]
    public async Task Attention_is_reported_as_stopped_work_with_an_explicit_retry()
    {
        var html = await RenderAsync("attention", "source-delete-sql-timeout");

        Assert.Contains("needs attention", html);
        Assert.Contains("Retry cleanup", html);
        Assert.Contains("source-delete-sql-timeout", html);
        Assert.DoesNotContain("Deletion is in progress", html);
    }

    [Fact]
    public async Task A_query_owner_wait_is_labelled_waiting()
    {
        var html = await RenderAsync("cleanup-memberships", "source-delete-search-queries-active");

        Assert.Contains("waiting", html);
        Assert.DoesNotContain("Retry cleanup", html);
    }

    [Fact]
    public async Task Attention_retains_committed_progress_and_the_phase_to_resume()
    {
        var id = Guid.Parse("A267714C-C21C-46D2-8C87-EFCBF9AA490D");
        var progress = new SourceDeletionProgressProjection(id, 4, "cleanup-memberships", 2000,
            DateTimeOffset.Parse("2026-10-14T10:00:00Z"));

        var html = await RenderAsync("attention", "source-delete-sql-timeout", progress);

        Assert.Contains("2,000", html);
        Assert.Contains("cleanup-memberships", html);
        Assert.Contains("2026-10-14 10:00:00 UTC", html);
        Assert.Contains(id.ToString("D"), html);
        Assert.DoesNotContain("%", html);
    }

    private static async Task<string> RenderAsync(string phase, string? reason = null,
        SourceDeletionProgressProjection? progress = null)
    {
        var services = new ServiceCollection().AddLogging();
        var writes = new RefusingWrites();
        services.AddSingleton(new NativeCorpusCommandService(writes, writes));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SourceLifecycleControls>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SourceLifecycleControls.RootId)] = Guid.NewGuid(),
                    [nameof(SourceLifecycleControls.State)] = "Deleting",
                    [nameof(SourceLifecycleControls.DisplayName)] = "Disposable source",
                    [nameof(SourceLifecycleControls.CanonicalPath)] = @"C:\disposable-originals",
                    [nameof(SourceLifecycleControls.DeletionPhase)] = phase,
                    [nameof(SourceLifecycleControls.DeletionReason)] = reason,
                    [nameof(SourceLifecycleControls.DeletionProgress)] = progress
                }));
            return component.ToHtmlString();
        });
    }

    // Rendering is read-only. A write accidentally attempted during rendering fails closed.
    private sealed class RefusingWrites : INativeOperationStore, INativeCorpusActionStore
    {
        public ValueTask<NativeActionReceipt?> FindReceiptAsync(string idempotencyKey, string actorSurface,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload,
            string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action,
            string canonicalPayload, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionCommitOperation> CreateCommitOperationAsync(string action,
            string canonicalPayload, IReadOnlyList<NativeTargetVersion> targets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
