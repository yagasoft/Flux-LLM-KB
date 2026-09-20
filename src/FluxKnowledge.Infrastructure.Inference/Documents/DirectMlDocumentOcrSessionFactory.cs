using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;
using Microsoft.ML.OnnxRuntime;

namespace FluxKnowledge.Infrastructure.Inference.Documents;

/// <summary>
/// Creates a DirectML ONNX session from a verified, held local model file.
/// It deliberately exposes no provider selection or remote model path.
/// </summary>
public sealed class DirectMlDocumentOcrSessionFactory(int deviceId) : IDocumentOcrSessionFactory
{
    private const string OnnxFilename = "inference.onnx";
    private const long MaximumOnnxBytes = 1024L * 1024L * 1024L;

    public async ValueTask<IDisposable> CreateAsync(
        DocumentOcrModelRole role,
        VerifiedLocalModelLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        var model = lease.Files.SingleOrDefault(static file => string.Equals(file.Filename, OnnxFilename, StringComparison.Ordinal));
        if (model is null)
        {
            throw new InvalidOperationException("document-ocr-model-inference-onnx-missing");
        }
        if (model.ByteLength is < 1 or > MaximumOnnxBytes)
        {
            throw new InvalidOperationException("document-ocr-model-inference-onnx-invalid");
        }

        var bytes = await ReadFullyAsync(lease, model.ByteLength, cancellationToken).ConfigureAwait(false);
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        options.AppendExecutionProvider_DML(deviceId);
        return new DirectMlDocumentOcrSession(role, new InferenceSession(bytes, options));
    }

    private static async ValueTask<byte[]> ReadFullyAsync(
        VerifiedLocalModelLease lease,
        long byteLength,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[checked((int)byteLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await lease.ReadAsync(OnnxFilename, offset, bytes.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                throw new InvalidOperationException("document-ocr-model-inference-onnx-read-failed");
            }

            offset = checked(offset + read);
        }

        return bytes;
    }
}

public sealed class DirectMlDocumentOcrSession(
    DocumentOcrModelRole role,
    InferenceSession session) : IDisposable
{
    public DocumentOcrModelRole Role { get; } = role;

    internal InferenceSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

    public IReadOnlyList<string> InputNames => Session.InputMetadata.Keys.ToArray();

    public IReadOnlyList<string> OutputNames => Session.OutputMetadata.Keys.ToArray();

    public void Dispose() => Session.Dispose();
}
