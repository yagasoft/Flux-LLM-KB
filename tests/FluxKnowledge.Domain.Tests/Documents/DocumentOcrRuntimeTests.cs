using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Documents;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class DocumentOcrRuntimeTests
{
    [Fact]
    public async Task Refused_model_gate_never_constructs_an_inference_session()
    {
        var sessionFactory = new RecordingSessionFactory();
        var runtime = new DocumentOcrRuntime(
            new StubModelResolver(new DocumentOcrModelResolution(
                false,
                ModelStoreReasons.ArtifactMissing,
                [new DocumentOcrModelRoleResolution(
                    DocumentOcrModelRole.TextDetection,
                    ModelStoreReasons.ArtifactMissing,
                    null,
                    [],
                    "inventory/verifications/refusal.json")],
                null)),
            sessionFactory);

        var result = await runtime.OpenAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ModelStoreReasons.ArtifactMissing, result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, sessionFactory.Calls);
    }

    [Fact]
    public async Task Open_runtime_keeps_model_files_and_sessions_until_disposal()
    {
        var modelFile = new TrackingModelFile();
        var modelLease = new DocumentOcrModelLease(
            Enum.GetValues<DocumentOcrModelRole>().ToDictionary(
                static role => role,
                _ => CreateVerifiedLease(modelFile)));
        var sessionFactory = new RecordingSessionFactory();
        var runtime = new DocumentOcrRuntime(
            new StubModelResolver(new DocumentOcrModelResolution(
                true,
                ModelStoreReasons.BundleVerified,
                [],
                modelLease)),
            sessionFactory);

        var result = await runtime.OpenAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        var lease = Assert.IsType<DocumentOcrRuntimeLease>(result.Lease);
        Assert.Equal(7, sessionFactory.Calls);
        Assert.False(modelFile.Disposed);
        Assert.All(sessionFactory.Sessions, static session => Assert.False(session.Disposed));

        lease.Dispose();

        Assert.True(modelFile.Disposed);
        Assert.All(sessionFactory.Sessions, static session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task Directml_factory_refuses_a_bundle_without_an_onnx_companion_before_reading_any_file()
    {
        var modelFile = new TrackingModelFile();
        using var lease = new VerifiedLocalModelLease(
            new Dictionary<string, IModelVerificationFile>(StringComparer.Ordinal)
            {
                ["inference.yml"] = modelFile
            },
            new NoopDisposable());
        var factory = new DirectMlDocumentOcrSessionFactory(deviceId: 0);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(DocumentOcrModelRole.TextDetection, lease, CancellationToken.None).AsTask());

        Assert.Equal("document-ocr-model-inference-onnx-missing", failure.Message);
        Assert.False(modelFile.ReadCalled);
    }

    private static VerifiedLocalModelLease CreateVerifiedLease(TrackingModelFile modelFile) => new(
        new Dictionary<string, IModelVerificationFile>(StringComparer.Ordinal)
        {
            ["inference.onnx"] = modelFile
        },
        new NoopDisposable());

    private sealed class StubModelResolver(DocumentOcrModelResolution result) : IDocumentOcrModelResolver
    {
        public ValueTask<DocumentOcrModelResolution> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingSessionFactory : IDocumentOcrSessionFactory
    {
        public int Calls { get; private set; }

        public List<TrackingSession> Sessions { get; } = [];

        public ValueTask<IDisposable> CreateAsync(DocumentOcrModelRole role, VerifiedLocalModelLease lease, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var session = new TrackingSession();
            Sessions.Add(session);
            return ValueTask.FromResult<IDisposable>(session);
        }
    }

    private sealed class TrackingSession : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingModelFile : IModelVerificationFile
    {
        public long ByteLength => 0;

        public bool Disposed { get; private set; }

        public bool ReadCalled { get; private set; }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ReadCalled = true;
            return ValueTask.FromResult(0);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
