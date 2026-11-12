using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class ResidentCpuPassageInferenceTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Unsupported_pending_capacity_is_rejected_before_loading_models(int maximum)
    {
        var loads=0;
        Assert.Throws<ArgumentOutOfRangeException>(()=>new ResidentCpuPassageInference(_=>
        { loads++; throw new InvalidOperationException(); },new("test",1024),"test",maximum));
        Assert.Equal(0,loads);
    }

    [Fact]
    public async Task Bounded_pending_admission_runs_after_actual_settlement_and_a_fourth_caller_is_busy()
    {
        await using var pool = QueuedPool(); await pool.WarmAsync(default);
        var held = new HeldCalls();
        var first=pool.ExecuteAsync(held.Work,default).AsTask(); var second=pool.ExecuteAsync(held.Work,default).AsTask();
        await held.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invoked=0;
        var pending=pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(Interlocked.Increment(ref invoked)),default).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            var busy=await Assert.ThrowsAsync<PassageRetrievalRefusalException>(()=>pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(0),default).AsTask());
            Assert.Equal("busy",busy.Status); Assert.Equal(2,pool.ActiveSearchCount); Assert.Equal(0,invoked);
        }
        finally { held.Release.TrySetResult(); await Task.WhenAll(first,second); }
        Assert.Equal(1,await pending); Assert.Equal(1,invoked); Assert.Equal(0,pool.ActiveSearchCount);
    }

    [Fact]
    public async Task Pending_cancellation_releases_only_its_waiting_slot_and_never_invokes_models()
    {
        await using var pool=QueuedPool(); await pool.WarmAsync(default);
        var held=new HeldCalls(); var first=pool.ExecuteAsync(held.Work,default).AsTask(); var second=pool.ExecuteAsync(held.Work,default).AsTask();
        await held.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invoked=0; using var caller=new CancellationTokenSource();
        var pending=pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(Interlocked.Increment(ref invoked)),caller.Token).AsTask();
        try
        {
            caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.Equal(0,invoked); Assert.Equal(2,pool.ActiveSearchCount);
            pending=pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(Interlocked.Increment(ref invoked)),default).AsTask();
            Assert.False(pending.IsCompleted);
        }
        finally { held.Release.TrySetResult(); await Task.WhenAll(first,second); }
        Assert.Equal(1,await pending); Assert.Equal(0,pool.ActiveSearchCount);
    }

    [Fact]
    public async Task Shutdown_rejects_pending_admission_but_retains_models_until_active_native_acknowledgement()
    {
        var disposed=0; var pool=QueuedPool(()=>Interlocked.Increment(ref disposed)); await pool.WarmAsync(default);
        var held=new HeldCalls(); var first=pool.ExecuteAsync(held.Work,default).AsTask(); var second=pool.ExecuteAsync(held.Work,default).AsTask();
        await held.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invoked=0; var pending=pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(Interlocked.Increment(ref invoked)),default).AsTask();
        var stopping=pool.DisposeAsync().AsTask();
        try
        {
            var refusal=await Assert.ThrowsAsync<PassageRetrievalRefusalException>(()=>pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("unavailable",refusal.Status);
            Assert.False(stopping.IsCompleted); Assert.Equal(0,disposed); Assert.Equal(0,invoked);
        }
        finally { held.Release.TrySetResult(); await Task.WhenAll(first,second); await stopping; }
        Assert.Equal(2,disposed); Assert.Equal(0,pool.ActiveSearchCount);
    }

    [Fact]
    public async Task Grant_cancellation_races_preserve_capacity_and_never_invoke_a_request_twice()
    {
        await using var pool=QueuedPool(); await pool.WarmAsync(default);
        for(var iteration=0;iteration<32;iteration++)
        {
            var held=new HeldCalls(); var first=pool.ExecuteAsync(held.Work,default).AsTask(); var second=pool.ExecuteAsync(held.Work,default).AsTask();
            await held.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var caller=new CancellationTokenSource(); var invoked=0;
            var pending=pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(Interlocked.Increment(ref invoked)),caller.Token).AsTask();
            await Task.WhenAll(Task.Run(caller.Cancel),Task.Run(()=>held.Release.TrySetResult()));
            await Task.WhenAll(first,second);
            try { await pending; } catch(OperationCanceledException) { }
            Assert.InRange(invoked,0,1); Assert.Equal(0,pool.ActiveSearchCount);
            Assert.Equal(7,await pool.ExecuteAsync((_,_,_)=>ValueTask.FromResult(7),default));
        }
    }

    [Fact]
    public async Task Shutdown_racing_with_handoff_retains_any_granted_callback_until_its_actual_acknowledgement()
    {
        for(var iteration=0;iteration<32;iteration++)
        {
            var disposed=0; var pool=QueuedPool(()=>Interlocked.Increment(ref disposed)); await pool.WarmAsync(default);
            var held=new HeldCalls(); var first=pool.ExecuteAsync(held.Work,default).AsTask(); var second=pool.ExecuteAsync(held.Work,default).AsTask();
            await held.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending=pool.ExecuteAsync(async (_,_,_)=> { entered.TrySetResult(); await release.Task; return 3; },default).AsTask();
            var stopping=Task.Run(async ()=>await pool.DisposeAsync());
            held.Release.TrySetResult();
            await Task.WhenAll(first,second);
            try
            {
                await Task.WhenAny(entered.Task,pending).WaitAsync(TimeSpan.FromSeconds(5));
                if(entered.Task.IsCompleted) { Assert.False(stopping.IsCompleted); Assert.Equal(0,disposed); }
            }
            finally
            {
                release.TrySetResult();
                try { await pending; } catch(OperationCanceledException) { } catch(PassageRetrievalRefusalException) { }
                await stopping;
            }
            Assert.Equal(2,disposed); Assert.Equal(0,pool.ActiveSearchCount);
        }
    }

    private static ResidentCpuPassageInference QueuedPool(Action? dispose=null) => new(_=>Task.FromResult(
        new CpuResidentPassageLoad(Enumerable.Range(0,2).Select(_=>new CpuPassageInferenceLane(new Embedding(),
            new Reranker(_=>Task.FromResult<IEnumerable<RerankScore>>([])),dispose??(()=>{}))).ToArray(),new NoopOwner())),
        new("test-embedding",1024),"test-reranker",maximumPendingRequests:1);

    private sealed class HeldCalls
    {
        private int _entered;
        internal TaskCompletionSource BothStarted { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async ValueTask<int> Work(IEmbeddingProvider _,IPassageReranker __,CancellationToken ___)
        { if(Interlocked.Increment(ref _entered)==2) BothStarted.TrySetResult(); await Release.Task; return 1; }
    }
}
