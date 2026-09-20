using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace PpStructureOnnxBenchmark;

internal sealed class ProbeVerificationFiles(IReadOnlyDictionary<string, string> paths) : IModelVerificationFiles
{
    private readonly HandleRelativeNativeFileSystem _fileSystem = new();
    private bool _disposed;

    public ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(ModelArtifactSpecification specification, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!paths.TryGetValue(specification.Filename, out var path) || !path.StartsWith(@"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\bundles\", StringComparison.OrdinalIgnoreCase)) return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing));
        try
        {
            var parent = _fileSystem.OpenModelDirectoryChain(Path.GetDirectoryName(path)!);
            var held = _fileSystem.TryOpenModelReadFile(parent.Leaf, Path.GetFileName(path), parent);
            if (held is null) { parent.Dispose(); return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing)); }
            return ValueTask.FromResult(ModelVerificationFileOpenResult.Found(new HeldFile(held)));
        }
        catch (NativeModelPathException) { return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.PathUnsafe)); }
        catch (FileNotFoundException) { return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing)); }
    }

    public async ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(ModelVerificationReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            using var root = _fileSystem.OpenModelDirectoryChain(@"J:\Models");
            using var inventory = _fileSystem.OpenOrCreateModelDirectory(root.Leaf, "inventory");
            using var verifications = _fileSystem.OpenOrCreateModelDirectory(inventory, "verifications");
            await _fileSystem.PublishImmutableModelReceiptAsync(verifications, receipt.ReceiptId.ToString("D") + ".json", JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken).ConfigureAwait(false);
            return ModelReceiptPersistenceResult.Published("inventory/verifications/" + receipt.ReceiptId.ToString("D") + ".json");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return ModelReceiptPersistenceResult.Refused(ModelStoreReasons.ReceiptFailed); }
    }

    public void Dispose() => _disposed = true;
    private sealed class HeldFile(NativeModelHeldReadFile file) : IModelVerificationFile { public long ByteLength => file.ByteLength; public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) => file.ReadAsync(offset, buffer, cancellationToken); public void Dispose() => file.Dispose(); }
}
