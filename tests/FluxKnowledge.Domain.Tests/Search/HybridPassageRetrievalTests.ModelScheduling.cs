using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Theory]
    [InlineData("generation")]
    [InlineData("dense")]
    [InlineData("lexical")]
    [InlineData("citable")]
    [InlineData("cited")]
    public async Task Two_searches_waiting_for_non_model_work_leave_a_lane_for_indexing(string blockedStage)
    {
        var models = new[] { new Models(), new Models() };
        await using var pool = new ResidentCpuPassageInference(_ => Task.FromResult(new CpuResidentPassageLoad(
            models.Select(model => new CpuPassageInferenceLane(model, model, () => { })).ToArray(),
            new SchedulingOwner())), new EmbeddingProfile("embedding", 2), "ranking", 1);
        await pool.WarmAsync(default);
        var readers = Enumerable.Range(0, 2).Select(_ => new Reader
        {
            Dense = [Passage(8, "query evidence")], BlockStage = blockedStage,
            Leases = new Leases { BlockAcquire = blockedStage == "generation" }
        }).ToArray();
        var tasks = readers.Select(reader => new HybridPassageRetrievalEngine(reader, reader, reader.Leases,
            reader.Leases, pool, new Probe(), new Codec(), new LocalPrivateContentDisclosure())
            .SearchAsync("query", Scope, 1, default).AsTask()).ToArray();
        try
        {
            await Task.WhenAll(readers.Select(reader => blockedStage == "generation"
                ? reader.Leases.AcquireStarted.Task : reader.BlockStarted.Task)).WaitAsync(TimeSpan.FromSeconds(2));
            if (blockedStage == "lexical")
                await Task.WhenAll(readers.Select(reader => reader.DenseCompleted.Task)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, pool.ActiveSearchCount);
            var indexed = await pool.ExecuteAsync((embedding, _, ct) =>
                embedding.CreateEmbeddingAsync("new indexing passage", ct), default).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("embedding", indexed.ModelFingerprint);
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
            Assert.All(readers, reader => Assert.False(reader.Leases.Disposed));
        }
        finally
        {
            foreach (var reader in readers) { reader.ReleaseBlock.TrySetResult(); reader.Leases.ReleaseAcquire.TrySetResult(); }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.All(readers, reader => Assert.Equal(1, reader.Leases.DisposeCalls));
    }

    [Fact]
    public async Task A_callback_arriving_after_admission_ended_cannot_touch_models_while_fallback_is_still_running()
    {
        var leases = new Leases();
        var reader = new Reader { Lexical = [Passage(8, "query evidence")], BlockStage = "fallback-read", Leases = leases };
        var scheduler = new DelayedEntryScheduler();
        var pending = new HybridPassageRetrievalEngine(reader, reader, leases, leases, scheduler,
            new Probe(), new Codec(), new LocalPrivateContentDisclosure()).SearchAsync("query", Scope, 1, default).AsTask();
        try
        {
            await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(leases.Disposed);
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.Deferred!());
            Assert.Equal(0, scheduler.Models.EmbeddingCalls);
            Assert.Equal(1, leases.DisposeCalls);
        }
        finally { reader.ReleaseBlock.TrySetResult(); }
        Assert.Equal("timeout", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).SemanticStatus);
    }

    [Fact]
    public async Task Caller_cancellation_keeps_the_model_callback_corpus_owner_until_actual_settlement()
    {
        var leases = new Leases(); var reader = new Reader();
        var models = new Models { SimulateCallerTimeout = true };
        using var caller = new CancellationTokenSource();
        var pending = Engine(reader, models, leases).SearchAsync("query", Scope, 1, caller.Token).AsTask();
        try
        {
            await models.EmbeddingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(leases.Disposed);
        }
        finally { models.AllowNativeCompletion.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.DetachedWork!);
        await leases.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, leases.DisposeCalls);
    }

    private sealed class DelayedEntryScheduler : IScheduledPassageInference
    {
        public Models Models { get; } = new();
        public EmbeddingProfile EmbeddingProfile => Models.EmbeddingProfile;
        public string RerankerFingerprint => Models.RerankerFingerprint;
        public Func<Task>? Deferred { get; private set; }
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken ct)
        {
            Deferred = async () => { await work(Models, Models, CancellationToken.None); };
            throw new TimeoutException();
        }
    }

    [Fact]
    public async Task Callback_entry_racing_admission_failure_never_uses_released_corpus_ownership()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var leases = new Leases();
            var models = new Models { OnEmbedding = () => Assert.False(leases.Disposed) };
            var scheduler = new RacingEntryScheduler(models);
            var reader = new Reader { Lexical = [Passage(8, "query evidence")], BlockStage = "fallback-read", Leases = leases };
            var pending = new HybridPassageRetrievalEngine(reader, reader, leases, leases, scheduler,
                new Probe(), new Codec(), new LocalPrivateContentDisclosure()).SearchAsync("query", Scope, 1, default).AsTask();
            try
            {
                await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var error = await Record.ExceptionAsync(() => scheduler.Callback!);
                Assert.True(error is null or OperationCanceledException, error?.ToString());
                Assert.True(leases.Disposed);
                Assert.Equal(1, leases.DisposeCalls);
                Assert.InRange(models.EmbeddingCalls, 0, 1);
            }
            finally { reader.ReleaseBlock.TrySetResult(); }
            Assert.Equal("timeout", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).SemanticStatus);
        }
    }

    private sealed class RacingEntryScheduler(Models models) : IScheduledPassageInference
    {
        public EmbeddingProfile EmbeddingProfile => models.EmbeddingProfile;
        public string RerankerFingerprint => models.RerankerFingerprint;
        public Task? Callback { get; private set; }
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken ct)
        {
            Callback = Task.Run(async () => await work(models, models, CancellationToken.None));
            throw new TimeoutException();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_release_cannot_be_downgraded_to_successful_fusion_even_after_early_cancellation(bool earlyCancellation)
    {
        var reader = new Reader { Lexical = [Passage(8, "query lexical evidence")], Dense = [Passage(9, "semantic evidence")] };
        var leases = new Leases(); reader.Leases = leases;
        var models = new Models
        {
            PauseRanking = earlyCancellation, IgnoreRankingCancellation = true, ReturnLateRankingSuccess = true,
            RankingFailure = new PassageInferenceOwnershipException(new InvalidOperationException("synthetic-late-native-cleanup-failure"))
        };
        var scheduler = new ReleaseFailureScheduler(models, earlyCancellation);
        var clock = new ManualClock();
        var pending = new HybridPassageRetrievalEngine(reader, reader, leases, leases, scheduler,
            new Probe(), new Codec(), new LocalPrivateContentDisclosure(), clock).SearchAsync("query", Scope, 2, default).AsTask();
        try
        {
            if (earlyCancellation)
            {
                await models.RankingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                clock.Advance(TimeSpan.FromSeconds(3));
                Assert.False(pending.IsCompleted);
                Assert.False(leases.Disposed);
                models.ReleaseRanking.TrySetResult();
            }
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("unavailable", response.SemanticStatus);
            Assert.Equal("lexical", response.RetrievalMode);
            Assert.Null(response.IndexGeneration);
            Assert.Equal(8, Assert.Single(response.Results).ChunkId);
            Assert.Equal(1, leases.DisposeCalls);
        }
        finally { models.ReleaseRanking.TrySetResult(); }
    }

    private sealed class ReleaseFailureScheduler(Models models, bool earlyCancellation) : IScheduledPassageInference
    {
        private int _calls;
        public EmbeddingProfile EmbeddingProfile => models.EmbeddingProfile;
        public string RerankerFingerprint => models.RerankerFingerprint;
        public Task? Callback { get; private set; }
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) return work(models, models, ct);
            if (!earlyCancellation) throw new InvalidOperationException("interactive-execution-or-release-unconfirmed");
            Callback = work(models, models, ct).AsTask();
            _ = Callback.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class SchedulingOwner : IDisposable { public void Dispose() { } }
}
