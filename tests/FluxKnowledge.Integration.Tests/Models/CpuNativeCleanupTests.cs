using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class CpuNativeCleanupTests
{
    [Fact]
    public async Task Failed_returned_CPU_session_close_keeps_verified_files_after_store_pin_is_disposed()
    {
        var file = new SyntheticFile();
        var fileOwner = new CountingDisposable();
        var original = new VerifiedLocalModelLease(new Dictionary<string, IModelVerificationFile> { ["model.onnx"] = file }, fileOwner);
        using var store = new PinnedLocalModelStore(new SyntheticStore(original));
        var specification = new ModelBundleSpecification(1,
            [new ModelArtifactSpecification(new string('a', 40), "model.onnx", new string('0', 64), 1)]);
        var sessionLease = (await store.ResolveAsync(specification, CancellationToken.None)).Lease!;
        var closing = new IOException("synthetic-returned-session-close");
        var native = new CountingDisposable(() => throw closing);

        var failure = Record.Exception(() => BgeOnnxTensorRunner.DisposeSession(native, sessionLease, cpu: true));
        store.Dispose();

        Assert.False(file.Disposed);
        Assert.Equal(0, fileOwner.Calls);
        Assert.Equal(1, native.Calls);
        Assert.Contains(closing, Assert.IsType<CpuNativeReleaseUnconfirmedException>(failure).Flatten().InnerExceptions);
        Assert.Equal(@"J:\Models\synthetic\model.onnx", sessionLease.GetVerifiedLocalPath("model.onnx"));
    }

    private sealed class SyntheticStore(VerifiedLocalModelLease lease) : ILocalModelStore
    {
        public ValueTask<ModelResolutionResult> ResolveAsync(ModelBundleSpecification specification, CancellationToken ct) =>
            ValueTask.FromResult(new ModelResolutionResult(true, ModelStoreReasons.BundleVerified,
                ModelManifestCodec.Fingerprint(specification), [], true, "synthetic", lease));
    }

    [Fact]
    public void Non_CPU_session_disposal_preserves_the_original_failure_policy()
    {
        var file = new SyntheticFile();
        var fileOwner = new CountingDisposable();
        var lease = new VerifiedLocalModelLease(new Dictionary<string, IModelVerificationFile> { ["model.onnx"] = file }, fileOwner);
        var closing = new IOException("synthetic-non-CPU-close");
        Assert.Same(closing, Assert.Throws<IOException>(() =>
            BgeOnnxTensorRunner.DisposeSession(new CountingDisposable(() => throw closing), lease, cpu: false)));
        Assert.True(file.Disposed);
        Assert.Equal(1, fileOwner.Calls);
    }

    [Fact]
    public void Failed_open_and_failed_session_close_retain_the_machine_owner_and_verified_files()
    {
        var opening = new InvalidOperationException("synthetic-post-open-validation");
        var closing = new IOException("synthetic-native-close");
        var native = new CountingDisposable(() => throw closing);
        var file = new SyntheticFile();
        var fileOwner = new CountingDisposable();
        var lease = new VerifiedLocalModelLease(new Dictionary<string, IModelVerificationFile> { ["model.onnx"] = file }, fileOwner);
        var owner = new CountingDisposable();
        var knownLane = new CountingDisposable();
        var failure = Record.Exception(() => BgeOnnxTensorRunner.DisposeFailedOpen(opening, native, lease, cpu: true));

        BgeCpuResidentModelFactory.DisposeFailedLoad(failure!, [Lane(knownLane)], owner);

        Assert.Equal(0, owner.Calls);
        Assert.False(file.Disposed);
        Assert.Equal(0, fileOwner.Calls);
        Assert.Equal(1, native.Calls);
        Assert.Equal(1, knownLane.Calls);
        var uncertain = Assert.IsType<CpuNativeReleaseUnconfirmedException>(failure);
        Assert.Contains(opening, uncertain.Flatten().InnerExceptions);
        Assert.Contains(closing, uncertain.Flatten().InnerExceptions);
    }

    [Fact]
    public void Confirmed_failed_open_cleanup_releases_files_and_owner_after_known_lanes_close()
    {
        var opening = new InvalidOperationException("synthetic-post-open-validation");
        var native = new CountingDisposable();
        var file = new SyntheticFile();
        var fileOwner = new CountingDisposable();
        var lease = new VerifiedLocalModelLease(new Dictionary<string, IModelVerificationFile> { ["model.onnx"] = file }, fileOwner);
        var knownLane = new CountingDisposable();
        var owner = new CountingDisposable(() => Assert.Equal(1, knownLane.Calls));

        BgeOnnxTensorRunner.DisposeFailedOpen(opening, native, lease, cpu: true);
        BgeCpuResidentModelFactory.DisposeFailedLoad(opening, [Lane(knownLane)], owner);

        Assert.Equal(1, native.Calls);
        Assert.True(file.Disposed);
        Assert.Equal(1, fileOwner.Calls);
        Assert.Equal(1, owner.Calls);
    }

    private static CpuPassageInferenceLane Lane(CountingDisposable close) => new(new Embedding(), new Reranker(), close.Dispose);
    private sealed class CountingDisposable(Action? close = null) : IDisposable
    {
        public int Calls { get; private set; }
        public void Dispose() { Calls++; close?.Invoke(); }
    }
    private sealed class SyntheticFile : IModelVerificationFile
    {
        public bool Disposed { get; private set; }
        public long ByteLength => 1;
        public string ProtectedLocalPath => @"J:\Models\synthetic\model.onnx";
        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
    private sealed class Embedding : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Reranker : IPassageReranker
    {
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct) => throw new NotSupportedException();
    }
}
