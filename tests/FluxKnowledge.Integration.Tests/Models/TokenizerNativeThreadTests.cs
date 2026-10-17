using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class TokenizerNativeThreadTests
{
    [Fact]
    public async Task Saturated_queue_applies_backpressure_and_finishes_cleanup_after_an_operation_fails()
    {
        using var release = new ManualResetEventSlim();
        using var started = new CountdownEvent(ProcessTokenizerNativeThread.QueueCapacity + 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = Task.Run(() => ProcessTokenizerNativeThread.Invoke(() =>
        {
            entered.SetResult();
            release.Wait();
        }));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var completed = 0;
        var cleanup = 0;
        var callers = Enumerable.Range(0, ProcessTokenizerNativeThread.QueueCapacity + 1).Select(index => new Thread(() =>
        {
            started.Signal();
            try
            {
                ProcessTokenizerNativeThread.Invoke(() =>
                {
                    Interlocked.Increment(ref completed);
                    if (index == 0) throw new InvalidOperationException("Expected queued failure.");
                    if (index == ProcessTokenizerNativeThread.QueueCapacity) Interlocked.Increment(ref cleanup);
                });
            }
            catch (Exception exception) { failures.Enqueue(exception); }
        }) { IsBackground = true }).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var caller in callers) caller.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(() => ProcessTokenizerNativeThread.PendingOperations == ProcessTokenizerNativeThread.QueueCapacity,
                TimeSpan.FromSeconds(10)), "The native queue did not saturate.");
            Assert.Equal(0, Volatile.Read(ref completed));
        }
        finally { release.Set(); }
        await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var caller in callers) Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(ProcessTokenizerNativeThread.QueueCapacity + 1, completed);
        Assert.Equal(1, cleanup);
        Assert.Equal("Expected queued failure.", Assert.Single(failures).Message);
    }

    [Fact]
    public async Task Concurrent_callers_complete_on_one_stable_owner_without_overlapping_native_work()
    {
        var active = 0;
        var ownerIds = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            ProcessTokenizerNativeThread.Invoke(() =>
            {
                Assert.Equal(1, Interlocked.Increment(ref active));
                try
                {
                    Assert.True(Thread.CurrentThread.IsBackground);
                    return Environment.CurrentManagedThreadId;
                }
                finally { Interlocked.Decrement(ref active); }
            }))));
        var owner = Assert.Single(ownerIds.Distinct());
        Assert.Equal(owner, ProcessTokenizerNativeThread.Invoke(() => Environment.CurrentManagedThreadId));
        Assert.Equal(0, active);
    }

    [Fact]
    public void Native_failure_reaches_its_caller_and_the_same_owner_remains_usable()
    {
        var owner = ProcessTokenizerNativeThread.Invoke(() => Environment.CurrentManagedThreadId);
        var expected = new InvalidOperationException("Synthetic native failure.");
        var actual = Assert.Throws<InvalidOperationException>(() => ProcessTokenizerNativeThread.Invoke<int>(() => throw expected));
        Assert.Same(expected, actual);
        Assert.Equal(owner, ProcessTokenizerNativeThread.Invoke(() => Environment.CurrentManagedThreadId));
    }

    [Fact]
    public void Reentrant_native_work_does_not_queue_to_its_own_owner()
    {
        var pair = ProcessTokenizerNativeThread.Invoke(() =>
            (Environment.CurrentManagedThreadId, ProcessTokenizerNativeThread.Invoke(() => Environment.CurrentManagedThreadId)));
        Assert.Equal(pair.Item1, pair.Item2);
    }

    [Fact]
    public void Caller_execution_context_does_not_flow_into_native_work()
    {
        var ambient = new AsyncLocal<string?> { Value = "Caller request context." };
        Assert.Null(ProcessTokenizerNativeThread.Invoke(() => ambient.Value));
        Assert.Equal("Caller request context.", ambient.Value);
    }
}
