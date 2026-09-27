using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class BgeGpuInferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reranker_refusal_allows_fused_result_only_after_confirmed_native_cleanup(bool cleanupFails)
    {
        var factory = new RecordingModels { Failure = cleanupFails ? "ranking-dispose" : "ranking-run" };
        var result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync(Owner(), async (embedding, reranker, ct) =>
        {
            await embedding.CreateEmbeddingAsync("query", ct);
            try { await reranker.RerankAsync("query", [new(8, "passage")], ct); }
            catch (InvalidOperationException) { }
            return "fused-result";
        });
        Assert.Equal(!cleanupFails, result.NativeCapacityReleased);
        if (cleanupFails) { Assert.Null(result.Value); Assert.NotNull(result.RefusalReason); }
        else { Assert.Equal("fused-result", result.Value); Assert.Null(result.RefusalReason); }
        Assert.Equal(["embedding.open", "embedding.run", "embedding.dispose", "ranking.open", "ranking.run", "ranking.dispose"], factory.Events);
    }

    [Fact]
    public async Task Request_embeds_then_unloads_before_loading_reranker_and_preserves_results()
    {
        using var trace = new FluxKnowledge.Integration.Tests.Support.HybridSearchTraceListener();
        var factory = new RecordingModels();
        var owner = Owner();
        IEmbeddingProvider? escaped = null;
        var result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync(owner, async (embedding, reranker, ct) =>
        {
            escaped = embedding;
            var vector = await embedding.CreateEmbeddingAsync("query", ct);
            Assert.Equal(BgeOfflineModels.EmbeddingFingerprint, vector.ModelFingerprint);
            Assert.True(owner.NativeCapacityReleased);
            var scores = await reranker.RerankAsync("query", [new(8, "first"), new(2, "second")], ct);
            return scores;
        });
        Assert.Null(result.RefusalReason);
        Assert.True(result.NativeCapacityReleased);
        Assert.Equal([8L, 2L], result.Value!.Scores.Select(score => score.PassageId));
        Assert.Equal(["embedding.open", "embedding.run", "embedding.dispose", "ranking.open", "ranking.run", "ranking.dispose"], factory.Events);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => escaped!.CreateEmbeddingAsync("late query", CancellationToken.None).AsTask());
        var phases = trace.Events.Where(entry => entry.Id == 3 && Equals(entry["batchId"], owner.Handle.BatchId.ToString("N"))).ToArray();
        Assert.Equal(["embedding:load", "embedding:inference", "embedding:unload", "reranker:load", "reranker:inference", "reranker:unload"],
            phases.Select(entry => entry["model"] + ":" + entry["phase"]));
        Assert.All(phases, entry => { Assert.Equal("success", entry["outcome"]); Assert.True((double)entry["elapsedMs"]! >= 0); });
        var serialized = System.Text.Json.JsonSerializer.Serialize(phases);
        Assert.DoesNotContain("query", serialized);
        Assert.DoesNotContain("first", serialized);
        Assert.DoesNotContain("second", serialized);
    }

    [Fact]
    public async Task Cache_miss_before_native_creation_refuses_and_confirms_no_gpu_allocation()
    {
        using var trace = new FluxKnowledge.Integration.Tests.Support.HybridSearchTraceListener();
        var factory = new RecordingModels { Failure = "cache-miss" };
        var owner = Owner();
        var result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync<int>(owner, async (embedding, _, ct) =>
        {
            await embedding.CreateEmbeddingAsync("query", ct);
            return 42;
        });
        Assert.Equal(ModelStoreReasons.ArtifactMissing, result.RefusalReason);
        Assert.True(result.NativeCapacityReleased);
        Assert.DoesNotContain("embedding.run", factory.Events);
        var phase = Assert.Single(trace.Events, entry => entry.Id == 3 && Equals(entry["batchId"], owner.Handle.BatchId.ToString("N")));
        Assert.Equal("load", phase["phase"]);
        Assert.Equal("failed", phase["outcome"]);
    }

    [Theory]
    [InlineData("run", true)]
    [InlineData("dispose", false)]
    [InlineData("creation", false)]
    public async Task Failure_reports_release_only_when_native_cleanup_is_confirmed(string fault, bool released)
    {
        using var trace = new FluxKnowledge.Integration.Tests.Support.HybridSearchTraceListener();
        var factory = new RecordingModels { Failure = fault };
        var result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync<int>(Owner(), async (embedding, _, ct) =>
        {
            await embedding.CreateEmbeddingAsync("query", ct);
            return 42;
        });
        Assert.NotNull(result.RefusalReason);
        Assert.Equal(released, result.NativeCapacityReleased);
        Assert.DoesNotContain("ranking.open", factory.Events);
    }

    [Fact]
    public async Task Owner_deadline_is_used_even_when_callback_supplies_an_uncancelled_token()
    {
        using var deadline = new CancellationTokenSource();
        var factory = new RecordingModels { BeforeRun = () => deadline.Cancel() };
        var result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync<int>(Owner(deadline.Token), async (embedding, _, _) =>
        {
            await embedding.CreateEmbeddingAsync("query", CancellationToken.None);
            return 42;
        });
        Assert.Equal("bge-request-cancelled", result.RefusalReason);
        Assert.True(result.NativeCapacityReleased);
    }

    [Fact]
    public async Task Phase_order_and_repeated_query_embedding_fail_before_another_model_load()
    {
        var factory = new RecordingModels();
        var result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync<int>(Owner(), async (_, reranker, ct) =>
        {
            await reranker.RerankAsync("query", [new(1, "passage")], ct);
            return 1;
        });
        Assert.Equal("bge-request-phase-invalid", result.RefusalReason);
        Assert.Empty(factory.Events);
        result = await new BgeGpuInferenceSession(factory).ExecuteSearchAsync<int>(Owner(), async (embedding, _, ct) =>
        {
            await embedding.CreateEmbeddingAsync("first", ct);
            await embedding.CreateEmbeddingAsync("second", ct);
            return 1;
        });
        Assert.Equal("bge-request-phase-invalid", result.RefusalReason);
        Assert.Single(factory.Events, value => value == "embedding.open");
    }

    [Fact]
    public async Task Background_batch_unloads_after_four_outputs_and_refuses_oversize_before_loading()
    {
        var factory = new RecordingModels();
        var session = new BgeGpuInferenceSession(factory);
        var result = await session.EmbedBatchAsync(Owner(), ["one", "two", "three", "four"], CancellationToken.None);
        Assert.Null(result.RefusalReason);
        Assert.Equal(4, result.Value!.Count);
        Assert.True(result.NativeCapacityReleased);
        factory.Events.Clear();
        var invalid = await session.EmbedBatchAsync(Owner(), ["one", "two", "three", "four", "five"], CancellationToken.None);
        Assert.Equal("bge-batch-invalid", invalid.RefusalReason);
        Assert.Empty(factory.Events);
    }

    private static GpuOwnedWorkContext Owner(CancellationToken ct = default) => new(new(Guid.NewGuid(), "gpu-0", "executor", 1, Guid.NewGuid()),
        BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, ct);

    [Fact]
    public void Closing_ownership_atomically_blocks_late_allocation_and_retains_unconfirmed_resources()
    {
        var owner = Owner();
        var allocation = owner.BeginNativeAllocation();
        Assert.False(owner.InvalidateAndReadNativeRelease());
        Assert.Throws<ObjectDisposedException>(() => owner.BeginNativeAllocation());
        allocation.ConfirmReleased();
        allocation.ConfirmReleased();
        Assert.True(owner.NativeCapacityReleased);
        Assert.True(owner.InvalidateAndReadNativeRelease());
        Assert.True(Owner().InvalidateAndReadNativeRelease());
    }

    internal sealed class RecordingModels : IBgeGpuModelFactory
    {
        public List<string> Events { get; } = [];
        public string? Failure { get; init; }
        public Action? BeforeRun { get; init; }

        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext owner)
        {
            Assert.True(owner.NativeCapacityReleased);
            Events.Add("embedding.open");
            if (Failure == "cache-miss") throw new BgeInferenceException(ModelStoreReasons.ArtifactMissing);
            var allocation = owner.BeginNativeAllocation();
            if (Failure == "creation") throw new InvalidOperationException("synthetic-constructor-failure");
            return ValueTask.FromResult(new BgeGpuModelLease<IBatchedEmbeddingProvider>(new Embedding(this), () =>
            {
                Events.Add("embedding.dispose");
                if (Failure == "dispose") throw new InvalidOperationException("synthetic-disposal-failure");
                allocation.ConfirmReleased();
            }));
        }

        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext owner)
        {
            Assert.True(owner.NativeCapacityReleased);
            Events.Add("ranking.open");
            var allocation = owner.BeginNativeAllocation();
            return ValueTask.FromResult(new BgeGpuModelLease<IPassageReranker>(new Ranker(this), () =>
            {
                Events.Add("ranking.dispose");
                if (Failure == "ranking-dispose") throw new InvalidOperationException("synthetic-ranking-disposal-failure");
                allocation.ConfirmReleased();
            }));
        }

        private sealed class Embedding(RecordingModels owner) : IBatchedEmbeddingProvider
        {
            public EmbeddingProfile Profile => new(BgeOfflineModels.EmbeddingFingerprint, 1024);
            public int MaximumBatchSize => 4;
            public async ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => (await CreateEmbeddingsAsync([text], ct))[0];
            public ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
            {
                owner.Events.Add("embedding.run"); owner.BeforeRun?.Invoke(); ct.ThrowIfCancellationRequested();
                if (owner.Failure == "run") throw new InvalidOperationException("synthetic-run-failure");
                IReadOnlyList<EmbeddingResult> results = texts.Select(_ =>
                {
                    var values = new float[1024]; values[0] = 1; return new EmbeddingResult(values, Profile.ModelFingerprint);
                }).ToArray();
                return ValueTask.FromResult(results);
            }
        }

        private sealed class Ranker(RecordingModels owner) : IPassageReranker
        {
            public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
            {
                owner.Events.Add("ranking.run"); ct.ThrowIfCancellationRequested();
                if (owner.Failure == "ranking-run") throw new InvalidOperationException("synthetic-ranking-run-failure");
                return ValueTask.FromResult(new RerankResult(passages.Select(p => new RerankScore(p.PassageId, p.PassageId)).ToArray(), BgeOfflineModels.RerankerFingerprint));
            }
        }
    }
}
