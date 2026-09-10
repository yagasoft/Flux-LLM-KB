using System.ComponentModel;
using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Models;

internal enum WindowsModelVerificationOperation
{
    OpenRoot,
    OpenArtifact,
    PersistReceipt
}

internal sealed record WindowsModelVerificationTestOptions(
    string? ReceiptFailureReason = null,
    Action<WindowsModelVerificationOperation>? Observe = null,
    Func<string, ValueTask>? BeforeReceiptPublish = null);

public sealed class WindowsModelVerificationFiles : IModelVerificationFiles
{
    public const string ProductionRoot = @"J:\Models";

    private readonly HandleRelativeNativeFileSystem _fileSystem;
    private readonly NativeModelDirectoryChain _root;
    private readonly WindowsModelVerificationTestOptions? _testOptions;
    private bool _disposed;

    private WindowsModelVerificationFiles(string root, WindowsModelVerificationTestOptions? testOptions = null)
    {
        _testOptions = testOptions;
        _fileSystem = new HandleRelativeNativeFileSystem();
        try
        {
            _root = _fileSystem.OpenModelDirectoryChain(root);
            _testOptions?.Observe?.Invoke(WindowsModelVerificationOperation.OpenRoot);
        }
        catch (NativeModelPathException exception)
        {
            throw new ModelVerificationFilesException(ModelStoreReasons.PathUnsafe, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelVerificationFilesException(ModelStoreReasons.StoreUnavailable, exception);
        }
    }

    public static WindowsModelVerificationFiles OpenProduction() => new(ProductionRoot);

    public static async Task<byte[]> ReadLocalManifestAsync(
        string localPath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (localPath.StartsWith(@"\\", StringComparison.Ordinal) || localPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(localPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid, exception);
        }

        if (!Path.IsPathFullyQualified(canonicalPath) ||
            canonicalPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            canonicalPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        var parentPath = Path.GetDirectoryName(canonicalPath);
        var filename = Path.GetFileName(canonicalPath);
        if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(filename))
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        var fileSystem = new HandleRelativeNativeFileSystem();
        NativeModelDirectoryChain? parent = null;
        NativeModelHeldReadFile? file = null;
        try
        {
            parent = fileSystem.OpenModelDirectoryChain(parentPath);
            file = fileSystem.TryOpenModelReadFile(parent.Leaf, filename, parent);
            if (file is null) throw new FileNotFoundException("The local model manifest does not exist.", canonicalPath);
            parent = null;
            if (file.ByteLength > maximumBytes)
            {
                throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
            }

            var content = new byte[checked((int)file.ByteLength)];
            var offset = 0;
            while (offset < content.Length)
            {
                var read = await file.ReadAsync(offset, content.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
                offset += read;
            }

            return content;
        }
        catch (NativeModelPathException exception)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid, exception);
        }
        finally
        {
            file?.Dispose();
            parent?.Dispose();
        }
    }

    internal static WindowsModelVerificationFiles OpenForTest(
        string root,
        WindowsModelVerificationTestOptions? testOptions = null) => new(root, testOptions);

    public ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(
        ModelArtifactSpecification specification,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _testOptions?.Observe?.Invoke(WindowsModelVerificationOperation.OpenArtifact);
        try
        {
            var directories = _fileSystem.OpenModelDirectoryChain(
                _root.Leaf,
                ["artifacts", "sha256", specification.Sha256]);
            try
            {
                var held = _fileSystem.TryOpenModelReadFile(directories.Leaf, specification.Filename, directories);
                if (held is null)
                {
                    directories.Dispose();
                    return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing));
                }

                return ValueTask.FromResult(ModelVerificationFileOpenResult.Found(new WindowsModelVerificationFile(held)));
            }
            catch
            {
                directories.Dispose();
                throw;
            }
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing));
        }
        catch (NativeModelPathException)
        {
            return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.PathUnsafe));
        }
        catch (IOException exception) when (TryGetWin32ErrorCode(exception) == 32)
        {
            return ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactInUse));
        }
        catch (IOException exception)
        {
            throw new ModelVerificationFilesException(ModelStoreReasons.StoreIoError, exception);
        }
    }

    public async ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(
        ModelVerificationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _testOptions?.Observe?.Invoke(WindowsModelVerificationOperation.PersistReceipt);
        if (_testOptions?.ReceiptFailureReason is { } receiptFailureReason)
        {
            return ModelReceiptPersistenceResult.Refused(receiptFailureReason);
        }

        try
        {
            using var inventory = _fileSystem.OpenOrCreateModelDirectory(_root.Leaf, "inventory");
            using var verifications = _fileSystem.OpenOrCreateModelDirectory(inventory, "verifications");
            var filename = $"{receipt.ReceiptId:D}.json";
            var json = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await _fileSystem.PublishImmutableModelReceiptAsync(
                    verifications,
                    filename,
                    json,
                    cancellationToken,
                    _testOptions?.BeforeReceiptPublish)
                .ConfigureAwait(false);
            return ModelReceiptPersistenceResult.Published($"inventory/verifications/{filename}");
        }
        catch (NativeModelReceiptCollisionException)
        {
            return ModelReceiptPersistenceResult.Refused(ModelStoreReasons.ReceiptFailed);
        }
        catch (NativeModelPathException)
        {
            return ModelReceiptPersistenceResult.Refused(ModelStoreReasons.PathUnsafe);
        }
        catch (IOException exception)
        {
            return ModelReceiptPersistenceResult.Refused(
                TryGetWin32ErrorCode(exception) == 5
                    ? ModelStoreReasons.StoreNotWritable
                    : ModelStoreReasons.ReceiptFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return ModelReceiptPersistenceResult.Refused(ModelStoreReasons.StoreNotWritable);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _root.Dispose();
    }

    private static int? TryGetWin32ErrorCode(IOException exception) =>
        exception.InnerException is Win32Exception inner ? inner.NativeErrorCode : null;

    private sealed class WindowsModelVerificationFile(NativeModelHeldReadFile file) : IModelVerificationFile
    {
        public long ByteLength => file.ByteLength;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
            file.ReadAsync(offset, buffer, cancellationToken);

        public void Dispose() => file.Dispose();
    }
}
