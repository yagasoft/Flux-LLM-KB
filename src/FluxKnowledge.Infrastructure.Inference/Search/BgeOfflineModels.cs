using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Fixed offline profiles. No provider-cache lookup or acquisition path.</summary>
public static class BgeOfflineModels
{
    private static readonly ProcessTokenizerRuntimePin TokenizerRuntimePin = new();
    public const string EmbeddingRevision = "5617a9f61b028005a4858fdac845db406aefb181";
    public const string RerankerRevision = "953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e";
    public const string RerankerExport = "f083b9dbba6d1b56869a6572e5d7f9f5fd17f961d3c521ea2424d80c5f800580";
    private const string EmbeddingTokenizerHash = "6710678b12670bc442b99edc952c4d996ae309a7020c1fa0096dd245c2faf790";
    private const string RerankerTokenizerHash = "69564b696052886ed0ac63fa393e928384e0f8caada38c1f4864a9bfbf379c15";

    // Recursive ONNX TensorProto inspection for these exact graph hashes established
    // that model.onnx_data is the complete external dependency closure in both cases.
    // A new graph hash requires repeating that inspection and native reference parity.
    internal static readonly ModelBundleSpecification EmbeddingModel = new(1,
    [
        new(EmbeddingRevision, "model.onnx", "f84251230831afb359ab26d9fd37d5936d4d9bb5d1d5410e66442f630f24435b", 724923),
        new(EmbeddingRevision, "model.onnx_data", "1eebfb28493f67bba03ce0ef64bfdc7fc5a3bd9d7493f818bb1d78cd798416b4", 2266820608)
    ]);
    internal static readonly ModelBundleSpecification RerankerModel = new(1,
    [
        new(RerankerRevision, "model.onnx", "a4d1c276f48200935f10d134b4c5c21233727cafc88a4ea7fb9c40ce10b63478", 550237),
        new(RerankerRevision, "model.onnx_data", "fe17b9d4a11cafc0680c183ac3b704b14f2f935514a8527a4423013f0f27325e", 2271023108)
    ]);
    internal static readonly ModelBundleSpecification TokenizerRuntime = new(1,
    [
        RuntimeFile("Tokenizers.DotNet.dll", "6730d3a5af8a5280e0f26edbaa24c2025c4ebb574de82c74bc715f0a3f531d88", 14848),
        RuntimeFile("hf_tokenizers.dll", "387e3e5ce2afa18eadb9615ee2c07151dc1a2123c338d60a7d51eb01ff07da1b", 3968599),
        RuntimeFile("System.Runtime.CompilerServices.Unsafe.dll", "60e5957ad8ceddc68650747a71598c0d84c291943c8693135333335d3dbaaf3b", 19232)
    ]);

    public static string EmbeddingFingerprint { get; } = Fingerprint(EmbeddingModel, EmbeddingTokenizerHash, "xlmr-single/cls/l2/1024/fp32/max512/v1");
    public static string EmbeddingTokenizerFingerprint { get; } = ModelManifestCodec.Fingerprint(new(1,
        [new(EmbeddingRevision, "tokenizer.json", EmbeddingTokenizerHash, 17082821)]));
    public static string RerankerFingerprint { get; } = Fingerprint(RerankerModel, RerankerTokenizerHash, "xlmr-pair/raw-logit/fp32/max512/batch4/v1");
    public static string GpuRuntimeKey { get; } = "bge-passage:" + Hash(EmbeddingFingerprint + "\n" + RerankerFingerprint);
    public static string GpuSettingsFingerprint { get; } = Hash("ort1.24.4/directml/device0/sequential/mempattern0/threads4/inter1/batch4/load-run-unload/v1");

    public static async ValueTask VerifyGpuFilesAsync(BgeGpuModelStores stores, CancellationToken ct)
    {
        using var embedding = await ResolveLeaseAsync(stores.Embedding, EmbeddingModel, ct).ConfigureAwait(false);
        using var reranker = await ResolveLeaseAsync(stores.Reranker, RerankerModel, ct).ConfigureAwait(false);
        using var embeddingTokenizer = await ResolveLeaseAsync(stores.Embedding, new(1,
            [new(EmbeddingRevision, "tokenizer.json", EmbeddingTokenizerHash, 17082821)]), ct).ConfigureAwait(false);
        using var rerankerTokenizer = await ResolveLeaseAsync(stores.RerankerTokenizer, new(1,
            [new(RerankerRevision, "tokenizer.json", RerankerTokenizerHash, 17098273)]), ct).ConfigureAwait(false);
        using var runtime = await ResolveLeaseAsync(stores.TokenizerRuntime, TokenizerRuntime, ct).ConfigureAwait(false);
    }

    public static async ValueTask<IBgeTokenizer> CreateTokenizerAsync(
        ILocalModelStore tokenizerStore, ILocalModelStore runtimeStore, bool reranker, CancellationToken cancellationToken)
    {
        var spec = new ModelBundleSpecification(1, [new(reranker ? RerankerRevision : EmbeddingRevision, "tokenizer.json",
            reranker ? RerankerTokenizerHash : EmbeddingTokenizerHash, reranker ? 17098273 : 17082821)]);
        var tokenizer = await ResolveLeaseAsync(tokenizerStore, spec, cancellationToken).ConfigureAwait(false);
        VerifiedLocalModelLease? runtime = null;
        try
        {
            runtime = await ResolveLeaseAsync(runtimeStore, TokenizerRuntime, cancellationToken).ConfigureAwait(false);
            // The pin owns an additional verified lease. The current request continues
            // to own and dispose its own runtime/tokenizer leases, including failure.
            await TokenizerRuntimePin.EnsureAsync(ModelManifestCodec.Fingerprint(TokenizerRuntime),
                runtime.GetVerifiedLocalPath("hf_tokenizers.dll"),
                ct => ResolveLeaseAsync(runtimeStore, TokenizerRuntime, ct), cancellationToken).ConfigureAwait(false);
            return await NativeBgeTokenizer.CreateAsync(tokenizer, runtime, ModelManifestCodec.Fingerprint(spec), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            runtime?.Dispose();
            tokenizer.Dispose();
            throw;
        }
    }

    public static async ValueTask<BgeEmbeddingProvider> OpenCpuEmbeddingAsync(
        ILocalModelStore store, IBgeTokenizer tokenizer, CancellationToken cancellationToken)
    {
        RequireTokenizer(tokenizer, reranker: false);
        var lease = await ResolveLeaseAsync(store, EmbeddingModel, cancellationToken).ConfigureAwait(false);
        return new(tokenizer, BgeOnnxTensorRunner.Open(lease, embedding: true, cancellationToken), EmbeddingFingerprint);
    }

    public static async ValueTask<BgeReranker> OpenCpuRerankerAsync(
        ILocalModelStore store, IBgeTokenizer tokenizer, CancellationToken cancellationToken)
    {
        RequireTokenizer(tokenizer, reranker: true);
        var lease = await ResolveLeaseAsync(store, RerankerModel, cancellationToken).ConfigureAwait(false);
        return new(tokenizer, BgeOnnxTensorRunner.Open(lease, embedding: false, cancellationToken), RerankerFingerprint);
    }

    public static async ValueTask<BgeEmbeddingProvider> OpenGpuEmbeddingAsync(
        ILocalModelStore store, IBgeTokenizer tokenizer, GpuOwnedWorkContext ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ownership.RequireActive(GpuRuntimeKey, GpuSettingsFingerprint);
        RequireTokenizer(tokenizer, reranker: false);
        var lease = await ResolveLeaseAsync(store, EmbeddingModel, ownership.CancellationToken).ConfigureAwait(false);
        return new(tokenizer, BgeOnnxTensorRunner.Open(lease, embedding: true, ownership.CancellationToken, ownership), EmbeddingFingerprint);
    }

    public static async ValueTask<BgeReranker> OpenGpuRerankerAsync(
        ILocalModelStore store, IBgeTokenizer tokenizer, GpuOwnedWorkContext ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ownership.RequireActive(GpuRuntimeKey, GpuSettingsFingerprint);
        RequireTokenizer(tokenizer, reranker: true);
        var lease = await ResolveLeaseAsync(store, RerankerModel, ownership.CancellationToken).ConfigureAwait(false);
        return new(tokenizer, BgeOnnxTensorRunner.Open(lease, embedding: false, ownership.CancellationToken, ownership), RerankerFingerprint);
    }

    private static void RequireTokenizer(IBgeTokenizer tokenizer, bool reranker)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        var expected = new ModelBundleSpecification(1, [new(reranker ? RerankerRevision : EmbeddingRevision, "tokenizer.json",
            reranker ? RerankerTokenizerHash : EmbeddingTokenizerHash, reranker ? 17098273 : 17082821)]);
        if (tokenizer.Fingerprint != ModelManifestCodec.Fingerprint(expected)) throw new BgeInferenceException("bge-tokenizer-profile-mismatch");
    }

    private static async ValueTask<VerifiedLocalModelLease> ResolveLeaseAsync(
        ILocalModelStore store, ModelBundleSpecification specification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var result = await store.ResolveAsync(specification, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Lease is null)
        {
            result.Lease?.Dispose();
            throw new BgeInferenceException(result.ReasonCode);
        }
        if (!result.ReceiptPersisted || result.BundleFingerprint != ModelManifestCodec.Fingerprint(specification))
        {
            result.Lease.Dispose();
            throw new BgeInferenceException("bge-model-verification-mismatch");
        }
        return result.Lease;
    }

    private static ModelArtifactSpecification RuntimeFile(string name, string hash, long bytes) => new(hash, name, hash, bytes);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Fingerprint(ModelBundleSpecification model, string tokenizerHash, string policy) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ModelManifestCodec.Fingerprint(model) + "\n" + tokenizerHash + "\n" + policy))).ToLowerInvariant();
}
