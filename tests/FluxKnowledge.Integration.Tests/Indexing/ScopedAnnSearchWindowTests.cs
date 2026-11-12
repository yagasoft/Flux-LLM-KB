using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class ScopedAnnSearchWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_or_cancellation_waits_for_the_other_actual_native_callback(bool cancelled)
    {
        using var release=new ManualResetEventSlim();
        var firstEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=0;var released=false;
        var running=ScopedAnnSearchWindow.RunAsync(
            ()=>{Interlocked.Increment(ref active);firstEntered.TrySetResult();try{Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return ValueTask.FromResult<IReadOnlyList<AnnMatch>>([new(1,0)]);}finally{Interlocked.Decrement(ref active);released=true;}},
            ()=>{secondEntered.TrySetResult();if(cancelled)throw new OperationCanceledException(new CancellationToken(true));throw new InvalidOperationException("second-native-failed");});
        try
        {
            await Task.WhenAll(firstEntered.Task,secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(running.IsCompleted);Assert.Equal(1,Volatile.Read(ref active));Assert.False(released);
            release.Set();
            if(cancelled)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running);
            else Assert.Equal("second-native-failed",(await Assert.ThrowsAsync<InvalidOperationException>(()=>running)).Message);
            Assert.True(released);Assert.Equal(0,Volatile.Read(ref active));
        }
        finally{release.Set();try{await running;}catch(Exception) {}}
    }

    [Fact]
    public async Task Results_keep_manifest_order_and_multiple_native_failures_are_preserved()
    {
        var results=await ScopedAnnSearchWindow.RunAsync(()=>ValueTask.FromResult<IReadOnlyList<AnnMatch>>([new(20,2)]),
            ()=>ValueTask.FromResult<IReadOnlyList<AnnMatch>>([new(10,1)]));
        Assert.Equal(new long[]{20,10},results.SelectMany(x=>x).Select(x=>x.VectorId));
        var failure=await Assert.ThrowsAsync<AggregateException>(()=>ScopedAnnSearchWindow.RunAsync(
            ()=>throw new InvalidOperationException("first-native-failed"),()=>throw new IOException("second-native-failed")));
        Assert.Equal(2,failure.InnerExceptions.Count);
        Assert.Contains(failure.InnerExceptions,e=>e.Message=="first-native-failed");Assert.Contains(failure.InnerExceptions,e=>e.Message=="second-native-failed");
    }
}
