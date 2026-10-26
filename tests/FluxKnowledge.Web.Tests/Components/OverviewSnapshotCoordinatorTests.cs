using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OverviewSnapshotCoordinatorTests
{
    [Theory]
    [InlineData(true, 0, 0, true)]
    [InlineData(true, 0, 3621, true)]
    [InlineData(false, 0, 3621, false)]
    [InlineData(true, 3621, 3621, false)]
    [InlineData(false, 0, 0, false)]
    [InlineData(true, 0, 1205, false)]
    [InlineData(true, -2, -2, false)]
    public async Task Only_SQL_command_cancellation_at_the_owned_deadline_is_busy(
        bool waitForDeadline, int firstError, int secondError, bool busy)
    {
        var reader = new ControlledReader();
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed());
        await shared.RefreshAsync(default);
        var original = shared.Current;
        reader.Failure = SqlProjectionReadBudgetTests.SqlFailure(firstError, secondError);
        reader.WaitForDeadline = waitForDeadline;
        await shared.RefreshAsync(default, force: true).AsTask().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(busy ? "Status is busy. Waiting for a consistent sample." : "Status refresh failed.", shared.Current.RefreshIssue);
        Assert.Same(original.Projection, shared.Current.Projection);
        Assert.Equal(original.PublishedAtUtc, shared.Current.PublishedAtUtc);
        reader.Failure = null;
        reader.WaitForDeadline = false;
        reader.Result = Sample(9);
        await shared.RefreshAsync(default, force: true).AsTask().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Null(shared.Current.RefreshIssue);
        Assert.Equal(9, shared.Current.Projection!.IndexedRecordCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connection_hints_reuse_a_fresh_sample_and_refresh_an_old_sample(bool fresh)
    {
        var now = DateTimeOffset.UtcNow;
        var feed = new StatusEventFeed();
        var reader = new ControlledReader { Result = Sample(7) with
            { Activity = new(now.AddSeconds(fresh ? 0 : -20), now, [], [], [], 0, 0, 0) } };
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), feed);
        using var subscription = shared.Subscribe();
        await shared.RefreshAsync(default);
        await feed.PublishAsync(new(null, "reconnect", now), default);
        if (fresh)
        {
            await WaitUntilAsync(() => shared.Metrics.CacheHits > 0 || reader.Reads > 1);
            Assert.Equal(1, reader.Reads);
            Assert.Equal(1, shared.Metrics.CacheHits);
            Assert.Null(shared.Current.RefreshIssue);
        }
        else
        {
            await reader.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(2, reader.Reads);
        }
    }

    [Fact]
    public async Task Inconsistent_samples_keep_the_original_sample_and_the_specific_refresh_notice()
    {
        var reader = new ControlledReader();
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed());
        await shared.RefreshAsync(default);
        var original = shared.Current.Projection;
        var changed = Sample(9);
        reader.Result = changed with { Activity = changed.Activity with { IsConsistent = false } };
        await shared.RefreshAsync(default, force: true);
        Assert.Same(original, shared.Current.Projection);
        Assert.Equal("Status changed during the read. Waiting for a consistent sample.", shared.Current.RefreshIssue);
        Assert.Equal(1, shared.Metrics.Completed);
        Assert.Equal(1, shared.Metrics.Failed);
    }

    [Fact]
    public async Task Periodic_fallback_refreshes_at_the_next_tick_even_when_the_initial_sample_is_off_boundary()
    {
        var clock = new ManualClock();
        var reader = new ControlledReader();
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed(), clock);
        using var subscription = shared.Subscribe();
        clock.Advance(TimeSpan.FromSeconds(1));
        reader.Result = Sample(7) with { Activity = new(clock.GetUtcNow(), clock.GetUtcNow(), [], [], [], 0, 0, 0) };
        await shared.RefreshAsync(default);
        clock.Advance(TimeSpan.FromSeconds(9));
        await WaitUntilAsync(() => clock.HasOneShotTimer || reader.Reads == 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        await reader.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, reader.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalidation_stays_visible_until_a_sample_started_after_it_covers_it(bool duringRead)
    {
        var feed = new StatusEventFeed();
        var reader = new ControlledReader();
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), feed);
        using var subscription = shared.Subscribe();
        await shared.RefreshAsync(default);
        reader.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = shared.RefreshAsync(default, force: true).AsTask();
        if (duringRead) await reader.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var coalesced = shared.Metrics.Coalesced;
        await feed.PublishAsync(new(null, "sources", DateTimeOffset.UtcNow), default);
        await WaitUntilAsync(() => shared.Metrics.Coalesced > coalesced);
        Assert.NotNull(shared.Current.RefreshIssue);
        reader.Release.SetResult();
        await second;
        Assert.Equal(2, shared.Metrics.Completed);
        if (duringRead)
        {
            Assert.NotNull(shared.Current.RefreshIssue);
            await shared.RefreshAsync(default);
        }
        Assert.Null(shared.Current.RefreshIssue);
        Assert.Equal(duringRead ? 3 : 2, reader.Reads);
    }

    [Fact]
    public async Task Observing_a_changed_deployment_hold_queues_one_coalesced_refresh()
    {
        var hold = new MutableHold();
        var reader = new ControlledReader();
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed(), hold: hold);
        using var subscription = shared.Subscribe();
        await shared.RefreshAsync(default);
        reader.Result = Sample(9) with { DeploymentHeld = true };
        hold.IsHeld = true;
        for (var i = 0; i < 20; i++) Assert.NotNull(shared.Current.RefreshIssue);
        await reader.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await shared.RefreshAsync(default);
        Assert.Equal(2, reader.Reads);
        Assert.Null(shared.Current.RefreshIssue);
    }

    [Fact]
    public async Task Host_shutdown_cancels_the_owned_read_and_disposal_awaits_its_cleanup()
    {
        var lifetime = new Lifetime();
        var reader = new ControlledReader { HoldCancellation = true };
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed(), lifetime: lifetime);
        var refresh = shared.RefreshAsync(default).AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lifetime.StopApplication();
        await reader.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dispose = shared.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        reader.CleanupRelease.SetResult();
        await Task.WhenAll(dispose, refresh).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(shared.Current.Projection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SQL_cancellation_at_shutdown_is_quiet_and_disposal_still_awaits_cleanup(bool terminationNotice)
    {
        var lifetime = new Lifetime();
        var reader = new ControlledReader { Failure = terminationNotice ? SqlProjectionReadBudgetTests.SqlFailure(0, 0, 3621) : SqlProjectionReadBudgetTests.SqlFailure(0),
            WaitForDeadline = true, HoldCancellation = true };
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed(), lifetime: lifetime);
        var refresh = shared.RefreshAsync(default).AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lifetime.StopApplication();
        await reader.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dispose = shared.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        reader.CleanupRelease.SetResult();
        await Task.WhenAll(dispose, refresh).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(shared.Current.RefreshIssue);
        Assert.Equal(0, shared.Metrics.Failed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_read_and_cancelling_a_waiter_does_not_cancel_it()
    {
        var reader = new ControlledReader();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed());
        using var caller = new CancellationTokenSource();
        var first = shared.RefreshAsync(caller.Token).AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var others = Enumerable.Range(0, 3).Select(_ => shared.RefreshAsync(default).AsTask()).ToArray();
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        reader.Release.TrySetResult();
        await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(1, reader.Reads);
        Assert.NotNull(shared.Current.Projection);
    }

    [Fact]
    public async Task Cache_hits_preserve_the_sample_and_its_timestamp_and_freeze_source_collections()
    {
        var sources = new List<SourcePipelineProgress>();
        var reader = new ControlledReader { Result = Sample(7, sources) };
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed());
        await shared.RefreshAsync(default);
        var original = shared.Current.Projection;
        sources.Add(new(Guid.NewGuid(), "Late mutation", "synthetic", "Enabled", 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, false, null));
        await shared.RefreshAsync(default);
        Assert.Equal(1, reader.Reads);
        Assert.Same(original, shared.Current.Projection);
        Assert.Empty(shared.Current.Projection!.Activity.Sources);
    }

    [Fact]
    public async Task Failure_preserves_the_last_good_sample_and_a_later_success_clears_the_issue()
    {
        var reader = new ControlledReader();
        reader.Release.SetResult();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), new StatusEventFeed());
        await shared.RefreshAsync(default);
        var original = shared.Current.Projection;
        reader.Fail = true;
        await shared.RefreshAsync(default, force: true);
        Assert.Same(original, shared.Current.Projection);
        Assert.NotNull(shared.Current.RefreshIssue);
        reader.Fail = false;
        reader.Result = Sample(9);
        await shared.RefreshAsync(default, force: true);
        Assert.Equal(9, shared.Current.Projection!.IndexedRecordCount);
        Assert.Null(shared.Current.RefreshIssue);
    }

    [Fact]
    public async Task A_status_event_during_a_read_queues_one_follow_up_without_delaying_the_first_caller()
    {
        var feed = new StatusEventFeed();
        var reader = new ControlledReader();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), feed);
        using var subscription = shared.Subscribe();
        var first = shared.RefreshAsync(default).AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 100; i++) await feed.PublishAsync(new(null, "sources", DateTimeOffset.UtcNow), default);
        // Let the event observer consume the burst while the first read is held.
        await Task.Delay(100);
        reader.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        await reader.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Events_without_dashboard_subscribers_do_not_start_database_reads()
    {
        var feed = new StatusEventFeed();
        var reader = new ControlledReader();
        using var services = new ServiceCollection().AddSingleton<IProjectionReader>(reader).BuildServiceProvider();
        await using var shared = new OverviewSnapshotCoordinator(services.GetRequiredService<IServiceScopeFactory>(), feed);
        await feed.PublishAsync(new(null, "sources", DateTimeOffset.UtcNow), default);
        await Task.Delay(100);
        Assert.Equal(0, reader.Reads);
    }

    private static OverviewProjection Sample(int indexed, IReadOnlyList<SourcePipelineProgress>? sources = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(0, 0, 0, 0, 0, 0, indexed, "none", new("Healthy", null, null, null, null, 0))
        { Activity = new(now, now, sources ?? [], [], [], 0, 0, 0), ReadConsistency = "Snapshot" };
    }

    private sealed class ControlledReader : IProjectionReader
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public bool Fail { get; set; }
        public OverviewProjection Result { get; set; } = Sample(7);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldCancellation { get; init; }
        public Exception? Failure { get; set; }
        public bool WaitForDeadline { get; set; }
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _reads) == 2) SecondRead.TrySetResult();
            Started.TrySetResult();
            if (Failure is { } failure)
            {
                if (WaitForDeadline)
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException)
                    {
                        CancellationObserved.TrySetResult();
                        if (HoldCancellation) await CleanupRelease.Task;
                    }
                throw failure;
            }
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (HoldCancellation)
            {
                CancellationObserved.TrySetResult();
                await CleanupRelease.Task;
                throw;
            }
            if (Fail) throw new InvalidOperationException("Synthetic read failure");
            return Result;
        }
        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MutableHold : IDeploymentValidationHold
    {
        public bool IsHeld { get; set; }
        public ValueTask WaitUntilReleasedAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => _stopping.Cancel();
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public bool HasOneShotTimer { get { lock (_sync) return _timers.Any(t => t.Due != DateTimeOffset.MaxValue && t.Period == Timeout.InfiniteTimeSpan); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] ready;
            lock (_sync)
            {
                _now += elapsed;
                ready = _timers.Where(t => t.Due <= _now).ToArray();
                foreach (var timer in ready) timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : DateTimeOffset.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) { Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime; Period = period; }
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner._sync) Due = DateTimeOffset.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
