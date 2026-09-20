using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class PaddleOcrVlmModelGateTests
{
    [Fact]
    public async Task Gate_verifies_each_fixed_manifest_once_and_holds_the_local_leases()
    {
        var store = new RecordingStore();
        var openedBundles = 0;
        using var gate = new PaddleOcrVlmModelGate(
            store,
            ReadManifestAsync,
            (_, _) =>
            {
                openedBundles++;
                return ValueTask.FromResult<IDisposable>(new NoopDisposable());
            });

        var first = gate.EnsureVerifiedAsync(CancellationToken.None).AsTask();
        var second = gate.EnsureVerifiedAsync(CancellationToken.None).AsTask();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded, result.ReasonCode));
        Assert.Equal(3, store.Resolutions.Count);
        Assert.Equal(1, openedBundles);
        Assert.Equal(
            [
                PaddleOcrVlmModelGate.VlmManifestPath,
                PaddleOcrVlmModelGate.LayoutManifestPath,
                PaddleOcrVlmModelGate.OrientationManifestPath
            ],
            store.ManifestPaths);
        Assert.All(store.Leases, lease => Assert.False(lease.Disposed));
    }

    [Fact]
    public async Task Gate_refuses_a_cache_miss_before_opening_provider_bundle_paths()
    {
        var store = new RecordingStore(failAtResolution: 2);
        var bundleOpens = 0;
        using var gate = new PaddleOcrVlmModelGate(
            store,
            ReadManifestAsync,
            (_, _) =>
            {
                bundleOpens++;
                return ValueTask.FromResult<IDisposable>(new NoopDisposable());
            });

        var result = await gate.EnsureVerifiedAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("document-ocr-model-artifact-missing", result.ReasonCode);
        Assert.Equal(2, store.Resolutions.Count);
        Assert.Equal(0, bundleOpens);
        Assert.True(Assert.Single(store.Leases).Disposed);
    }

    private static ValueTask<byte[]> ReadManifestAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        Assert.Contains(path,
            new[]
            {
                PaddleOcrVlmModelGate.VlmManifestPath,
                PaddleOcrVlmModelGate.LayoutManifestPath,
                PaddleOcrVlmModelGate.OrientationManifestPath
            });
        Assert.Equal(ModelManifestCodec.MaximumBytes, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var revision = path switch
        {
            PaddleOcrVlmModelGate.VlmManifestPath => PaddleOcrVlmModelGate.VlmRevision,
            PaddleOcrVlmModelGate.LayoutManifestPath => PaddleOcrVlmModelGate.LayoutRevision,
            _ => PaddleOcrVlmModelGate.OrientationRevision
        };
        var filename = $"{revision[..8]}.bin";
        var bytes = Encoding.UTF8.GetBytes(path);
        var manifest = new
        {
            schemaVersion = 1,
            files = new[]
            {
                new
                {
                    revision,
                    filename,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    byteLength = bytes.LongLength
                }
            }
        };
        return ValueTask.FromResult(JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private sealed class RecordingStore(int? failAtResolution = null) : ILocalModelStore
    {
        public List<ModelBundleSpecification> Resolutions { get; } = [];
        public List<LeaseState> Leases { get; } = [];
        public IReadOnlyList<string> ManifestPaths => Resolutions.Select(static specification =>
            specification.Files.Single().Revision switch
            {
                PaddleOcrVlmModelGate.VlmRevision => PaddleOcrVlmModelGate.VlmManifestPath,
                PaddleOcrVlmModelGate.LayoutRevision => PaddleOcrVlmModelGate.LayoutManifestPath,
                _ => PaddleOcrVlmModelGate.OrientationManifestPath
            }).ToArray();

        public ValueTask<ModelResolutionResult> ResolveAsync(
            ModelBundleSpecification specification,
            CancellationToken cancellationToken)
        {
            Resolutions.Add(specification);
            if (Resolutions.Count == failAtResolution)
            {
                return ValueTask.FromResult(new ModelResolutionResult(
                    false,
                    ModelStoreReasons.ArtifactMissing,
                    null,
                    [],
                    true,
                    "inventory/verifications/refusal.json",
                    null));
            }

            var state = new LeaseState();
            Leases.Add(state);
            var file = specification.Files.Single();
            var lease = new VerifiedLocalModelLease(
                new Dictionary<string, IModelVerificationFile>
                {
                    [file.Filename] = new MemoryModelFile(file.ByteLength)
                },
                state);
            return ValueTask.FromResult(new ModelResolutionResult(
                true,
                ModelStoreReasons.BundleVerified,
                ModelManifestCodec.Fingerprint(specification),
                [],
                true,
                "inventory/verifications/success.json",
                lease));
        }
    }

    private sealed class LeaseState : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class MemoryModelFile(long byteLength) : IModelVerificationFile
    {
        public long ByteLength => byteLength;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public void Dispose()
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
