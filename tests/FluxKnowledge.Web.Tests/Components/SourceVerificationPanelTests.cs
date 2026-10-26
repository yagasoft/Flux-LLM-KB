using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceVerificationPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delayed_precommit_read_cannot_replace_newer_committed_status_or_add_an_obsolete_error(bool failOldRead)
    {
        var reads = new Reads();
        var writes = new Writes();
        var state = new SourceVerificationPanelState(reads, new(writes, writes));
        var root = reads.Status.SourceRootId.Value;
        await state.LoadAsync(root, default);
        var staleStatus = reads.Status;
        var delayed = new TaskCompletionSource<SourceVerificationStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        reads.OnRead = call => call == 2 ? new(delayed.Task) : ValueTask.FromResult<SourceVerificationStatus?>(reads.Status);
        var oldRead = state.LoadAsync(root, default);
        reads.Status = reads.Status with { Paused = true };
        await state.SetPausedAsync(root, true);
        Assert.True(state.Status!.Paused);
        if (failOldRead) delayed.SetException(new IOException("Obsolete status request failed"));
        else delayed.SetResult(staleStatus);
        await oldRead;
        Assert.True(state.Status!.Paused);
        Assert.Null(state.ReadError);
        Assert.Contains("paused", state.Message);
    }

    [Fact]
    public async Task Lost_commit_response_then_a_held_retry_keeps_the_exact_tuple_until_recorded_replay()
    {
        var reads = new Reads();
        var writes = new Writes { FailNext = new IOException("Lost response after commit"), RecordReceiptOnFailure = true };
        var hold = new MutableHold();
        var state = new SourceVerificationPanelState(reads, new(writes, writes, deploymentValidationHold: hold));
        var root = reads.Status.SourceRootId.Value;
        await state.LoadAsync(root, default);
        await state.SetPausedAsync(root, true);
        Assert.True(state.HasPendingCommit);
        var original = Assert.Single(writes.Commits);
        hold.IsHeld = true;
        var replayCalls = writes.ReplayCalls;
        await state.RetryAsync();
        Assert.True(state.HasPendingCommit);
        Assert.Equal(replayCalls, writes.ReplayCalls);
        await state.VerifyAsync(root);
        Assert.Single(writes.Commits);
        hold.IsHeld = false;
        await state.RetryAsync();
        Assert.False(state.HasPendingCommit);
        Assert.Single(writes.Commits);
        Assert.Equal(1, writes.Previews);
        Assert.Equal(original.IdempotencyKey, writes.LastReplayKey);
        Assert.Equal(original.ConfirmationId, writes.LastReplayConfirmation);
        Assert.Equal(original.CanonicalPayload, writes.LastReplayPayload);
    }

    [Fact]
    public async Task An_uncertain_attempt_without_a_receipt_can_release_its_tuple_after_proven_confirmation_expiry()
    {
        var reads = new Reads();
        var writes = new Writes { FailNext = new IOException("Attempt failed before durable commit") };
        var state = new SourceVerificationPanelState(reads, new(writes, writes));
        var root = reads.Status.SourceRootId.Value;
        await state.SetPausedAsync(root, true);
        Assert.True(state.HasPendingCommit);
        var original = Assert.Single(writes.Commits);
        writes.FailNext = new NativeOperationException("confirmation-expired");
        await state.RetryAsync();
        Assert.False(state.HasPendingCommit);
        Assert.Contains("confirmation-expired", state.Message);
        await state.SetPausedAsync(root, false);
        Assert.Equal(2, writes.Previews);
        Assert.NotEqual(original.IdempotencyKey, writes.Commits[^1].IdempotencyKey);
    }

    [Fact]
    public async Task Status_refresh_is_read_only_and_marks_cached_values_stale_on_failure()
    {
        var reads = new Reads();
        var writes = new Writes();
        var state = new SourceVerificationPanelState(reads, new(writes, writes));
        await state.LoadAsync(reads.Status.SourceRootId.Value, default);
        Assert.Same(reads.Status, state.Status);
        reads.Fail = true;
        await state.LoadAsync(reads.Status.SourceRootId.Value, default);
        Assert.NotNull(state.ReadError);
        Assert.Same(reads.Status, state.Status);
        Assert.Equal(2, reads.Calls);
        Assert.Empty(writes.Commits);
        Assert.Equal(0, writes.Previews);
    }

    [Fact]
    public async Task Uncertain_commit_retries_the_exact_request_and_blocks_other_changes()
    {
        var reads = new Reads();
        var writes = new Writes { FailNext = new IOException("Simulated lost response") };
        var state = new SourceVerificationPanelState(reads, new(writes, writes));
        var root = reads.Status.SourceRootId.Value;
        await state.LoadAsync(root, default);
        await state.SetPausedAsync(root, true);
        Assert.True(state.HasPendingCommit);
        await state.VerifyAsync(root);
        Assert.Single(writes.Commits);
        await state.RetryAsync();
        Assert.False(state.HasPendingCommit);
        Assert.Equal(1, writes.Previews);
        Assert.Equal(2, writes.Commits.Count);
        Assert.Equal(writes.Commits[0].CanonicalPayload, writes.Commits[1].CanonicalPayload);
        Assert.Equal(writes.Commits[0].ConfirmationId, writes.Commits[1].ConfirmationId);
        Assert.Equal(writes.Commits[0].IdempotencyKey, writes.Commits[1].IdempotencyKey);
        Assert.Contains("paused", state.Message);
    }

    [Fact]
    public async Task Definitive_refusal_discards_confirmation_and_allows_a_fresh_action()
    {
        var reads = new Reads();
        var writes = new Writes { FailNext = new NativeOperationException("confirmation-stale") };
        var state = new SourceVerificationPanelState(reads, new(writes, writes));
        var root = reads.Status.SourceRootId.Value;
        await state.SetPausedAsync(root, true);
        Assert.False(state.HasPendingCommit);
        Assert.Contains("confirmation-stale", state.Message);
        await state.SetPausedAsync(root, false);
        Assert.Equal(2, writes.Previews);
        Assert.NotEqual(writes.Commits[0].IdempotencyKey, writes.Commits[1].IdempotencyKey);
    }

    [Fact]
    public async Task Panel_shows_real_counts_policy_and_scanning_health_without_mutating_or_polling_during_prerender()
    {
        var (html, reads, writes) = await RenderAsync();
        Assert.Contains("Integrity verification", html);
        Assert.Contains("Journal", html);
        Assert.Contains("2026-10-24 10:00:00 UTC", html);
        Assert.Contains("3 verified", html);
        Assert.Contains("1 resolved", html);
        Assert.Contains("2 deferred", html);
        Assert.Contains("1,024", html);
        Assert.Contains("512", html);
        Assert.Contains("Sunday", html);
        Assert.Contains("02:00", html);
        Assert.Contains("UTC", html);
        Assert.Contains("Verify now", html);
        Assert.Equal(1, reads.Calls);
        Assert.Equal(0, writes.Previews);
    }

    [Fact]
    public async Task Paused_and_uninitialised_source_explains_why_manual_verification_is_unavailable()
    {
        var (html, _, _) = await RenderAsync(paused: true, baseline: false);
        Assert.Contains("Resume verification", html);
        Assert.Contains("complete baseline", html);
        Assert.Matches("(?s)<button[^>]*disabled[^>]*>\\s*Verify now", html);
    }

    [Fact]
    public async Task Interrupted_byte_measurement_is_labelled_as_a_lower_bound_without_downgrading_integrity()
    {
        var (html, _, _) = await RenderAsync(readCountsIncomplete: true);
        Assert.Contains("Durably recorded content read", html);
        Assert.Contains("at least 1,024 source bytes", html);
        Assert.Contains("at least 512 retained bytes", html);
        Assert.Contains("interrupted attempt", html);
        Assert.DoesNotContain("Actual content read:", html);
        Assert.Contains("3 verified", html);
    }

    private static async Task<(string Html, Reads Reads, Writes Writes)> RenderAsync(bool paused = false, bool baseline = true, bool readCountsIncomplete = false)
    {
        var reads = new Reads();
        reads.Status = reads.Status with { Paused = paused, Run = reads.Status.Run! with { ReadCountsIncomplete = readCountsIncomplete },
            Scanning = reads.Status.Scanning! with { BaselineEpoch = baseline ? 1 : 0, RecoveryRequired = !baseline } };
        var writes = new Writes();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<ISourceVerificationStore>(reads);
        services.AddSingleton(new NativeCorpusCommandService(writes, writes));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SourceVerificationPanel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["RootId"] = reads.Status.SourceRootId.Value, ["RootState"] = "Enabled" }));
            return component.ToHtmlString();
        });
        return (html, reads, writes);
    }

    private sealed class Reads : ISourceVerificationStore
    {
        public SourceVerificationStatus Status { get; set; } = new(SourceRootId.New(), SourceVerificationPolicy.Default("UTC"),
            false, null, new(Guid.NewGuid(), "Running", 10, 3, 1, 2, 1024, 512,
                DateTimeOffset.Parse("2026-10-25T01:00:00Z"), null, null, "pending-capture"))
        {
            FeatureEnabled = true, Scanning = new("Journal", null, 1, true, false, null, 0, 0,
                DateTimeOffset.Parse("2026-10-24T10:00:00Z"))
        };
        public bool Fail { get; set; }
        public Func<int, ValueTask<SourceVerificationStatus?>>? OnRead { get; set; }
        public int Calls { get; private set; }
        public ValueTask<Guid> StartAsync(SourceRootId id, bool manual, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<SourceVerificationStatus?> ReadAsync(SourceRootId id, CancellationToken ct)
        {
            Calls++;
            if (OnRead is not null) return OnRead(Calls);
            if (Fail) throw new IOException("Simulated SQL outage");
            return ValueTask.FromResult<SourceVerificationStatus?>(Status);
        }
    }

    private sealed class Writes : INativeOperationStore, INativeCorpusActionStore
    {
        public List<NativeActionCommitRequest> Commits { get; } = [];
        public int Previews { get; private set; }
        public Exception? FailNext { get; set; }
        public bool RecordReceiptOnFailure { get; set; }
        public int ReplayCalls { get; private set; }
        public string? LastReplayKey { get; private set; }
        public string? LastReplayConfirmation { get; private set; }
        public string? LastReplayPayload { get; private set; }
        private NativeActionReceipt? _receipt;
        public ValueTask<NativeActionReceipt?> FindReceiptAsync(string key, string surface, CancellationToken ct) => ValueTask.FromResult<NativeActionReceipt?>(null);
        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string payload, string confirmation, string key, string surface, CancellationToken ct)
        {
            ReplayCalls++; LastReplayKey = key; LastReplayConfirmation = confirmation; LastReplayPayload = payload;
            return ValueTask.FromResult(_receipt);
        }
        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken ct)
        {
            Previews++;
            return ValueTask.FromResult(new NativeActionPreview(Guid.NewGuid(), "confirmation-" + Previews, request.RequestFingerprint,
                DateTimeOffset.UtcNow.AddMinutes(5), request.Targets, request.EffectSummary));
        }
        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken ct)
        {
            Commits.Add(request);
            if (FailNext is { } exception)
            {
                FailNext = null;
                if (RecordReceiptOnFailure) _receipt = new(Guid.NewGuid(), true, "accepted", null);
                throw exception;
            }
            return ValueTask.FromResult(new NativeActionReceipt(Guid.NewGuid(), false, "accepted", null));
        }
        public ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action, string payload, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>([new("root", "version")]);
        public ValueTask<NativeActionCommitOperation> CreateCommitOperationAsync(string action, string payload,
            IReadOnlyList<NativeTargetVersion> targets, CancellationToken ct) =>
            ValueTask.FromResult<NativeActionCommitOperation>(new NativeCorpusMutationCommitOperation(action, payload));
    }

    private sealed class MutableHold : IDeploymentValidationHold
    {
        public bool IsHeld { get; set; }
        public ValueTask WaitUntilReleasedAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
