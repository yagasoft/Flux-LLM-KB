using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Web;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed partial class BgeCpuWarmupServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosted_memory_refusal_waits_a_minute_and_shutdown_cancels_the_retry(bool stopBeforeRetry)
    {
        var clock = new WarmupRetryClock();
        var attempts = 0;
        var disposed = 0;
        var logger = new RecordingLogger();
        await using var pool = new ResidentCpuPassageInference(_ => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException<CpuResidentPassageLoad>(new CpuWarmupMemoryUnavailableException())
            : Task.FromResult(Load(() => { }, () => Interlocked.Increment(ref disposed))),
            new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        using var service = new BgeCpuWarmupService(pool, logger, clock);
        await service.StartAsync(default);
        await WaitForAsync(() => clock.HasTimer);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.Delay);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, attempts);
        if (stopBeforeRetry) await service.StopAsync(default);
        clock.Advance(TimeSpan.FromSeconds(1));
        if (!stopBeforeRetry)
        {
            await WaitForAsync(() => logger.Entries.Any(entry => entry.Message.Contains("search is ready", StringComparison.Ordinal)));
            Assert.Equal(2, attempts);
            Assert.Equal(7, await pool.ExecuteAsync((_, _, _) => ValueTask.FromResult(7), default));
            await service.StopAsync(default);
            Assert.Equal(1, disposed);
        }
        else
        {
            Assert.Equal(1, attempts);
            Assert.Equal(0, disposed);
            Assert.False(clock.HasTimer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosted_nonretryable_failure_never_schedules_memory_retry(bool lateMemory)
    {
        var clock = new WarmupRetryClock();
        var logger = new RecordingLogger();
        var attempts = 0;
        Exception failure = lateMemory ? new BgeInferenceException("cpu-search-memory-unavailable") : new IOException("native-failure");
        await using var pool = new ResidentCpuPassageInference(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<CpuResidentPassageLoad>(failure);
        }, new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        using var service = new BgeCpuWarmupService(pool, logger, clock);
        await service.StartAsync(default);
        await WaitForAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Error));
        Assert.False(clock.HasTimer);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, attempts);
        await service.StopAsync(default);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    [Fact]
    public async Task Initial_memory_refusal_can_retry_once_concurrently_without_cancelling_shared_loading()
    {
        var attempts = 0;
        var disposed = 0;
        var second = new TaskCompletionSource<CpuResidentPassageLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ResidentCpuPassageInference(_ => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException<CpuResidentPassageLoad>(new CpuWarmupMemoryUnavailableException()) : second.Task,
            new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        await Assert.ThrowsAsync<CpuWarmupMemoryUnavailableException>(() => pool.WarmAsync(default));
        using var cancelled = new CancellationTokenSource();
        var retry = pool.WarmAsync(cancelled.Token);
        var others = Enumerable.Range(0, 8).Select(_ => pool.WarmAsync(default)).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        second.SetResult(Load(() => { }, () => Interlocked.Increment(ref disposed)));
        await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(7, await pool.ExecuteAsync((_, _, _) => ValueTask.FromResult(7), default));
        await pool.DisposeAsync();
        Assert.Equal(1, disposed);
    }

    [Theory]
    [InlineData("late-memory")]
    [InlineData("uncertain")]
    [InlineData("configuration")]
    [InlineData("native")]
    public async Task Later_memory_or_unconfirmed_or_other_failures_are_never_retried(string kind)
    {
        Exception original = kind switch
        {
            "late-memory" => new BgeInferenceException("cpu-search-memory-unavailable"),
            "uncertain" => new AggregateException(new CpuWarmupMemoryUnavailableException()),
            "configuration" => new InvalidOperationException("model-configuration-invalid"),
            _ => new IOException("native-failure")
        };
        var attempts = 0;
        await using var pool = new ResidentCpuPassageInference(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<CpuResidentPassageLoad>(original);
        }, new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        for (var i = 0; i < 3; i++)
            Assert.Same(original, await Record.ExceptionAsync(() => pool.WarmAsync(default)));
        Assert.Equal(1, attempts);
    }

    private sealed class WarmupRetryClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<RetryTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public bool HasTimer { get { lock (_sync) return _timers.Any(timer => timer.Due != DateTimeOffset.MaxValue); } }
        public TimeSpan? Delay { get { lock (_sync) return _timers.Where(timer => timer.Due != DateTimeOffset.MaxValue).Select(timer => (TimeSpan?)(timer.Due - _now)).Min(); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (_sync)
            {
                var timer = new RetryTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            RetryTimer[] ready;
            lock (_sync)
            {
                _now += elapsed;
                ready = _timers.Where(timer => timer.Due <= _now).ToArray();
                foreach (var timer in ready) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class RetryTimer(WarmupRetryClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner._sync) Due = DateTimeOffset.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
