using System.Threading.Channels;
using FluxKnowledge.Application.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceVerificationReadBudgetTests
{
    [Fact]
    public async Task Source_and_retained_reads_share_one_chunk_burst_and_the_combined_rate()
    {
        var clock = new Clock();
        using var budget = new SourceVerificationReadBudget(2 * 1024 * 1024, clock);
        await budget.AcquireAsync(128 * 1024, default); // Source chunk consumes the single initial burst.
        var retained = budget.AcquireAsync(128 * 1024, default).AsTask();
        var delay = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(retained.IsCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(63), delay.Due);
        clock.Advance(delay);
        await retained.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Long_idle_time_cannot_accumulate_more_than_one_chunk_of_burst_credit()
    {
        var clock = new Clock();
        using var budget = new SourceVerificationReadBudget(2 * 1024 * 1024, clock);
        await budget.AcquireAsync(128 * 1024, default);
        clock.Now += TimeSpan.FromDays(1);
        await budget.AcquireAsync(128 * 1024, default);
        using var cancellation = new CancellationTokenSource();
        var next = budget.AcquireAsync(128 * 1024, cancellation.Token).AsTask();
        var delay = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.IsCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(63), delay.Due);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.True(delay.Disposed);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_spend_the_same_credit_and_cancellation_releases_the_waiter()
    {
        var clock = new Clock();
        using var budget = new SourceVerificationReadBudget(2 * 1024 * 1024, clock);
        await budget.AcquireAsync(128 * 1024, default);
        using var cancellation = new CancellationTokenSource();
        var first = budget.AcquireAsync(128 * 1024, cancellation.Token).AsTask();
        var delay = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var second = budget.AcquireAsync(128 * 1024, default).AsTask();
        Assert.False(second.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var nextDelay = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delay.Disposed);
        Assert.False(second.IsCompleted);
        clock.Advance(nextDelay);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(128 * 1024 + 1)]
    public async Task Invalid_chunk_does_not_enter_a_delay(int bytes)
    {
        var clock = new Clock();
        using var budget = new SourceVerificationReadBudget(2 * 1024 * 1024, clock);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => budget.AcquireAsync(bytes, default).AsTask());
        Assert.False(clock.Timers.Reader.TryRead(out _));
    }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Now { get; set; }
        public Channel<Delay> Timers { get; } = Channel.CreateUnbounded<Delay>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var result = new Delay(callback, state, dueTime);
            Timers.Writer.TryWrite(result);
            return result;
        }
        public void Advance(Delay delay) { Now += delay.Due; delay.Fire(); }
    }
    private sealed class Delay(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan next, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
