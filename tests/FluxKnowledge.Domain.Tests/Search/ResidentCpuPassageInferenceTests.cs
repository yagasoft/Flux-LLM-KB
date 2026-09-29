using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class ResidentCpuPassageInferenceTests
{
    [Fact]
    public async Task Two_lanes_run_concurrently_and_a_blocked_native_callback_keeps_its_lane_until_settled()
    {
        var disposed = 0;
        await using var pool = Pool(2, () => Interlocked.Increment(ref disposed));
        await pool.WarmAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<int> Work(IEmbeddingProvider _, IPassageReranker __, CancellationToken ___)
        {
            if (Interlocked.Increment(ref bothEntered) == 2) entered.TrySetResult();
            await release.Task;
            return 1;
        }
        using var cancelled = new CancellationTokenSource();
        var first = pool.ExecuteAsync(Work, cancelled.Token).AsTask();
        var second = pool.ExecuteAsync(Work, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() =>
            pool.ExecuteAsync((_, _, _) => ValueTask.FromResult(2), CancellationToken.None).AsTask());
        Assert.Equal(0, disposed);
        var stopping = pool.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted);
        release.TrySetResult();
        Assert.Equal(1, await first);
        Assert.Equal(1, await second);
        await stopping;
        Assert.Equal(2, disposed);
    }

    [Fact]
    public async Task Reranking_shards_run_in_parallel_and_return_original_passage_order()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var sessions = Enumerable.Range(0, 2).Select(_ => (IPassageReranker)new Reranker(async passages =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
            await release.Task;
            return passages.Select(p => new RerankScore(p.PassageId, p.PassageId));
        })).ToArray();
        var reranker = new ParallelCpuPassageReranker(sessions, "test-reranker");
        var passages = new[] { new RerankPassage(3, "three"), new(1, "one"), new(4, "four"), new(2, "two") };
        var result = reranker.RerankAsync("query", passages, CancellationToken.None).AsTask();
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Equal([3L, 1L, 4L, 2L], (await result).Scores.Select(score => score.PassageId));
    }

    [Fact]
    public async Task Failed_shard_does_not_release_a_sibling_native_session_early()
    {
        var siblingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IPassageReranker[] sessions =
        [
            new Reranker(_ => Task.FromException<IEnumerable<RerankScore>>(new InvalidOperationException("native-failure"))),
            new Reranker(async passages =>
            {
                siblingEntered.TrySetResult();
                await releaseSibling.Task;
                return passages.Select(p => new RerankScore(p.PassageId, 1));
            })
        ];
        var work = new ParallelCpuPassageReranker(sessions, "test-reranker").RerankAsync("query",
            [new(1, "first"), new(2, "second")], CancellationToken.None).AsTask();
        await siblingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(work.IsCompleted);
        releaseSibling.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => work);
    }

    [Fact]
    public async Task Shutdown_during_warmup_disposes_late_models_and_owner_before_completing()
    {
        var loading = new TaskCompletionSource<CpuResidentPassageLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedModels = 0;
        var disposedOwners = 0;
        var pool = new ResidentCpuPassageInference(_ => loading.Task,
            new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        var warming = pool.WarmAsync(CancellationToken.None);
        var shutdown = pool.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        loading.TrySetResult(new CpuResidentPassageLoad(
            [new CpuPassageInferenceLane(new Embedding(), new Reranker(_ =>
                Task.FromResult<IEnumerable<RerankScore>>([])), () => Interlocked.Increment(ref disposedModels))],
            new CountingOwner(() => Interlocked.Increment(ref disposedOwners))));
        await warming;
        await shutdown;
        Assert.Equal(1, disposedModels);
        Assert.Equal(1, disposedOwners);
    }

    [Fact]
    public async Task New_worker_waits_for_prior_native_callback_and_final_pool_disposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxCpuPool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, CpuSearchOwnerLease.FileName), []);
        ResidentCpuPassageInference CreatePool() => new(async ct => new CpuResidentPassageLoad(
            [new CpuPassageInferenceLane(new Embedding(), new Reranker(_ =>
                Task.FromResult<IEnumerable<RerankScore>>([])), () => { })],
            await CpuSearchOwnerLease.AcquireAsync(directory, ct)),
            new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        var first = CreatePool();
        var next = CreatePool();
        try
        {
            await first.WarmAsync(CancellationToken.None);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = first.ExecuteAsync(async (_, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return 1;
            }, CancellationToken.None).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var nextWarm = next.WarmAsync(CancellationToken.None);
            var firstStop = first.DisposeAsync().AsTask();
            await Task.Delay(150);
            Assert.False(firstStop.IsCompleted);
            Assert.False(nextWarm.IsCompleted);
            release.TrySetResult();
            Assert.Equal(1, await active);
            await firstStop;
            await nextWarm.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await first.DisposeAsync();
            await next.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Failed_native_disposal_attempts_every_lane_and_keeps_owner()
    {
        var laterDisposed = 0;
        var ownerDisposed = 0;
        var load = new CpuResidentPassageLoad(
        [
            new CpuPassageInferenceLane(new Embedding(), new Reranker(_ =>
                Task.FromResult<IEnumerable<RerankScore>>([])), () => throw new IOException("native-close-failed")),
            new CpuPassageInferenceLane(new Embedding(), new Reranker(_ =>
                Task.FromResult<IEnumerable<RerankScore>>([])), () => Interlocked.Increment(ref laterDisposed))
        ], new CountingOwner(() => Interlocked.Increment(ref ownerDisposed)));
        Assert.Throws<AggregateException>(() => load.Dispose());
        Assert.Equal(1, laterDisposed);
        Assert.Equal(0, ownerDisposed);
    }

    private static ResidentCpuPassageInference Pool(int count, Action dispose) => new(_ =>
        Task.FromResult(new CpuResidentPassageLoad(Enumerable.Range(0, count)
            .Select(_ => new CpuPassageInferenceLane(new Embedding(), new Reranker(_ =>
                Task.FromResult<IEnumerable<RerankScore>>([])), dispose)).ToArray(), new NoopOwner())),
        new EmbeddingProfile("test-embedding", 1024), "test-reranker");

    private sealed class NoopOwner : IDisposable { public void Dispose() { } }
    private sealed class CountingOwner(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    private sealed class Embedding : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => throw new NotImplementedException();
    }
    private sealed class Reranker(Func<IReadOnlyList<RerankPassage>, Task<IEnumerable<RerankScore>>> score) : IPassageReranker
    {
        public async ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
            => new((await score(passages)).ToArray(), "test-reranker");
    }
}
