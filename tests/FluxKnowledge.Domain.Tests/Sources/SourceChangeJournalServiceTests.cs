using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceChangeJournalServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_intake_loop_pumps_verification_and_wakes_the_same_dispatcher(bool intakeUnavailable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualClock();
        var intake = new RecordingCoordinator { Unavailable = intakeUnavailable };
        var verification = new RecordingVerificationScheduler();
        var wake = new RecordingScanWake();
        using var services = new ServiceCollection().AddSingleton<ISourceIncrementalCoordinator>(intake)
            .AddSingleton<ISourceVerificationScheduler>(verification).BuildServiceProvider();
        using var service = new SourceChangeJournalService(services.GetRequiredService<IServiceScopeFactory>(), wake,
            new() { Enabled = true }, NullLogger<SourceChangeJournalService>.Instance, timeProvider: clock);
        await service.StartAsync(deadline.Token);
        _ = await clock.Timers.Reader.ReadAsync(deadline.Token);
        _ = await clock.Timers.Reader.ReadAsync(deadline.Token); // End of first whole round.
        Assert.Equal(1, verification.Count);
        Assert.Equal(1, wake.Count);
        Assert.Equal(1, intake.Count);
        await service.StopAsync(deadline.Token);
    }

    private sealed class RecordingVerificationScheduler : ISourceVerificationScheduler
    {
        public int Count { get; private set; }
        public ValueTask<int> PumpAsync(CancellationToken cancellationToken) { Count++; return ValueTask.FromResult(1); }
    }

    [Fact]
    public async Task Startup_catchup_bounded_wakes_and_periodic_poll_share_one_serial_loop()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualClock();
        var intake = new RecordingCoordinator();
        var scanWake = new RecordingScanWake();
        var journalWake = new ChannelSourceJournalWakeSignal();
        using var services = new ServiceCollection().AddSingleton<ISourceIncrementalCoordinator>(intake).BuildServiceProvider();
        using var service = new SourceChangeJournalService(services.GetRequiredService<IServiceScopeFactory>(), scanWake,
            new() { Enabled = true, PollSeconds = 2 }, NullLogger<SourceChangeJournalService>.Instance,
            journalWake: journalWake, timeProvider: clock);
        await service.StartAsync(deadline.Token);
        var healthTimer = await clock.Timers.Reader.ReadAsync(deadline.Token);
        Assert.Equal(TimeSpan.FromSeconds(2), healthTimer.DueTime);
        Assert.Equal(1, await intake.Pumps.Reader.ReadAsync(deadline.Token)); // No watcher is needed at startup.
        var firstFloor = await clock.Timers.Reader.ReadAsync(deadline.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), firstFloor.DueTime);
        for (var index = 0; index < 1000; index++) journalWake.Notify();
        Assert.Equal(1, intake.Count); // Hints cannot bypass the minimum interval.
        firstFloor.Fire();
        Assert.Equal(2, await intake.Pumps.Reader.ReadAsync(deadline.Token));
        var secondFloor = await clock.Timers.Reader.ReadAsync(deadline.Token);
        secondFloor.Fire();
        healthTimer.Fire();
        Assert.Equal(3, await intake.Pumps.Reader.ReadAsync(deadline.Token)); // Poll without a hint.
        _ = await clock.Timers.Reader.ReadAsync(deadline.Token);
        await service.StopAsync(deadline.Token);
        Assert.Equal(3, intake.Count);
        Assert.Equal(0, scanWake.Count); // Quiet metadata intake is not new content work.
        Assert.Equal(1, intake.MaximumConcurrent);
    }

    [Fact]
    public async Task Queued_work_wakes_scanning_and_shutdown_cancels_the_existing_intake()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualClock();
        var intake = new RecordingCoordinator { Queued = 1 };
        var scanWake = new RecordingScanWake();
        using var services = new ServiceCollection().AddSingleton<ISourceIncrementalCoordinator>(intake).BuildServiceProvider();
        using var service = new SourceChangeJournalService(services.GetRequiredService<IServiceScopeFactory>(), scanWake,
            new() { Enabled = true }, NullLogger<SourceChangeJournalService>.Instance, timeProvider: clock);
        await service.StartAsync(deadline.Token);
        var healthTimer = await clock.Timers.Reader.ReadAsync(deadline.Token);
        _ = await intake.Pumps.Reader.ReadAsync(deadline.Token);
        var floor = await clock.Timers.Reader.ReadAsync(deadline.Token);
        Assert.Equal(1, scanWake.Count);
        intake.Block = true;
        // The periodic deadline releases the next bounded round.
        floor.Fire();
        healthTimer.Fire();
        Assert.Equal(2, await intake.Pumps.Reader.ReadAsync(deadline.Token));
        await service.StopAsync(deadline.Token);
        Assert.True(intake.Cancelled);
        Assert.Equal(2, intake.Count);
        Assert.Equal(1, scanWake.Count);
    }

    [Fact]
    public async Task Watcher_persistence_wakes_journal_intake_after_commit_without_consuming_the_scan_wake()
    {
        var store = new WatchStore();
        var scanWake = new RecordingScanWake();
        var journalWake = new ChannelSourceJournalWakeSignal();
        var coordinator = new SourceWatchCoordinator(store, scanWake, journalWake);
        using var cancellation = new CancellationTokenSource();
        var waiting = journalWake.WaitAsync(cancellation.Token).AsTask();
        await coordinator.RecordAsync(new(SourceRootId.New(), SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch, [new("one.txt")]), default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.Recorded);
        Assert.Equal(0, scanWake.Count);
    }

    private sealed class RecordingScanWake : ISourceScanWakeSignal
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Notify() => Interlocked.Increment(ref _count);
        public ValueTask WaitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class WatchStore : ISourceRootWatchStore
    {
        public bool Recorded { get; private set; }
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken) { Recorded = true; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset nowUtc, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingCoordinator : ISourceIncrementalCoordinator
    {
        private int _count;
        private int _concurrent;
        public int Count => Volatile.Read(ref _count);
        public int MaximumConcurrent { get; private set; }
        public int Queued { get; set; }
        public bool Block { get; set; }
        public bool Unavailable { get; set; }
        public bool Cancelled { get; private set; }
        public Channel<int> Pumps { get; } = Channel.CreateUnbounded<int>();
        public async ValueTask<int> PumpAsync(CancellationToken ct)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _concurrent));
            try
            {
                Pumps.Writer.TryWrite(Interlocked.Increment(ref _count));
                if (Unavailable) throw new IOException("journal-unavailable-fixture");
                if (Block)
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { Cancelled = true; throw; }
                return Queued;
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public ValueTask<SourceIncrementalPlan?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<SourceRevisionId?> TryReuseAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask RecordCaptureAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, SourceDiscoveredFile file, SourceRevisionId revision, ISourceFileCapture? capture, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> CompleteAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, IReadOnlyList<SourceNamespaceObservation> directories, IReadOnlyList<SourceEnumerationEvidence> evidence, IReadOnlySet<SourceRevisionId> seen, GitInventoryEvidence? inventory, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        public Channel<ManualTimer> Timers { get; } = Channel.CreateUnbounded<ManualTimer>();
        public ManualTimer? Latest { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Latest = timer;
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private bool _disposed;
        public TimeSpan DueTime { get; } = dueTime;
        public void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan next, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
