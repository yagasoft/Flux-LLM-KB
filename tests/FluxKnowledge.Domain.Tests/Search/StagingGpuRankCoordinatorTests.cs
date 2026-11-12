using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class ExpandedGpuRankCoordinatorTests
{
    private const string Profile = "verified-test-ranker";
    private static RerankPassage[] Pairs(int count = 100) => Enumerable.Range(1,count).Select(i => new RerankPassage(i,"passage "+i)).ToArray();

    [Fact]
    public async Task Two_whole_requests_share_one_load_and_wait_for_native_and_durable_settlement()
    {
        var backend = new Backend();
        await using var coordinator = new ExpandedGpuRankCoordinator(backend, readinessTimeout: TimeSpan.FromSeconds(1));
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow,"first","span",default);
        await using var second = coordinator.Reserve(DateTimeOffset.UtcNow,"second","span",default);
        var a = first.SubmitAsync("one",Pairs()).AsTask();
        var b = second.SubmitAsync("two",Pairs()).AsTask();
        backend.Load.TrySetResult();
        await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(a.IsCompleted); Assert.False(b.IsCompleted);
        Assert.Equal(1,backend.Opens); Assert.Equal(new[]{50,50,50,50},backend.Calls);
        backend.Durable.TrySetResult();
        Assert.Equal(100,(await a).Scores.Count); Assert.Equal(100,(await b).Scores.Count);
        Assert.Equal(1,backend.Closes);
    }

    [Fact]
    public async Task One_caller_scores_without_a_gather_delay_and_third_or_refill_callers_are_refused()
    {
        var backend = new Backend();
        await using var coordinator = new ExpandedGpuRankCoordinator(backend, readinessTimeout: TimeSpan.FromSeconds(1));
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",default);
        var result = first.SubmitAsync("one",Pairs()).AsTask();
        backend.Load.TrySetResult();
        await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new[]{50,50},backend.Calls);
        Assert.Throws<PassageRetrievalRefusalException>(() => coordinator.Reserve(DateTimeOffset.UtcNow,"late","span",default));
        backend.Durable.TrySetResult(); await result;
    }

    [Fact]
    public async Task One_cancelled_client_cannot_cancel_the_other_or_publish_late_native_scores()
    {
        var backend = new Backend { BlockFirst = true };
        await using var coordinator = new ExpandedGpuRankCoordinator(backend, readinessTimeout: TimeSpan.FromSeconds(1));
        using var cancel = new CancellationTokenSource();
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",cancel.Token);
        await using var second = coordinator.Reserve(DateTimeOffset.UtcNow,"two","span",default);
        var a = first.SubmitAsync("one",Pairs()).AsTask(); var b = second.SubmitAsync("two",Pairs()).AsTask();
        backend.Load.TrySetResult(); await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancel.Cancel(); Assert.False(a.IsCompleted); Assert.False(backend.OwnerCancelled);
        backend.FirstRelease.TrySetResult(); await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1));
        backend.Durable.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Equal(100,(await b).Scores.Count);
        Assert.Equal(new[]{50,50,50},backend.Calls);
    }

    [Fact]
    public async Task An_uncertain_owner_blocks_new_cohorts_even_after_the_result_task_returns()
    {
        var backend = new Backend { Uncertain = true };
        var coordinator = new ExpandedGpuRankCoordinator(backend, readinessTimeout: TimeSpan.FromSeconds(1));
        var first = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",default);
        var result = first.SubmitAsync("one",Pairs()).AsTask();
        backend.Load.TrySetResult(); await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1)); backend.Durable.TrySetResult();
        await Assert.ThrowsAsync<PassageInferenceOwnershipException>(() => result);
        Assert.Throws<PassageRetrievalRefusalException>(() => coordinator.Reserve(DateTimeOffset.UtcNow,"new","span",default));
        Assert.Equal(1,backend.Opens);
        await Assert.ThrowsAsync<PassageInferenceOwnershipException>(() => first.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<PassageInferenceOwnershipException>(() => coordinator.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Payloads_are_frozen_once_and_invalid_or_duplicate_submissions_never_enter_models()
    {
        var backend = new Backend();
        await using var coordinator = new ExpandedGpuRankCoordinator(backend, readinessTimeout: TimeSpan.FromSeconds(1));
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",default);
        var pairs = Pairs(); var result = first.SubmitAsync("one",pairs).AsTask(); pairs[0] = new(999,"changed after submit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.SubmitAsync("again",Pairs()).AsTask());
        backend.Load.TrySetResult(); await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1)); backend.Durable.TrySetResult();
        Assert.Equal(1,(await result).Scores[0].PassageId);
    }

    [Fact]
    public async Task Actual_frozen_payload_and_each_text_are_bounded_before_model_entry()
    {
        var backend = new Backend();
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        await using var ticket = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",default);
        await Assert.ThrowsAsync<ArgumentException>(() => ticket.SubmitAsync("one",new MiscountedPairs()).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => ticket.SubmitAsync("one",[new(1,new string('x',16385))]).AsTask());
        backend.Load.TrySetResult(); backend.Durable.TrySetResult();
    }

    [Fact]
    public async Task Model_identity_is_pinned_and_duplicate_backend_callback_cannot_score_twice()
    {
        var backend = new Backend { Duplicate = true };
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        await using var ticket = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",default);
        var result = ticket.SubmitAsync("one",Pairs()).AsTask();
        backend.Load.TrySetResult(); await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1)); backend.Durable.TrySetResult();
        await result; Assert.Equal(new[]{50,50},backend.Calls); Assert.True(backend.DuplicateRejected);
        backend.ProfileValue = "changed";
        Assert.Equal(Profile,coordinator.RerankerFingerprint);
    }

    [Fact]
    public async Task Repeated_concurrent_shutdown_joins_the_same_task()
    {
        var coordinator = new ExpandedGpuRankCoordinator(new Backend());
        await Task.WhenAll(coordinator.DisposeAsync().AsTask(),coordinator.DisposeAsync().AsTask());
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task A_second_client_can_join_entered_native_work_but_a_third_cannot_refill_it()
    {
        var backend = new Backend { BlockFirst = true };
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",default);
        var a = first.SubmitAsync("one",Pairs()).AsTask(); backend.Load.TrySetResult();
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await using var second = coordinator.Reserve(DateTimeOffset.UtcNow,"two","span",default);
        var b = second.SubmitAsync("two",Pairs()).AsTask();
        Assert.Throws<PassageRetrievalRefusalException>(() => coordinator.Reserve(DateTimeOffset.UtcNow,"third","span",default));
        backend.FirstRelease.TrySetResult(); await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1)); backend.Durable.TrySetResult();
        await Task.WhenAll(a,b); Assert.Equal(new[]{50,50,50,50},backend.Calls);
    }

    [Fact]
    public async Task Ready_second_client_scores_before_unready_first_and_readiness_expires_from_original_arrival()
    {
        var backend = new Backend();
        await using var coordinator = new ExpandedGpuRankCoordinator(backend,readinessTimeout:TimeSpan.FromMilliseconds(250));
        await using var first = coordinator.Reserve(DateTimeOffset.UtcNow,"unready","span",default);
        await using var second = coordinator.Reserve(DateTimeOffset.UtcNow,"ready","span",default);
        var b = second.SubmitAsync("two",Pairs()).AsTask(); backend.Load.TrySetResult();
        await backend.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(2)); backend.Durable.TrySetResult();
        Assert.Equal(100,(await b).Scores.Count);
        var failure = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => first.SubmitAsync("late",Pairs()).AsTask());
        Assert.Equal("timeout",failure.Status); Assert.Equal(new[]{50,50},backend.Calls);
    }

    [Fact]
    public async Task All_client_cancellation_skips_scoring_and_disposes_normally_without_cancelling_shared_owner()
    {
        var backend = new Backend();
        await using var coordinator = new ExpandedGpuRankCoordinator(backend);
        using var cancel = new CancellationTokenSource();
        await using var ticket = coordinator.Reserve(DateTimeOffset.UtcNow,"one","span",cancel.Token);
        var result = ticket.SubmitAsync("one",Pairs()).AsTask(); cancel.Cancel(); backend.Load.TrySetResult();
        await backend.NativeDone.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(backend.OwnerCancelled); Assert.False(result.IsCompleted); Assert.Empty(backend.Calls);
        backend.Durable.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
    }

    private sealed class MiscountedPairs : IReadOnlyList<RerankPassage>
    {
        public int Count => 1;
        public RerankPassage this[int index] => new(index+1,"passage");
        public IEnumerator<RerankPassage> GetEnumerator() => ((IEnumerable<RerankPassage>)Pairs(101)).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Backend : IExpandedGpuRankBackend
    {
        public string RerankerFingerprint => ProfileValue;
        internal string ProfileValue = Profile;
        internal readonly TaskCompletionSource Load = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource NativeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Durable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<int> Calls = [];
        internal bool BlockFirst, Uncertain, OwnerCancelled, Duplicate, DuplicateRejected;
        internal int Opens, Closes;
        public async Task<ExpandedGpuRankOutcome> RunAsync(Guid cohortId, Func<IPassageReranker,CancellationToken,ValueTask<IReadOnlyDictionary<Guid,RerankResult>>> score, CancellationToken ct)
        {
            Opens++;
            using var cancel = ct.Register(() =>
            {
                OwnerCancelled = true;
                // A failing assertion must still let the test's owned coordinator drain.
                Load.TrySetResult(); FirstRelease.TrySetResult(); Durable.TrySetResult();
            });
            await Load.Task;
            var results = await score(new Ranker(this),ct);
            if (Duplicate)
            {
                try { await score(new Ranker(this),ct); }
                catch (InvalidOperationException) { DuplicateRejected = true; }
            }
            Closes++; NativeDone.TrySetResult(); await Durable.Task;
            return new(results,Uncertain ? ExpandedGpuRankSettlement.RetainedUncertain : ExpandedGpuRankSettlement.Settled,null);
        }
        private sealed class Ranker(Backend backend) : IPassageReranker
        {
            public async ValueTask<RerankResult> RerankAsync(string query,IReadOnlyList<RerankPassage> pairs,CancellationToken ct)
            {
                backend.Calls.Add(pairs.Count); backend.FirstEntered.TrySetResult();
                if(backend.BlockFirst && backend.Calls.Count == 1) await backend.FirstRelease.Task;
                ct.ThrowIfCancellationRequested();
                return new(pairs.Select(p => new RerankScore(p.PassageId,.5f)).ToArray(),Profile);
            }
        }
    }
}
