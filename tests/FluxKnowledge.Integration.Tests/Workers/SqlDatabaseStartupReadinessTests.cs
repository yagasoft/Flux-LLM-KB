using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class SqlDatabaseStartupReadinessTests
{
    [Fact]
    public async Task Temporary_unavailability_retries_only_the_probe_before_releasing_waiters()
    {
        var probes = 0;
        var delays = new List<TimeSpan>();
        var durableOperations = 0;
        var readiness = Gate(_ => ++probes < 3
            ? Task.FromException(new DatabaseStartupUnavailableException(2)) : Task.CompletedTask,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        await readiness.WaitAsync(CancellationToken.None);
        durableOperations++;

        Assert.True(readiness.IsReady);
        Assert.Equal(3, probes);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
        Assert.Equal(1, durableOperations);
    }

    [Fact]
    public async Task Concurrent_waiters_share_one_probe_and_caller_cancellation_does_not_cancel_it()
    {
        var probes = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readiness = Gate(async ct => { Interlocked.Increment(ref probes); entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        using var cancelled = new CancellationTokenSource();
        var first = readiness.WaitAsync(cancelled.Token).AsTask();
        var others = Enumerable.Range(0, 16).Select(_ => readiness.WaitAsync(CancellationToken.None).AsTask()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(readiness.IsReady);
        Assert.All(others, task => Assert.False(task.IsCompleted));
        release.SetResult();
        await Task.WhenAll(others);
        Assert.Equal(1, probes);
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public async Task Shutdown_cancels_backoff_without_a_new_probe_or_false_readiness()
    {
        var probes = 0;
        var delaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var readiness = Gate(_ => { probes++; return Task.FromException(new DatabaseStartupUnavailableException(2)); },
            async (_, ct) => { delaying.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }, shutdown.Token);
        var wait = readiness.WaitAsync(CancellationToken.None).AsTask();
        await delaying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(1, probes);
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public async Task Unclassified_failure_is_not_retried_or_reported_ready()
    {
        var failure = new InvalidOperationException("synthetic-integrity-or-configuration-failure");
        var delays = 0;
        var readiness = Gate(_ => Task.FromException(failure), (_, _) => { delays++; return Task.CompletedTask; });
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.WaitAsync(CancellationToken.None).AsTask()));
        Assert.Equal(0, delays);
        Assert.False(readiness.IsReady);
    }

    private static SqlDatabaseStartupReadiness Gate(Func<CancellationToken, Task> probe,
        Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken shutdown = default) =>
        new(probe, delay ?? ((_, _) => Task.CompletedTask), shutdown, NullLogger<SqlDatabaseStartupReadiness>.Instance);
}
