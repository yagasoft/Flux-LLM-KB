using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class BgeCpuOfflineProbeTests
{
    [BgeNativeProbeFact]
    public async Task Native_tokenizers_are_disposed_while_the_verified_runtime_survives_failure_and_repetition()
    {
        var tokenizerStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]));
        var runtimeStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
        bool Loaded()
        {
            using var process = Process.GetCurrentProcess();
            return process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("hf_tokenizers.dll", StringComparison.OrdinalIgnoreCase));
        }
        using var current = Process.GetCurrentProcess();
        var before = current.PrivateMemorySize64;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            using var first = await BgeOfflineModels.CreateTokenizerAsync(tokenizerStore, runtimeStore, false, CancellationToken.None);
            using var second = await BgeOfflineModels.CreateTokenizerAsync(tokenizerStore, runtimeStore, false, CancellationToken.None);
            var expected = first.EncodeUntruncated("Sample request.");
            // Exercise separate native entry points and repeated resolver use.
            for (var request = 0; request < 10; request++) Assert.Equal(expected, second.EncodeUntruncated("Sample request."));
            first.Dispose();
            Assert.True(Loaded());
            Assert.Equal(expected, second.EncodeUntruncated("Sample request."));
            second.Dispose();
            Assert.Throws<ObjectDisposedException>(() => second.EncodeUntruncated("Disposed request."));
            Assert.True(Loaded());
        }
        current.Refresh();
        // The former unload cycle retained ~168 MB each and exceeded 3 GB in 20
        // tokenizer-only cycles. Two instances per iteration exercise 80 cycles.
        Assert.InRange(current.PrivateMemorySize64 - before, long.MinValue, 2L * 1024 * 1024 * 1024);
        await using var fixture = await LocalModelFixture.CreateAsync();
        var spec = await fixture.SeedCompleteBundleAsync();
        using var malformed = (await fixture.Store.ResolveAsync(spec, CancellationToken.None)).Lease!;
        using var runtime = (await runtimeStore.ResolveAsync(BgeOfflineModels.TokenizerRuntime, CancellationToken.None)).Lease!;
        // The already verified synthetic processor JSON cannot construct a tokenizer:
        // native load occurred; failure balances the request reference, not the process pin.
        Assert.ThrowsAny<Exception>(() => NativeBgeTokenizer.OpenForTest(malformed, runtime, "processor.json"));
        Assert.True(Loaded());
    }

    [BgeNativeProbeFact]
    public async Task Verified_native_tokenisers_and_cpu_models_match_the_offline_reference()
    {
        var referencePath = @"J:\Models\manifests\bge-reranker-source-20260927\native-reference.json";
        using var reference = JsonDocument.Parse(await WindowsModelVerificationFiles.ReadLocalManifestAsync(referencePath, 1024 * 1024, CancellationToken.None));
        Assert.Equal(0, reference.RootElement.GetProperty("network_attempts").GetInt32());
        var embeddingStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]));
        var rerankerStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["bundles", "bge-reranker-v2-m3-onnx", BgeOfflineModels.RerankerRevision, BgeOfflineModels.RerankerExport]));
        var rerankerTokenizerStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["bundles", "bge-reranker-v2-m3", BgeOfflineModels.RerankerRevision]));
        var runtimeStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
        var stopwatch = Stopwatch.StartNew();
        using var embeddingTokenizer = await BgeOfflineModels.CreateTokenizerAsync(embeddingStore, runtimeStore, reranker: false, CancellationToken.None);
        using var rerankerTokenizer = await BgeOfflineModels.CreateTokenizerAsync(rerankerTokenizerStore, runtimeStore, reranker: true, CancellationToken.None);
        var tokenizerLoadMs = stopwatch.Elapsed.TotalMilliseconds;
        foreach (var fixture in reference.RootElement.GetProperty("embedding_fixtures").EnumerateArray())
        {
            var text = fixture.GetProperty("text").GetString()!;
            var expected = fixture.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()).ToArray();
            Assert.Equal(expected, embeddingTokenizer.EncodeUntruncated(text));
            Assert.Equal(expected.Length, embeddingTokenizer.CountTokens(text));
        }
        var fixtures = reference.RootElement.GetProperty("reranker_fixtures").EnumerateArray().ToArray();
        foreach (var fixture in fixtures)
        {
            var actual = BgeInputBatch.Pair(rerankerTokenizer.EncodeUntruncated(fixture.GetProperty("query").GetString()!),
                rerankerTokenizer.EncodeUntruncated(fixture.GetProperty("passage").GetString()!));
            Assert.Equal(fixture.GetProperty("input_ids").EnumerateArray().Select(i => i.GetInt64()), actual);
        }
        Assert.Throws<BgeInferenceException>(() => embeddingTokenizer.EncodeUntruncated("broken\ud800"));
        var beforeEmbedding = stopwatch.Elapsed.TotalMilliseconds;
        double maximumEmbeddingError = 0;
        using (var provider = await BgeOfflineModels.OpenCpuEmbeddingAsync(embeddingStore, embeddingTokenizer, CancellationToken.None))
        {
            foreach (var fixture in reference.RootElement.GetProperty("embedding_fixtures").EnumerateArray().Where(f => f.TryGetProperty("values", out _)))
            {
                var actual = await provider.CreateEmbeddingAsync(fixture.GetProperty("text").GetString()!, CancellationToken.None);
                var expected = fixture.GetProperty("values").EnumerateArray().Select(i => i.GetSingle()).ToArray();
                var error = actual.Values.Zip(expected, (a, b) => Math.Abs((double)a - b)).Max();
                maximumEmbeddingError = Math.Max(maximumEmbeddingError, error);
                Assert.InRange(error, 0, 1e-4);
                Assert.Equal(BgeOfflineModels.EmbeddingFingerprint, actual.ModelFingerprint);
            }
        }
        var embeddingTotalMs = stopwatch.Elapsed.TotalMilliseconds - beforeEmbedding;
        var beforeReranking = stopwatch.Elapsed.TotalMilliseconds;
        double maximumLogitError = 0;
        using (var ranker = await BgeOfflineModels.OpenCpuRerankerAsync(rerankerStore, rerankerTokenizer, CancellationToken.None))
        {
            for (var i = 0; i < fixtures.Length; i++)
            {
                var f = fixtures[i];
                var actual = await ranker.RerankAsync(f.GetProperty("query").GetString()!, [new(i + 1, f.GetProperty("passage").GetString()!)], CancellationToken.None);
                var error = Math.Abs((double)actual.Scores[0].Logit - f.GetProperty("logit").GetDouble());
                maximumLogitError = Math.Max(maximumLogitError, error);
                Assert.InRange(error, 0, 1e-3);
            }
            // Genuine padding and multiple native batches preserve the supplied identities.
            var grouped = fixtures.Where(f => f.GetProperty("query").GetString() == "What does hybrid search combine?").ToArray();
            var batch = await ranker.RerankAsync("What does hybrid search combine?",
                grouped.Select((f, i) => new RerankPassage(i + 1, f.GetProperty("passage").GetString()!)).ToArray(), CancellationToken.None);
            Assert.Equal([1L, 2], batch.Scores.Select(s => s.PassageId));
            for (var i = 0; i < batch.Scores.Count; i++)
                Assert.InRange(Math.Abs((double)batch.Scores[i].Logit - grouped[i].GetProperty("logit").GetDouble()), 0, 1e-3);
            var over = reference.RootElement.GetProperty("oversized_pair");
            var refusal = await Assert.ThrowsAsync<BgeInferenceException>(() => ranker.RerankAsync(over.GetProperty("query").GetString()!,
                [new(1, over.GetProperty("passage").GetString()!)], CancellationToken.None).AsTask());
            Assert.Equal("bge-input-too-long", refusal.ReasonCode);
        }
        var receipt = new { device = "cpu", tokenizerLoadMs, embeddingTotalMs,
            rerankingTotalMs = stopwatch.Elapsed.TotalMilliseconds - beforeReranking,
            maximumEmbeddingError, maximumLogitError,
            embeddingFingerprint = BgeOfflineModels.EmbeddingFingerprint, rerankerFingerprint = BgeOfflineModels.RerankerFingerprint,
            scope = "Public synthetic native CPU parity; not full-search latency or retrieval-quality acceptance" };
        await File.WriteAllTextAsync(@"J:\Models\inventory\bge-native-cpu-parity-20260927.json", JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class BgeNativeProbeFactAttribute : FactAttribute
{
    public BgeNativeProbeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FLUX_KB_RUN_BGE_NATIVE_PROBE") != "1")
            Skip = "Opt-in verified J: models and public offline reference required; ordinary tests never acquire/load real models.";
    }
}
