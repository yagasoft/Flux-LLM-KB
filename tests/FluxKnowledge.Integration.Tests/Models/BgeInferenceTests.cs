using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class BgeInferenceTests
{
    [Fact]
    public void Disposed_passage_tokenizer_cannot_open_model_files_on_a_late_count()
    {
        var store = new RefusingStore("unexpected-model-access");
        using var tokenizer = new BgePassageTokenizer(new(store, store, store, store));
        tokenizer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => tokenizer.CountTokens("late construction"));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Embedding_batch_preserves_order_and_normalises_each_passage_in_one_run()
    {
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((batch, _) =>
        {
            Assert.Equal(4, batch.Count);
            var values = new float[batch.Count * 1024];
            for (var row = 0; row < batch.Count; row++) values[row * 1024 + row] = row + 1;
            return new(values, [batch.Count, 1024]);
        });
        using var provider = new BgeEmbeddingProvider(tokenizer, runner, "profile");
        IBatchedEmbeddingProvider batched = provider;
        Assert.Equal(new EmbeddingProfile("profile", 1024), batched.Profile);
        Assert.Equal(4, batched.MaximumBatchSize);
        var results = await batched.CreateEmbeddingsAsync(["first", "second", "third", "fourth"], CancellationToken.None);
        Assert.Equal(4, results.Count);
        for (var row = 0; row < results.Count; row++)
        {
            Assert.Equal("profile", results[row].ModelFingerprint);
            Assert.Equal(1024, results[row].Values.Count);
            Assert.Equal(1f, results[row].Values[row]);
            Assert.Equal(1f, results[row].Values.Sum());
        }
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Embedding_batch_validates_every_input_before_native_execution()
    {
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((_, _) => throw new InvalidOperationException("unexpected-native-run"));
        using var provider = new BgeEmbeddingProvider(tokenizer, runner, "profile");
        foreach (string[] texts in new string[][] { [], ["a", "b", "c", "d", "e"], ["valid", " "], ["valid", "oversize"] })
            await Assert.ThrowsAsync<BgeInferenceException>(() => provider.CreateEmbeddingsAsync(texts, CancellationToken.None).AsTask());
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("zero")]
    [InlineData("nan")]
    public async Task Embedding_batch_refuses_invalid_second_result_without_returning_partial_output(string fault)
    {
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((_, _) =>
        {
            var values = new float[2048];
            values[0] = 1;
            values[1024] = fault == "nan" ? float.NaN : fault == "zero" ? 0 : 1;
            return new(values, fault == "shape" ? [1024, 2] : [2, 1024]);
        });
        using var provider = new BgeEmbeddingProvider(tokenizer, runner, "profile");
        var refusal = await Assert.ThrowsAsync<BgeInferenceException>(() => provider.CreateEmbeddingsAsync(["one", "two"], CancellationToken.None).AsTask());
        Assert.Equal("bge-embedding-output-invalid", refusal.ReasonCode);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Cancelled_embedding_batch_does_not_enter_native_execution()
    {
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((_, _) => throw new InvalidOperationException("unexpected-native-run"));
        using var provider = new BgeEmbeddingProvider(tokenizer, runner, "profile");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateEmbeddingsAsync(["one"], cancelled.Token).AsTask());
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gpu_model_access_requires_live_matching_ownership_before_cache_or_native_access(bool reranker)
    {
        using var tokenizer = new FakeTokenizer();
        var store = new RefusingStore("unexpected-model-access");
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "gpu-0", "synthetic-executor", 1, Guid.NewGuid());
        var wrong = new GpuOwnedWorkContext(handle, "wrong-model", "wrong-settings", CancellationToken.None);
        Task Open(GpuOwnedWorkContext context) => reranker
            ? BgeOfflineModels.OpenGpuRerankerAsync(store, tokenizer, context).AsTask()
            : BgeOfflineModels.OpenGpuEmbeddingAsync(store, tokenizer, context).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(wrong));
        var ended = new GpuOwnedWorkContext(handle, BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, CancellationToken.None);
        ended.Invalidate();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Open(ended));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var expired = new GpuOwnedWorkContext(handle, BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(expired));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(ModelStoreReasons.ArtifactMissing, false)]
    [InlineData(ModelStoreReasons.StoreUnavailable, false)]
    [InlineData(ModelStoreReasons.ArtifactMissing, true)]
    [InlineData(ModelStoreReasons.StoreUnavailable, true)]
    public async Task Owned_gpu_cache_miss_fails_closed_without_native_creation(string reason, bool reranker)
    {
        var store = new RefusingStore(reason);
        var spec = new ModelBundleSpecification(1, [new(reranker ? BgeOfflineModels.RerankerRevision : BgeOfflineModels.EmbeddingRevision,
            "tokenizer.json", reranker ? "69564b696052886ed0ac63fa393e928384e0f8caada38c1f4864a9bfbf379c15" : "6710678b12670bc442b99edc952c4d996ae309a7020c1fa0096dd245c2faf790",
            reranker ? 17098273 : 17082821)]);
        using var tokenizer = new FakeTokenizer(ModelManifestCodec.Fingerprint(spec));
        var owner = new GpuOwnedWorkContext(new(Guid.NewGuid(), "gpu-0", "executor", 1, Guid.NewGuid()),
            BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<BgeInferenceException>(() => reranker
            ? BgeOfflineModels.OpenGpuRerankerAsync(store, tokenizer, owner).AsTask()
            : BgeOfflineModels.OpenGpuEmbeddingAsync(store, tokenizer, owner).AsTask());
        Assert.Equal(reason, refusal.ReasonCode);
        Assert.Equal(1, store.Calls);
    }
    [Theory]
    [InlineData(ModelStoreReasons.ArtifactMissing)]
    [InlineData(ModelStoreReasons.StoreUnavailable)]
    public async Task Offline_tokenizer_cache_misses_fail_closed_before_touching_runtime(string reason)
    {
        var missing = new RefusingStore(reason);
        var runtime = new RefusingStore("unexpected-runtime-access");
        var refusal = await Assert.ThrowsAsync<BgeInferenceException>(() => BgeOfflineModels.CreateTokenizerAsync(
            missing, runtime, reranker: false, CancellationToken.None).AsTask());
        Assert.Equal(reason, refusal.ReasonCode);
        Assert.Equal(1, missing.Calls);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Wrong_tokenizer_profile_refuses_before_any_native_model_access()
    {
        using var tokenizer = new FakeTokenizer();
        var store = new RefusingStore("unexpected-model-access");
        var refusal = await Assert.ThrowsAsync<BgeInferenceException>(() => BgeOfflineModels.OpenCpuEmbeddingAsync(store, tokenizer, CancellationToken.None).AsTask());
        Assert.Equal("bge-tokenizer-profile-mismatch", refusal.ReasonCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Pair_template_preserves_both_complete_inputs_and_pads_only_with_mask_zero()
    {
        var pair = BgeInputBatch.Pair([0, 11, 2], [0, 22, 23, 2]);
        Assert.Equal([0L, 11, 2, 2, 22, 23, 2], pair);
        var batch = BgeInputBatch.Create([pair, [0, 9, 2]]);
        Assert.Equal(2, batch.Count);
        Assert.Equal(7, batch.SequenceLength);
        Assert.Equal([0L, 9, 2, 1, 1, 1, 1], batch.InputIds[7..]);
        Assert.Equal([1L, 1, 1, 0, 0, 0, 0], batch.AttentionMask[7..]);
    }

    [Fact]
    public void Maximum_is_checked_without_silent_truncation()
    {
        long[] Tokens(int count) => [0, .. Enumerable.Repeat(10L, count - 2), 2];
        Assert.Equal(512, BgeInputBatch.Create([Tokens(512)]).SequenceLength);
        Assert.Throws<BgeInferenceException>(() => BgeInputBatch.Create([Tokens(513)]));
        Assert.Equal(513, BgeInputBatch.Pair(Tokens(257), Tokens(256)).Length);
        Assert.Throws<BgeInferenceException>(() => BgeInputBatch.Create([BgeInputBatch.Pair(Tokens(257), Tokens(256))]));
        Assert.Throws<BgeInferenceException>(() => BgeInputBatch.Create([[0, 250002, 2]]));
        Assert.Throws<BgeInferenceException>(() => BgeInputBatch.Create([[11, 2]]));
    }

    [Fact]
    public void Embedding_output_is_dimension_checked_finite_and_normalised()
    {
        var output = new float[1024];
        output[0] = 3; output[1] = 4;
        var vector = BgeOutputValidation.Embedding(output, [1, 1024]);
        Assert.Equal(0.6f, vector[0]);
        Assert.Equal(0.8f, vector[1]);
        Assert.Equal(3, output[0]);
        Assert.Throws<BgeInferenceException>(() => BgeOutputValidation.Embedding(output, [1024, 1]));
        Assert.Throws<BgeInferenceException>(() => BgeOutputValidation.Embedding(new float[1024], [1, 1024]));
        output[10] = float.NaN;
        Assert.Throws<BgeInferenceException>(() => BgeOutputValidation.Embedding(output, [1, 1024]));
        Assert.Throws<BgeInferenceException>(() => BgeOutputValidation.Logits([1, float.PositiveInfinity], [2, 1], 2));
        Assert.Throws<BgeInferenceException>(() => BgeOutputValidation.Logits([1], [1, 1], 2));
    }

    [Fact]
    public async Task Reranking_preserves_candidate_identity_and_raw_logit_order()
    {
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((batch, _) => new BgeTensorOutput(Enumerable.Range(0, batch.Count).Select(i => i - 2f).ToArray(), [batch.Count, 1]));
        using var ranker = new BgeReranker(tokenizer, runner, "reranker-profile");
        var result = await ranker.RerankAsync("query", [new(8, "first"), new(2, "second")], CancellationToken.None);
        Assert.Equal([8L, 2], result.Scores.Select(s => s.PassageId));
        Assert.Equal([-2f, -1], result.Scores.Select(s => s.Logit));
        Assert.Equal("reranker-profile", result.ModelFingerprint);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Reranking_validates_the_whole_shortlist_before_execution_and_refuses_partial_output()
    {
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((batch, call) => new BgeTensorOutput(
            Enumerable.Repeat(call == 2 ? float.NaN : 1, batch.Count).ToArray(), [batch.Count, 1]));
        using var ranker = new BgeReranker(tokenizer, runner, "profile");
        await Assert.ThrowsAsync<BgeInferenceException>(() => ranker.RerankAsync("query", [new(1, "short"), new(2, "oversize")], CancellationToken.None).AsTask());
        Assert.Equal(0, runner.Calls);
        await Assert.ThrowsAsync<BgeInferenceException>(() => ranker.RerankAsync("query", [new(1, "a"), new(1, "b")], CancellationToken.None).AsTask());
        Assert.Equal(0, runner.Calls);
        await Assert.ThrowsAsync<BgeInferenceException>(() => ranker.RerankAsync("query",
            Enumerable.Range(1, 5).Select(i => new RerankPassage(i, "short")).ToArray(), CancellationToken.None).AsTask());
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task Cancellation_during_native_work_is_observed_before_returning_any_scores()
    {
        using var cancel = new CancellationTokenSource();
        using var tokenizer = new FakeTokenizer();
        using var runner = new FakeRunner((batch, _) => { cancel.Cancel(); return new([1], [1, 1]); });
        using var ranker = new BgeReranker(tokenizer, runner, "profile");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ranker.RerankAsync("query", [new(1, "short")], cancel.Token).AsTask());
    }

    private sealed class FakeTokenizer(string fingerprint = "synthetic") : IBgeTokenizer
    {
        public string Fingerprint => fingerprint;
        public long[] EncodeUntruncated(string text) => text == "oversize" ? [0, .. Enumerable.Repeat(10L, 512), 2] : [0, 10, 2];
        public int CountTokens(string text) => EncodeUntruncated(text).Length;
        public void Dispose() { }
    }

    private sealed class RefusingStore(string reason) : ILocalModelStore
    {
        public int Calls { get; private set; }
        public ValueTask<ModelResolutionResult> ResolveAsync(ModelBundleSpecification specification, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ModelResolutionResult(false, reason, null, [], false, null, null));
        }
    }

    private sealed class FakeRunner(Func<BgeInputBatch, int, BgeTensorOutput> run) : IBgeTensorRunner
    {
        public int Calls { get; private set; }
        public BgeTensorOutput Run(BgeInputBatch batch, CancellationToken cancellationToken) => run(batch, ++Calls);
        public void Dispose() { }
    }
}
