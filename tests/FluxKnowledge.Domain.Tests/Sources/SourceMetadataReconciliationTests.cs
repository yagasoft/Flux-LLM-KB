using FluxKnowledge.Integrations.Files;
using System.Threading.Channels;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceMetadataReconciliationTests
{
    [Fact]
    public async Task Metadata_credit_has_a_bounded_burst_and_a_long_pause_cannot_bank_unlimited_work()
    {
        var clock = new Clock();
        var pacer = new SourceMetadataPacer(2, clock);
        await pacer.WaitAsync(default); await pacer.WaitAsync(default);
        var next = pacer.WaitAsync(default).AsTask();
        Assert.False(next.IsCompleted);
        var delay = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(500), delay.Due);
        clock.Now += delay.Due; delay.Fire();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now += TimeSpan.FromDays(1);
        await pacer.WaitAsync(default); await pacer.WaitAsync(default);
        using var cancellation = new CancellationTokenSource();
        var bounded = pacer.WaitAsync(cancellation.Token).AsTask();
        Assert.False(bounded.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded);
    }

    [Fact]
    public async Task Cancellation_is_observed_even_when_credit_is_available()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SourceMetadataPacer(64).WaitAsync(cancellation.Token).AsTask());
    }
    [Fact]
    public void Native_directory_query_restarts_with_the_documented_bit_and_never_expands_cloud_namespaces()
    {
        Assert.Equal(0x9u, WindowsSourcePathAccess.DirectoryQueryFlags(restart: true));
        Assert.Equal(0x8u, WindowsSourcePathAccess.DirectoryQueryFlags(restart: false));
    }

    private sealed class Clock : TimeProvider
    {
        internal TimeSpan Now;
        internal Channel<Delay> Timers { get; } = Channel.CreateUnbounded<Delay>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Delay(callback, state, dueTime); Timers.Writer.TryWrite(timer); return timer;
        }
    }
    private sealed class Delay(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        internal TimeSpan Due => due;
        private bool _disposed;
        internal void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
