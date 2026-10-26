using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceReconciliationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_scans_do_not_starve_registration_of_already_retained_inputs(bool scanFails)
    {
        var claim = Claim();
        var restart = new RecordingRestartStore();
        var offeredBeforeAnotherScanClaim = false;
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ =>
                {
                    offeredBeforeAnotherScanClaim = restart.Offers > 0;
                    return ValueTask.FromResult<ClaimedSourceScan?>(null);
                }
            ]);
        ISourceScanner scanner = scanFails ? new ThrowingScanner() :
            new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0));
        await RunAsync(control, scanner, restart);

        Assert.True(offeredBeforeAnotherScanClaim, "Already retained inputs must be offered before another ready source scan is claimed.");
        var completion = Assert.Single(control.Completions);
        Assert.Equal(scanFails ? "InvalidOperationException" : null, completion.FailureReason);
        Assert.Equal(!scanFails, completion.Result.IsCompleteInventory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_failure_does_not_recomplete_or_reclassify_a_persisted_scan(bool scanFails)
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [_ => ValueTask.FromResult<ClaimedSourceScan?>(claim), _ => ValueTask.FromResult<ClaimedSourceScan?>(null)]);
        var restart = new RecordingRestartStore(_ => ValueTask.FromException<int>(new InvalidOperationException("registration failed")));
        ISourceScanner scanner = scanFails ? new ThrowingScanner() :
            new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(control, scanner, restart));

        Assert.Equal("registration failed", error.Message);
        Assert.Equal(1, control.ClaimAttempts);
        var completion = Assert.Single(control.Completions);
        Assert.Equal(scanFails ? "InvalidOperationException" : null, completion.FailureReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_persisting_a_scan_stops_registration_and_further_claims(bool scanFails)
    {
        using var cancellation = new CancellationTokenSource();
        var claim = Claim();
        var control = new RecordingControlStore([_ => ValueTask.FromResult<ClaimedSourceScan?>(claim)], afterCompletion: cancellation.Cancel);
        var restart = new RecordingRestartStore();
        ISourceScanner scanner = scanFails ? new ThrowingScanner() :
            new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(control, scanner, restart, cancellationToken: cancellation.Token));

        Assert.Equal(0, restart.Offers);
        Assert.Equal(1, control.ClaimAttempts);
        Assert.Single(control.Completions);
    }

    [Fact]
    public async Task Offering_retained_inputs_after_a_scan_wakes_the_outbox()
    {
        var claim = Claim();
        var offered = false;
        var restart = new RecordingRestartStore(_ =>
        {
            if (offered) return ValueTask.FromResult(0);
            offered = true;
            return ValueTask.FromResult(1);
        });
        var outbox = new RecordingOutboxWakeSignal();
        var control = new RecordingControlStore(
            [_ => ValueTask.FromResult<ClaimedSourceScan?>(claim), _ => ValueTask.FromResult<ClaimedSourceScan?>(null)]);

        await RunAsync(control, new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0)), restart, outbox);

        Assert.Equal(1, outbox.Notifications);
        Assert.Equal(2, restart.Offers);
        Assert.Single(control.Completions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpersisted_scan_completion_does_not_offer_registration_before_the_next_claim(bool scanFails)
    {
        var claim = Claim();
        var restart = new RecordingRestartStore();
        var offersBeforeAnotherClaim = -1;
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ =>
                {
                    offersBeforeAnotherClaim = restart.Offers;
                    return ValueTask.FromResult<ClaimedSourceScan?>(null);
                }
            ], throwOnCompletion: true);
        ISourceScanner scanner = scanFails ? new ThrowingScanner() :
            new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0));

        await RunAsync(control, scanner, restart);

        Assert.Equal(0, offersBeforeAnotherClaim);
        Assert.Single(control.Completions);
    }

    private sealed class RecordingOutboxWakeSignal : IOutboxWakeSignal
    {
        public int Notifications { get; private set; }
        public void Notify() => Notifications++;
        public ValueTask WaitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRestartStore(Func<CancellationToken, ValueTask<int>>? onOffer = null) : ISourceActivityRestartStore
    {
        public int Offers { get; private set; }
        public ValueTask<int> OfferUnlinkedInProcessActivitiesAsync(CancellationToken cancellationToken)
        {
            Offers++;
            return onOffer?.Invoke(cancellationToken) ?? ValueTask.FromResult(0);
        }
    }

    [Fact]
    public async Task Waiting_deletion_is_revisited_without_waiting_for_or_polling_source_reconciliation()
    {
        var deletionStore = new WaitingDeletionStore();
        var control = new RecordingControlStore([_ => ValueTask.FromResult<ClaimedSourceScan?>(null)]);
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddSingleton<ISourceScanner>(new UnusedScanner());
        services.AddSingleton(new SourceDeletionCoordinator(deletionStore));
        await using var provider = services.BuildServiceProvider();
        using var service = new SourceReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(),
            new ChannelSourceScanWakeSignal(), TimeProvider.System);

        try
        {
            await service.StartAsync(default);
            await deletionStore.Revisited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, control.ClaimAttempts);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Startup_wait_preserves_a_queued_wake_and_scans_once_after_database_readiness()
    {
        var claim = Claim();
        var control = new WakeControlStore(claim);
        var scanner = new CompletingScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0));
        var wake = new ChannelSourceScanWakeSignal();
        var readiness = new ControlledStartupReadiness();
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddSingleton<ISourceScanner>(scanner);
        await using var provider = services.BuildServiceProvider();
        using var service = new SourceReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(),
            wake, TimeProvider.System, databaseStartupReadiness: readiness);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await readiness.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            wake.Notify();
            wake.Notify();
            Assert.Equal(0, control.ClaimAttempts);
            Assert.Equal(0, scanner.Scans);

            readiness.Release();
            await scanner.Scanned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => control.ClaimAttempts == 3, TimeSpan.FromSeconds(2));
        }
        finally { await service.StopAsync(CancellationToken.None); }

        Assert.Equal(1, scanner.Scans);
        Assert.Equal(1, readiness.Waits);
    }

    private sealed class ControlledStartupReadiness : IDatabaseStartupReadiness
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waits { get; private set; }
        public bool IsReady => _ready.Task.IsCompletedSuccessfully;
        public void Release() => _ready.TrySetResult();
        public ValueTask WaitAsync(CancellationToken ct)
        {
            Waits++;
            Waiting.TrySetResult();
            return new(_ready.Task.WaitAsync(ct));
        }
    }

    private sealed class WaitingDeletionStore : ISourceDeletionStore
    {
        private int _claims;
        public TaskCompletionSource Revisited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _claims) >= 2) Revisited.TrySetResult();
            return ValueTask.FromResult<SourceDeletionWorkItem?>(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "draining"));
        }
        public ValueTask<SourceDeletionRunResult> PurgeAsync(SourceDeletionWorkItem workItem,
            IndexGenerationCandidateSnapshot? survivorGeneration, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SourceDeletionRunResult(false, "draining"));
        public ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(SourceDeletionWorkItem workItem,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RecordCleanupResultAsync(SourceDeletionWorkItem workItem, Guid cleanupItemId,
            SourceDeletionFileResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask FailAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DeferAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task RunAvailableAsync_uses_a_stable_opaque_lease_owner_for_the_service_lifetime()
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ]);

        await RunAsync(control, new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0)));

        Assert.Equal(2, control.LeaseOwners.Count);
        var owner = Assert.Single(control.LeaseOwners.Distinct(StringComparer.Ordinal));
        const string prefix = "source-reconciliation:";
        Assert.StartsWith(prefix, owner, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(owner[prefix.Length..], "N", out _));
    }

    [Fact]
    public async Task RunAvailableAsync_retries_a_transient_claim_failure()
    {
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromException<ClaimedSourceScan?>(new InvalidOperationException("transient claim failure")),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ]);

        await RunAsync(control, new UnusedScanner());

        Assert.Equal(2, control.ClaimAttempts);
        Assert.Empty(control.Completions);
    }

    [Fact]
    public async Task RunAvailableAsync_does_not_reclassify_a_successful_scan_when_completion_persistence_fails()
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ],
            throwOnCompletion: true);
        var scanner = new ResultScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 3, 2, 1, 0));

        await RunAsync(control, scanner);

        var completion = Assert.Single(control.Completions);
        Assert.Equal(claim, completion.Claim);
        Assert.Equal(3, completion.Result.DiscoveredCount);
        Assert.Equal(2, completion.Result.IndexedCount);
        Assert.Null(completion.FailureReason);
        Assert.Equal(2, control.ClaimAttempts);
    }

    [Fact]
    public async Task RunAvailableAsync_survives_a_failed_scan_completion_persistence_failure()
    {
        var claim = Claim();
        var control = new RecordingControlStore(
            [
                _ => ValueTask.FromResult<ClaimedSourceScan?>(claim),
                _ => ValueTask.FromResult<ClaimedSourceScan?>(null)
            ],
            throwOnCompletion: true);

        await RunAsync(control, new ThrowingScanner());

        var completion = Assert.Single(control.Completions);
        Assert.Equal(claim, completion.Claim);
        Assert.Equal(claim.SourceRoot.Id, completion.Result.SourceRootId);
        Assert.Equal(claim.ScanRequest.Id, completion.Result.SourceScanRequestId);
        Assert.Equal("InvalidOperationException", completion.FailureReason);
        Assert.Equal(2, control.ClaimAttempts);
    }

    [Fact]
    public async Task Watch_pump_drains_due_watcher_batches_without_claiming_source_scans()
    {
        var control = new RecordingControlStore([_ => ValueTask.FromResult<ClaimedSourceScan?>(null)]);
        var watchStore = new DueWatchStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddSingleton<ISourceScanner>(new UnusedScanner());
        await using var provider = services.BuildServiceProvider();
        var service = new SourceReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(), new ChannelSourceScanWakeSignal(), TimeProvider.System, new SourceWatchCoordinator(watchStore));

        var released = await service.PumpDueWatchBatchesAsync(CancellationToken.None);

        Assert.Equal(1, watchStore.Released);
        Assert.Equal(1, released);
        Assert.Equal(0, control.ClaimAttempts);
    }

    [Fact]
    public async Task Wake_runs_a_queued_scan_without_waiting_for_the_reconciliation_cadence()
    {
        var claim = Claim();
        var control = new WakeControlStore(claim);
        var scanner = new CompletingScanner(new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 0, 0, 0));
        var wake = new ChannelSourceScanWakeSignal();
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddSingleton<ISourceScanner>(scanner);
        await using var provider = services.BuildServiceProvider();
        var service = new SourceReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            wake,
            TimeProvider.System);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await control.InitialClaimFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            wake.Notify();

            await scanner.Scanned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => control.ClaimAttempts == 3, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, control.ClaimAttempts);
    }

    [Fact]
    public async Task Simultaneous_timer_and_wake_completion_preserves_the_consumed_wake()
    {
        using var waitCancellation = new CancellationTokenSource();
        var timer = Task.FromResult(true);
        var wake = Task.CompletedTask;

        var woke = await SourceReconciliationService.WaitForTimerOrWakeAsync(
            timer,
            wake,
            waitCancellation,
            CancellationToken.None);

        Assert.True(woke);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The reconciliation loop did not complete its expected claim cycle.");
    }

    private static async Task RunAsync(RecordingControlStore control, ISourceScanner scanner,
        ISourceActivityRestartStore? restart = null, IOutboxWakeSignal? outbox = null,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISourceScanControlStore>(control);
        services.AddSingleton(scanner);
        if (restart is not null) services.AddSingleton(restart);
        if (outbox is not null) services.AddSingleton(outbox);
        await using var provider = services.BuildServiceProvider();
        using var service = new SourceReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ChannelSourceScanWakeSignal(),
            TimeProvider.System);

        await service.RunAvailableAsync(cancellationToken);
    }

    private static ClaimedSourceScan Claim()
    {
        var rootId = new SourceRootId(Guid.NewGuid());
        var requestId = new SourceScanRequestId(Guid.NewGuid());
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00+00:00");
        return new ClaimedSourceScan(
            Guid.NewGuid(),
            "test-worker",
            1,
            SourceRootConfiguration.Restore(
                rootId,
                "C:\\source-reconciliation-service-tests",
                "Test source",
                true,
                false,
                1024,
                [],
                [],
                [],
                TimeSpan.FromMinutes(15),
                SourceRootState.Enabled,
                1),
            SourceScanRequest.Restore(
                requestId,
                rootId,
                "test",
                now,
                SourceScanRequestState.Running,
                now));
    }

    private sealed class RecordingControlStore(
        IEnumerable<Func<CancellationToken, ValueTask<ClaimedSourceScan?>>> claims,
        bool throwOnCompletion = false,
        Action? afterCompletion = null) : ISourceScanControlStore
    {
        private readonly Queue<Func<CancellationToken, ValueTask<ClaimedSourceScan?>>> _claims = new(claims);
        private readonly bool _throwOnCompletion = throwOnCompletion;

        public int ClaimAttempts { get; private set; }
        public List<string> LeaseOwners { get; } = [];
        public List<Completion> Completions { get; } = [];

        public ValueTask<ClaimedSourceScan?> ClaimNextReleasedAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimAttempts++;
            LeaseOwners.Add(leaseOwner);
            return _claims.Dequeue()(cancellationToken);
        }

        public ValueTask CompleteAsync(
            ClaimedSourceScan claim,
            SourceScanResult result,
            string? failureReason,
            CancellationToken cancellationToken)
        {
            Completions.Add(new Completion(claim, result, failureReason));
            afterCompletion?.Invoke();
            return _throwOnCompletion
                ? ValueTask.FromException(new InvalidOperationException("completion persistence failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed record Completion(ClaimedSourceScan Claim, SourceScanResult Result, string? FailureReason);

    private sealed class ResultScanner(SourceScanResult result) : ISourceScanner
    {
        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class ThrowingScanner : ISourceScanner
    {
        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceScanResult>(new InvalidOperationException("scanner failed"));
    }

    private sealed class UnusedScanner : ISourceScanner
    {
        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<SourceScanResult>(new InvalidOperationException("scanner should not run"));
    }

    private sealed class DueWatchStore : ISourceRootWatchStore
    {
        private bool _claimed;
        public int Released { get; private set; }
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>([]);
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset nowUtc, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            if (_claimed) return ValueTask.FromResult<ClaimedSourceWatchBatch?>(null);
            _claimed = true;
            return ValueTask.FromResult<ClaimedSourceWatchBatch?>(new ClaimedSourceWatchBatch(SourceRootId.New(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1, leaseOwner, 1));
        }
        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken cancellationToken) { Released++; return ValueTask.CompletedTask; }
    }

    private sealed class WakeControlStore(ClaimedSourceScan claim) : ISourceScanControlStore
    {
        private int _claimCount;
        public int ClaimAttempts { get; private set; }
        public TaskCompletionSource InitialClaimFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ClaimedSourceScan?> ClaimNextReleasedAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimAttempts++;
            if (_claimCount++ == 0)
            {
                InitialClaimFinished.TrySetResult();
                return ValueTask.FromResult<ClaimedSourceScan?>(null);
            }

            return ValueTask.FromResult<ClaimedSourceScan?>(_claimCount == 2 ? claim : null);
        }

        public ValueTask CompleteAsync(ClaimedSourceScan claim, SourceScanResult result, string? failureReason, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class CompletingScanner(SourceScanResult result) : ISourceScanner
    {
        public TaskCompletionSource Scanned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Scans { get; private set; }

        public ValueTask<SourceScanResult> ScanAsync(
            SourceRootConfiguration sourceRoot,
            SourceScanRequest scanRequest,
            CancellationToken cancellationToken)
        {
            Scans++;
            Scanned.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
