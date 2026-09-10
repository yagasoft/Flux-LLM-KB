using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal sealed class NativeModelPathException : UnauthorizedAccessException
{
    internal NativeModelPathException() : base("model-path-unsafe")
    {
    }
}

internal sealed class NativeModelReceiptCollisionException : IOException
{
    internal NativeModelReceiptCollisionException() : base("model-receipt-already-exists")
    {
    }
}

internal sealed class NativeModelDirectoryChain : IDisposable
{
    private readonly List<VerifiedNativeDirectory> _directories;
    private bool _disposed;

    internal NativeModelDirectoryChain(IEnumerable<VerifiedNativeDirectory> directories)
    {
        _directories = directories.ToList();
        if (_directories.Count == 0) throw new ArgumentException("A model directory chain requires a directory.", nameof(directories));
    }

    internal VerifiedNativeDirectory Leaf => _disposed
        ? throw new ObjectDisposedException(nameof(NativeModelDirectoryChain))
        : _directories[^1];

    internal void Add(VerifiedNativeDirectory directory)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeModelDirectoryChain));
        _directories.Add(directory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _directories.Count - 1; index >= 0; index--) _directories[index].Dispose();
    }
}

internal sealed class NativeModelHeldReadFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly IDisposable _owner;
    private bool _disposed;

    internal NativeModelHeldReadFile(SafeFileHandle handle, long byteLength, IDisposable owner)
    {
        _handle = handle;
        ByteLength = byteLength;
        _owner = owner;
    }

    internal long ByteLength { get; }

    internal ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeModelHeldReadFile));
        if (offset < 0 || offset > ByteLength) throw new ArgumentOutOfRangeException(nameof(offset));
        return RandomAccess.ReadAsync(_handle, buffer[..(int)Math.Min(buffer.Length, ByteLength - offset)], offset, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
        _owner.Dispose();
    }
}

internal sealed partial class HandleRelativeNativeFileSystem
{
    internal NativeModelDirectoryChain OpenModelDirectoryChain(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath) ||
            absolutePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            absolutePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new NativeModelPathException();
        }

        var canonicalPath = Path.GetFullPath(absolutePath);
        var rootPath = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrWhiteSpace(rootPath) || !IsLocalFixedModelDriveType(NativeMethods.GetDriveType(rootPath)))
        {
            throw new NativeModelPathException();
        }

        var rootHandle = NativeMethods.OpenAbsoluteDirectory(rootPath);
        NativeModelDirectoryChain? chain = null;
        try
        {
            EnsureModelDirectory(rootHandle);
            var root = new VerifiedNativeDirectory(rootHandle, NativeMethods.GetIdentity(rootHandle), rootPath);
            rootHandle = null!;
            chain = new NativeModelDirectoryChain([root]);
            var relativePath = Path.GetRelativePath(rootPath, canonicalPath);
            if (!string.Equals(relativePath, ".", StringComparison.Ordinal))
            {
                foreach (var component in relativePath.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    chain.Add(OpenModelDirectory(chain.Leaf, component));
                }
            }

            return chain;
        }
        catch
        {
            chain?.Dispose();
            throw;
        }
        finally
        {
            rootHandle?.Dispose();
        }
    }

    internal NativeModelDirectoryChain OpenModelDirectoryChain(
        VerifiedNativeDirectory parent,
        IReadOnlyList<string> literalChildren)
    {
        EnsureModelParent(parent);
        var chain = new NativeModelDirectoryChain([OpenModelDirectory(parent, literalChildren.FirstOrDefault() ?? throw new NativeModelPathException())]);
        try
        {
            for (var index = 1; index < literalChildren.Count; index++)
            {
                chain.Add(OpenModelDirectory(chain.Leaf, literalChildren[index]));
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    internal VerifiedNativeDirectory OpenOrCreateModelDirectory(VerifiedNativeDirectory parent, string literalChild)
    {
        EnsureModelParent(parent);
        EnsureModelLiteralChild(literalChild);
        if (NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.DirectoryReadAccess,
                NativeMethods.ShareReadWrite,
                NativeMethods.FileOpen,
                NativeMethods.DirectoryOpenOptions,
                out var existing))
        {
            return ToModelDirectory(existing!, parent.CanonicalPath, literalChild);
        }

        try
        {
            var created = NativeMethods.OpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.DirectoryReadAccess,
                NativeMethods.ShareReadWrite,
                NativeMethods.FileCreate,
                NativeMethods.DirectoryOpenOptions);
            return ToModelDirectory(created, parent.CanonicalPath, literalChild);
        }
        catch (IOException)
        {
            if (!NativeMethods.TryOpenRelative(
                    parent.Handle,
                    literalChild,
                    NativeMethods.DirectoryReadAccess,
                    NativeMethods.ShareReadWrite,
                    NativeMethods.FileOpen,
                    NativeMethods.DirectoryOpenOptions,
                    out var raced))
            {
                throw;
            }

            return ToModelDirectory(raced!, parent.CanonicalPath, literalChild);
        }
    }

    internal NativeModelHeldReadFile? TryOpenModelReadFile(
        VerifiedNativeDirectory parent,
        string literalChild,
        IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureModelParent(parent);
        EnsureModelLiteralChild(literalChild);
        if (!NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.FileReadAccess,
                NativeMethods.ShareRead,
                NativeMethods.FileOpen,
                NativeMethods.ModelFileOpenOptions,
                out var handle))
        {
            return null;
        }

        try
        {
            EnsureModelFile(handle!);
            return new NativeModelHeldReadFile(handle!, RandomAccess.GetLength(handle!), owner);
        }
        catch
        {
            handle!.Dispose();
            throw;
        }
    }

    internal async ValueTask PublishImmutableModelReceiptAsync(
        VerifiedNativeDirectory parent,
        string destinationLiteralChild,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken,
        Func<string, ValueTask>? beforePublish = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureModelParent(parent);
        EnsureModelLiteralChild(destinationLiteralChild);
        var stagingLiteralChild = $"{destinationLiteralChild}.{Guid.NewGuid():N}.tmp";
        EnsureModelLiteralChild(stagingLiteralChild);
        if (ModelLiteralChildExists(parent, destinationLiteralChild))
        {
            throw new NativeModelReceiptCollisionException();
        }

        SafeFileHandle? staging = null;
        var published = false;
        try
        {
            staging = NativeMethods.OpenRelative(
                parent.Handle,
                stagingLiteralChild,
                NativeMethods.FileWriteAccess | NativeMethods.FileRenameAndReadAccess,
                NativeMethods.ShareNone,
                NativeMethods.FileCreate,
                NativeMethods.FileOpenOptions);
            EnsureModelFile(staging);
            await RandomAccess.WriteAsync(staging, content, fileOffset: 0, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            NativeMethods.Flush(staging);
            if (beforePublish is not null)
            {
                await beforePublish(stagingLiteralChild).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureModelFile(staging);
            try
            {
                NativeMethods.RenameRelative(staging, parent.Handle, destinationLiteralChild, replaceIfExists: false);
            }
            catch (IOException)
            {
                if (ModelLiteralChildExists(parent, destinationLiteralChild))
                {
                    throw new NativeModelReceiptCollisionException();
                }

                throw;
            }

            published = true;
        }
        finally
        {
            if (!published && staging is not null)
            {
                _ = NativeMethods.TryMarkForDeletion(staging, out _);
            }

            staging?.Dispose();
        }
    }

    private static VerifiedNativeDirectory OpenModelDirectory(VerifiedNativeDirectory parent, string literalChild)
    {
        EnsureModelParent(parent);
        EnsureModelLiteralChild(literalChild);
        var handle = NativeMethods.OpenRelative(
            parent.Handle,
            literalChild,
            NativeMethods.DirectoryReadAccess,
            NativeMethods.ShareReadWrite,
            NativeMethods.FileOpen,
            NativeMethods.DirectoryOpenOptions);
        return ToModelDirectory(handle, parent.CanonicalPath, literalChild);
    }

    private static VerifiedNativeDirectory ToModelDirectory(SafeFileHandle handle, string parentPath, string literalChild)
    {
        try
        {
            EnsureModelDirectory(handle);
            return new VerifiedNativeDirectory(handle, NativeMethods.GetIdentity(handle), Path.Combine(parentPath, literalChild));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void EnsureModelParent(VerifiedNativeDirectory parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.IsDisposed || parent.Handle.IsInvalid || parent.Handle.IsClosed)
        {
            throw new ObjectDisposedException(nameof(VerifiedNativeDirectory));
        }

        var information = NativeMethods.GetInformation(parent.Handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeDirectory) == 0 ||
            (information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0 ||
            NativeMethods.ToIdentity(information) != parent.Identity)
        {
            throw new NativeModelPathException();
        }
    }

    private static void EnsureModelDirectory(SafeFileHandle handle)
    {
        var information = NativeMethods.GetInformation(handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0 ||
            (information.FileAttributes & NativeMethods.FileAttributeDirectory) == 0)
        {
            throw new NativeModelPathException();
        }
    }

    private static void EnsureModelFile(SafeFileHandle handle)
    {
        var information = NativeMethods.GetInformation(handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0 ||
            (information.FileAttributes & NativeMethods.FileAttributeDirectory) != 0 ||
            (information.FileAttributes & NativeMethods.FileAttributeOfflineOrRecall) != 0)
        {
            throw new NativeModelPathException();
        }
    }

    private static void EnsureModelLiteralChild(string literalChild)
    {
        if (string.IsNullOrWhiteSpace(literalChild) ||
            literalChild is "." or ".." ||
            literalChild.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            literalChild.EndsWith(' ') || literalChild.EndsWith('.') ||
            literalChild.Any(static character => char.IsControl(character)))
        {
            throw new NativeModelPathException();
        }
    }

    private static bool ModelLiteralChildExists(VerifiedNativeDirectory parent, string literalChild)
    {
        if (!NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.FileAttributesAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.OpenAnyOptions,
                out var existing))
        {
            return false;
        }

        using (existing)
        {
            var information = NativeMethods.GetInformation(existing!);
            if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
            {
                throw new NativeModelPathException();
            }

            return true;
        }
    }

    internal static bool IsLocalFixedModelDriveTypeForTest(uint driveType) => IsLocalFixedModelDriveType(driveType);

    private static bool IsLocalFixedModelDriveType(uint driveType) => driveType == NativeMethods.DriveFixed;
}
