using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Integrations.Models;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class BgeCpuOfflineProbeTests(ITestOutputHelper output)
{
    [BgeNativeProbeFact]
    public async Task Changing_caller_threads_do_not_accumulate_native_tokenizer_arenas()
    {
        var tokenizerStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]));
        var runtimeStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
        long[] expected;
        using (var tokenizer = await BgeOfflineModels.CreateTokenizerAsync(tokenizerStore, runtimeStore, false, CancellationToken.None))
            expected = tokenizer.EncodeUntruncated("Sample request. English العربية.");
        var specification = new FluxKnowledge.Application.Models.ModelBundleSpecification(1,
            [new(BgeOfflineModels.EmbeddingRevision, "tokenizer.json",
                "6710678b12670bc442b99edc952c4d996ae309a7020c1fa0096dd245c2faf790", 17082821)]);
        await RunCaller(); // Warm a complete native lifecycle before measuring retention.
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.PrivateMemorySize64;
        for (var caller = 0; caller < 32; caller++) await RunCaller();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        output.WriteLine($"Departed callers: 32; private bytes before: {before}; after: {process.PrivateMemorySize64}; growth: {process.PrivateMemorySize64 - before}.");
        // Native model initialisation on each departed thread formerly retained
        // several committed 32 MiB arenas, even after successful cleanup.
        // Managed GC cannot release those arenas; a fixed owner must reuse them.
        Assert.InRange(process.PrivateMemorySize64 - before, long.MinValue, 128L * 1024 * 1024);

        async Task RunCaller()
        {
            // Resolve exact local files before starting the caller, so async file IO
            // cannot move native initialisation onto a reusable thread-pool thread.
            var tokenizerLease = (await tokenizerStore.ResolveAsync(specification, CancellationToken.None)).Lease!;
            FluxKnowledge.Application.Models.VerifiedLocalModelLease runtimeLease;
            try { runtimeLease = (await runtimeStore.ResolveAsync(BgeOfflineModels.TokenizerRuntime, CancellationToken.None)).Lease!; }
            catch { tokenizerLease.Dispose(); throw; }
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var ownedTokenizerLease = tokenizerLease;
                    using var ownedRuntimeLease = runtimeLease;
                    using var tokenizer = NativeBgeTokenizer.OpenForTest(tokenizerLease, runtimeLease, "tokenizer.json");
                    Assert.Equal(expected, tokenizer.EncodeUntruncated("Sample request. English العربية."));
                }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            try { thread.Start(); }
            catch { runtimeLease.Dispose(); tokenizerLease.Dispose(); throw; }
            // A timeout must not release files still owned by the background caller.
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Tokenizer caller did not finish.");
            Assert.Null(failure);
        }
    }

    [BgeNativeProbeFact]
    public async Task Concurrent_encode_and_dispose_complete_without_releasing_native_state_under_a_caller()
    {
        var tokenizerStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]));
        var runtimeStore = new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(
            ["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
        using var tokenizer = await BgeOfflineModels.CreateTokenizerAsync(tokenizerStore, runtimeStore, false, CancellationToken.None);
        var expected = tokenizer.EncodeUntruncated("Sample concurrent request.");
        using var start = new ManualResetEventSlim();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var callers = Enumerable.Range(0, 16).Select(index => new Thread(() =>
        {
            start.Wait();
            try
            {
                if (index % 2 == 0)
                {
                    try { Assert.Equal(expected, tokenizer.EncodeUntruncated("Sample concurrent request.")); }
                    catch (ObjectDisposedException) { }
                }
                else tokenizer.Dispose();
            }
            catch (Exception exception) { failures.Enqueue(exception); }
        }) { IsBackground = true }).ToArray();
        foreach (var caller in callers) caller.Start();
        start.Set();
        foreach (var caller in callers) Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        Assert.Empty(failures);
        Assert.Throws<ObjectDisposedException>(() => tokenizer.EncodeUntruncated("After disposal."));
        tokenizer.Dispose();
    }

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
