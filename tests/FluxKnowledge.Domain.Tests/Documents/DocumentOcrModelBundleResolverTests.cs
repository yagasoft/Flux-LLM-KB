using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Documents;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class DocumentOcrModelBundleResolverTests
{
    [Fact]
    public async Task Missing_companion_refuses_without_attempting_later_roles_and_releases_held_leases()
    {
        var firstLeaseFile = new TrackingFile();
        var store = new RecordingModelStore((call, specification) => call switch
        {
            1 => Verified(specification, firstLeaseFile),
            2 => Refused(specification, ModelStoreReasons.ArtifactMissing),
            _ => throw new InvalidOperationException("A resolver must stop at the first refused companion bundle.")
        });
        var resolver = new DocumentOcrModelBundleResolver(store, ReadManifestAsync);

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ModelStoreReasons.ArtifactMissing, result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.Equal(2, store.Calls);
        Assert.True(firstLeaseFile.Disposed);
        Assert.Equal(
            [DocumentOcrModelRole.TextDetection, DocumentOcrModelRole.EnglishRecognition],
            result.Roles.Select(static role => role.Role));
        Assert.Equal(ModelStoreReasons.BundleVerified, result.Roles[0].ReasonCode);
        Assert.Equal(ModelStoreReasons.ArtifactMissing, result.Roles[1].ReasonCode);
    }

    [Fact]
    public async Task Complete_bundle_holds_every_verified_file_until_the_document_ocr_lease_is_disposed()
    {
        var files = new List<TrackingFile>();
        var store = new RecordingModelStore((_, specification) =>
        {
            var file = new TrackingFile();
            files.Add(file);
            return Verified(specification, file);
        });
        var resolver = new DocumentOcrModelBundleResolver(store, ReadManifestAsync);

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        var lease = Assert.IsType<DocumentOcrModelLease>(result.Lease);
        Assert.Equal(Enum.GetValues<DocumentOcrModelRole>(), result.Roles.Select(static role => role.Role));
        Assert.All(files, static file => Assert.False(file.Disposed));

        lease.Dispose();

        Assert.All(files, static file => Assert.True(file.Disposed));
    }

    private static ValueTask<ModelBundleSpecification> ReadManifestAsync(
        DocumentOcrModelRole role,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = ((int)role + 1).ToString("x")[0];
        return ValueTask.FromResult(new ModelBundleSpecification(
            1,
            [new ModelArtifactSpecification(
                new string(revision, 40),
                "inference.onnx",
                new string('a', 64),
                0)]));
    }

    private static ModelResolutionResult Verified(ModelBundleSpecification specification, TrackingFile file) => new(
        true,
        ModelStoreReasons.BundleVerified,
        ModelManifestCodec.Fingerprint(specification),
        [new ModelArtifactObservation("inference.onnx", ModelStoreReasons.BundleVerified, 0, new string('a', 64))],
        true,
        "J:\\Models\\inventory\\verifications\\test.json",
        new VerifiedLocalModelLease(
            new Dictionary<string, IModelVerificationFile>(StringComparer.Ordinal)
            {
                ["inference.onnx"] = file
            },
            new NoopDisposable()));

    private static ModelResolutionResult Refused(ModelBundleSpecification specification, string reasonCode) => new(
        false,
        reasonCode,
        ModelManifestCodec.Fingerprint(specification),
        [new ModelArtifactObservation("inference.onnx", reasonCode, null, null)],
        true,
        "J:\\Models\\inventory\\verifications\\test.json",
        null);

    private sealed class RecordingModelStore(
        Func<int, ModelBundleSpecification, ModelResolutionResult> resolve) : ILocalModelStore
    {
        public int Calls { get; private set; }

        public ValueTask<ModelResolutionResult> ResolveAsync(
            ModelBundleSpecification specification,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(resolve(Calls, specification));
        }
    }

    private sealed class TrackingFile : IModelVerificationFile
    {
        public long ByteLength => 0;

        public bool Disposed { get; private set; }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public void Dispose() => Disposed = true;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
