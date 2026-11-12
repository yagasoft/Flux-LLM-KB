using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class BgeRerankerPrecisionTests
{
    [Fact]
    public void Explicit_FP32_preserves_identifiers_and_FP16_has_distinct_model_and_runtime_keys()
    {
        Assert.Equal("09006ce2e2677c71ad6094c20c66420a2c05b50944ecd7e40ed9bb269f57fc14", BgeOfflineModels.RerankerFingerprint);
        Assert.Equal(BgeOfflineModels.RerankerFingerprint, BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp32));
        Assert.Equal(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GetGpuRuntimeKey(BgeRerankerPrecision.Fp32));
        Assert.NotEqual(BgeOfflineModels.RerankerFingerprint, BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal));
        Assert.NotEqual(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GetGpuRuntimeKey(BgeRerankerPrecision.Fp16Internal));
        Assert.NotNull(typeof(BgeGpuModelFactory).GetConstructor([typeof(BgeGpuModelStores)]));
        Assert.NotNull(typeof(BgeGpuModelFactory).GetConstructor([typeof(BgeGpuModelStores), typeof(BgeRerankerPrecision)]));
        var store = new Store("unexpected-store-access");
        using var stores = new BgeGpuModelStores(store,store,store,store);
        Assert.Equal(BgeRerankerPrecision.Fp32, new BgeGpuModelFactory(stores).Precision);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Existing_dependency_injection_keeps_the_FP32_constructor_without_model_access()
    {
        var store = new Store("unexpected-store-access");
        using var stores = new BgeGpuModelStores(store, store, store, store);
        using var provider = new ServiceCollection().AddSingleton(stores)
            .AddSingleton<IBgeGpuModelFactory, BgeGpuModelFactory>().BuildServiceProvider();
        Assert.Equal(BgeRerankerPrecision.Fp32, provider.GetRequiredService<IBgeGpuModelFactory>().Precision);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(ModelStoreReasons.ArtifactMissing)]
    [InlineData(ModelStoreReasons.StoreUnavailable)]
    [InlineData(ModelStoreReasons.ArtifactLengthMismatch)]
    [InlineData(ModelStoreReasons.ArtifactSha256Mismatch)]
    public async Task FP16_resolves_only_its_exact_verified_pair_and_refuses_before_native_work(string reason)
    {
        using var tokenizer = new Tokenizer();
        var store = new Store(reason);
        var owner = Owner(BgeRerankerPrecision.Fp16Internal);
        var error = await Assert.ThrowsAsync<BgeInferenceException>(() => BgeOfflineModels.OpenGpuRerankerAsync(
            store, tokenizer, owner, BgeRerankerPrecision.Fp16Internal).AsTask());
        Assert.Equal(reason, error.ReasonCode);
        Assert.Equal(1, store.Calls);
        Assert.Equal([
            new ModelArtifactSpecification(BgeOfflineModels.RerankerRevision, "model.onnx",
                "56d4de1833a96c7f1723078a49ffc4869de1ae5ee3aeb04ae4a30398b7e985bf", 712554),
            new ModelArtifactSpecification(BgeOfflineModels.RerankerRevision, "model.onnx_data",
                "22146757162e027fea6b96911a78e0233b3636640d75e2ac7135cc1cfea654e4", 1135511554)
        ], store.Specification!.Files);
        Assert.True(owner.NativeCapacityReleased);
    }

    [Fact]
    public async Task FP32_owner_cannot_open_FP16_and_tokenizer_mismatch_cannot_reach_the_store()
    {
        var store = new Store("unexpected-store-access");
        using var tokenizer = new Tokenizer();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BgeOfflineModels.OpenGpuRerankerAsync(
            store, tokenizer, Owner(BgeRerankerPrecision.Fp32), BgeRerankerPrecision.Fp16Internal).AsTask());
        using var wrong = new Tokenizer("wrong-tokenizer");
        var error = await Assert.ThrowsAsync<BgeInferenceException>(() => BgeOfflineModels.OpenGpuRerankerAsync(
            store, wrong, Owner(BgeRerankerPrecision.Fp16Internal), BgeRerankerPrecision.Fp16Internal).AsTask());
        Assert.Equal("bge-tokenizer-profile-mismatch", error.ReasonCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Unknown_precision_and_cancelled_owner_refuse_without_store_access()
    {
        var unknown = (BgeRerankerPrecision)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => BgeOfflineModels.GetRerankerFingerprint(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => BgeOfflineModels.GetGpuRuntimeKey(unknown));
        var store = new Store("unexpected-store-access");
        using var tokenizer = new Tokenizer();
        using var stores = new BgeGpuModelStores(store,store,store,store);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgeGpuModelFactory(stores,unknown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BgeOfflineModels.VerifyGpuFilesAsync(stores,unknown,default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BgeOfflineModels.OpenGpuRerankerAsync(
            store, tokenizer, Owner(BgeRerankerPrecision.Fp32), unknown).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => BgeOfflineModels.OpenGpuRerankerAsync(
            store, tokenizer, Owner(BgeRerankerPrecision.Fp16Internal, cancellation.Token), BgeRerankerPrecision.Fp16Internal).AsTask());
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(BgeRerankerPrecision.Fp32, false)]
    [InlineData(BgeRerankerPrecision.Fp32, true)]
    [InlineData(BgeRerankerPrecision.Fp16Internal, false)]
    [InlineData(BgeRerankerPrecision.Fp16Internal, true)]
    public async Task Session_binds_profile_identity_and_preserves_actual_native_release(BgeRerankerPrecision precision, bool cleanupFails)
    {
        var factory = new Models(precision, cleanupFails);
        var session = new BgeGpuInferenceSession(factory);
        Assert.Equal(BgeOfflineModels.GetRerankerFingerprint(precision), session.RerankerFingerprint);
        var owner = Owner(precision);
        var result = await session.ExecuteSearchAsync(owner, (_, reranker, ct) => reranker.RerankAsync("query", [new(8,"passage")], ct));
        Assert.Equal(!cleanupFails, result.NativeCapacityReleased);
        Assert.Equal(1, factory.Opens);
        Assert.Equal(1, factory.Disposals);
        if (cleanupFails) { Assert.Null(result.Value); Assert.NotNull(result.RefusalReason); }
        else
        {
            Assert.Null(result.RefusalReason);
            Assert.Equal(BgeOfflineModels.GetRerankerFingerprint(precision), result.Value!.ModelFingerprint);
            Assert.Equal(8, Assert.Single(result.Value.Scores).PassageId);
        }
    }

    [Fact]
    public async Task Session_rejects_another_precision_owner_before_loading_and_wrong_output_after_cleanup()
    {
        var factory = new Models(BgeRerankerPrecision.Fp16Internal, false);
        var session = new BgeGpuInferenceSession(factory);
        var wrongOwner = await session.ExecuteSearchAsync(Owner(BgeRerankerPrecision.Fp32),
            (_, reranker, ct) => reranker.RerankAsync("query", [new(8,"passage")], ct));
        Assert.NotNull(wrongOwner.RefusalReason);
        Assert.Equal(0, factory.Opens);
        var wrongOutput = new Models(BgeRerankerPrecision.Fp16Internal, false, BgeOfflineModels.RerankerFingerprint);
        var result = await new BgeGpuInferenceSession(wrongOutput).ExecuteSearchAsync(Owner(BgeRerankerPrecision.Fp16Internal),
            (_, reranker, ct) => reranker.RerankAsync("query", [new(8,"passage")], ct));
        Assert.Equal("bge-reranker-output-invalid", result.RefusalReason);
        Assert.True(result.NativeCapacityReleased);
        Assert.Null(result.Value);
        Assert.Equal(1, wrongOutput.Disposals);
    }

    private static GpuOwnedWorkContext Owner(BgeRerankerPrecision precision, CancellationToken ct = default) => new(
        new(Guid.NewGuid(), "gpu-0", "probe", 1, Guid.NewGuid()), BgeOfflineModels.GetGpuRuntimeKey(precision),
        BgeOfflineModels.GpuSettingsFingerprint, ct);

    private sealed class Store(string reason) : ILocalModelStore
    {
        public int Calls { get; private set; }
        public ModelBundleSpecification? Specification { get; private set; }
        public ValueTask<ModelResolutionResult> ResolveAsync(ModelBundleSpecification specification, CancellationToken ct)
        {
            Calls++; Specification = specification;
            return ValueTask.FromResult(new ModelResolutionResult(false,reason,null,[],false,null,null));
        }
    }

    private sealed class Tokenizer(string? fingerprint = null) : IBgeTokenizer
    {
        public string Fingerprint { get; } = fingerprint ?? ModelManifestCodec.Fingerprint(new(1,
            [new(BgeOfflineModels.RerankerRevision,"tokenizer.json","69564b696052886ed0ac63fa393e928384e0f8caada38c1f4864a9bfbf379c15",17098273)]));
        public long[] EncodeUntruncated(string text) => throw new InvalidOperationException("No real tokenisation in profile checks.");
        public int CountTokens(string text) => throw new InvalidOperationException("No real tokenisation in profile checks.");
        public void Dispose() { }
    }

    internal sealed class Models(BgeRerankerPrecision precision, bool cleanupFails, string? outputFingerprint = null) : IBgeGpuModelFactory
    {
        public BgeRerankerPrecision Precision { get; } = precision;
        public int Opens { get; private set; }
        public int Disposals { get; private set; }
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext owner) =>
            throw new InvalidOperationException("Rerank only.");
        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext owner)
        {
            Opens++;
            var allocation = owner.BeginNativeAllocation();
            return ValueTask.FromResult(new BgeGpuModelLease<IPassageReranker>(new Ranker(outputFingerprint ?? BgeOfflineModels.GetRerankerFingerprint(Precision)), () =>
            {
                Disposals++;
                if (cleanupFails) throw new InvalidOperationException("synthetic-close-failure");
                allocation.ConfirmReleased();
            }));
        }
    }
    private sealed class Ranker(string fingerprint) : IPassageReranker
    {
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct) =>
            ValueTask.FromResult(new RerankResult(passages.Select(p => new RerankScore(p.PassageId,1)).ToArray(),fingerprint));
    }
}
