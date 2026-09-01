using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal enum NativeFileOperation
{
    ReplaceFile,
    ReplaceFileAfterTemporaryValidation,
    ReplaceFileAfterDestinationValidation,
    ReplaceFileAfterBackupMove,
    ReplaceFileBeforeInstall,
    ReplaceFileBeforeBackupDelete,
    DeleteLiteralChild,
    MoveLiteralChild,
    CopyLiteralChild,
    CreateDirectory,
    SetDirectorySecurity
}

internal readonly record struct NativeFileIdentity(
    uint VolumeSerialNumber,
    uint FileIndexHigh,
    uint FileIndexLow)
{
    internal static NativeFileIdentity Unknown { get; } = default;
}

internal sealed record NativeFileMutation(bool Changed, string? Reason, NativeFileIdentity? Identity = null)
{
    internal static NativeFileMutation Completed(NativeFileIdentity? identity = null) => new(true, null, identity);
    internal static NativeFileMutation Refused(string reason) => new(false, reason);
}

internal sealed record NativeLiteralFile(byte[] Content, NativeFileIdentity Identity);
internal sealed record NativeLiteralChild(string Name, NativeFileIdentity Identity, bool IsDirectory);

internal sealed class VerifiedNativeDirectory : IDisposable
{
    private bool _disposed;

    internal VerifiedNativeDirectory(SafeFileHandle handle, NativeFileIdentity identity, string canonicalPath)
    {
        Handle = handle;
        Identity = identity;
        CanonicalPath = canonicalPath;
    }

    internal SafeFileHandle Handle { get; }
    internal string CanonicalPath { get; }
    internal bool IsDisposed => _disposed;
    internal NativeFileIdentity Identity { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Handle.Dispose();
    }
}

/// <summary>
/// Windows-native no-follow operations whose names are resolved relative to an already verified
/// directory handle. Mutating methods accept only one literal child and revalidate identities
/// immediately before their single-child mutation.
/// </summary>
internal sealed class HandleRelativeNativeFileSystem
{
    private readonly Func<NativeFileOperation, string, ValueTask>? _beforeMutation;

    internal HandleRelativeNativeFileSystem(
        Func<NativeFileOperation, string, ValueTask>? beforeMutation = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Handle-relative native go-live filesystem operations require Windows.");
        }

        _beforeMutation = beforeMutation;
    }

    internal VerifiedNativeDirectory OpenDirectory(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath) ||
            absolutePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            absolutePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only an absolute local directory can be opened.");
        }

        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new UnauthorizedAccessException("The directory has no local volume root.");
        }

        var current = NativeMethods.OpenAbsoluteDirectory(root);
        try
        {
            EnsureSafeDirectory(current);
            var relative = Path.GetRelativePath(root, canonicalPath);
            if (!string.Equals(relative, ".", StringComparison.Ordinal))
            {
                foreach (var component in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    EnsureLiteralChild(component);
                    var next = NativeMethods.OpenRelative(
                        current,
                        component,
                        NativeMethods.DirectoryReadAccess,
                        NativeMethods.ShareReadWrite,
                        NativeMethods.FileOpen,
                        NativeMethods.DirectoryOpenOptions);
                    current.Dispose();
                    current = next;
                    EnsureSafeDirectory(current);
                }
            }

            var identity = NativeMethods.GetIdentity(current);
            return new VerifiedNativeDirectory(current, identity, canonicalPath);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    internal VerifiedNativeDirectory OpenOrCreateDirectory(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath) ||
            absolutePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            absolutePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only an absolute local directory can be created.");
        }

        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new UnauthorizedAccessException("The directory has no local volume root.");
        }

        var current = NativeMethods.OpenAbsoluteDirectory(root);
        try
        {
            EnsureSafeDirectory(current);
            var relative = Path.GetRelativePath(root, canonicalPath);
            if (!string.Equals(relative, ".", StringComparison.Ordinal))
            {
                foreach (var component in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    EnsureLiteralChild(component);
                    SafeFileHandle next;
                    if (!NativeMethods.TryOpenRelative(
                            current,
                            component,
                            NativeMethods.DirectoryReadAccess,
                            NativeMethods.ShareReadWrite,
                            NativeMethods.FileOpen,
                            NativeMethods.DirectoryOpenOptions,
                            out var existing))
                    {
                        next = NativeMethods.OpenRelative(
                            current,
                            component,
                            NativeMethods.DirectoryReadAccess,
                            NativeMethods.ShareReadWrite,
                            NativeMethods.FileCreate,
                            NativeMethods.DirectoryOpenOptions);
                    }
                    else
                    {
                        next = existing!;
                    }

                    current.Dispose();
                    current = next;
                    EnsureSafeDirectory(current);
                }
            }

            var identity = NativeMethods.GetIdentity(current);
            return new VerifiedNativeDirectory(current, identity, canonicalPath);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    internal VerifiedNativeDirectory OpenDirectory(VerifiedNativeDirectory parent, string literalChild)
    {
        EnsureVerifiedParent(parent);
        EnsureLiteralChild(literalChild);
        var handle = NativeMethods.OpenRelative(
            parent.Handle,
            literalChild,
            NativeMethods.DirectoryReadAccess,
            NativeMethods.ShareReadWrite,
            NativeMethods.FileOpen,
            NativeMethods.DirectoryOpenOptions);
        try
        {
            EnsureSafeDirectory(handle);
            return new VerifiedNativeDirectory(
                handle,
                NativeMethods.GetIdentity(handle),
                Path.Combine(parent.CanonicalPath, literalChild));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal async ValueTask<NativeFileMutation> CreateDirectoryAsync(
        VerifiedNativeDirectory parent,
        string literalChild,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(parent);
        if (!IsLiteralChild(literalChild))
        {
            return NativeFileMutation.Refused("foreign-child-name");
        }

        if (NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.DirectoryReadAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.DirectoryOpenOptions,
                out var existing))
        {
            existing!.Dispose();
            return NativeFileMutation.Refused("destination-exists");
        }

        await InvokeBeforeMutationAsync(NativeFileOperation.CreateDirectory, literalChild).ConfigureAwait(false);
        EnsureVerifiedParent(parent);
        if (NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.DirectoryReadAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.DirectoryOpenOptions,
                out var raced))
        {
            raced!.Dispose();
            return NativeFileMutation.Refused("destination-exists");
        }

        using var created = NativeMethods.OpenRelative(
            parent.Handle,
            literalChild,
            NativeMethods.DirectoryReadAccess,
            NativeMethods.ShareReadWrite,
            NativeMethods.FileCreate,
            NativeMethods.DirectoryOpenOptions);
        EnsureSafeDirectory(created);
        return NativeFileMutation.Completed(NativeMethods.GetIdentity(created));
    }

    internal async ValueTask SetDirectorySecurityAsync(
        VerifiedNativeDirectory directory,
        DirectorySecurity security,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native directory ACL operations require Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(directory);
        await InvokeBeforeMutationAsync(NativeFileOperation.SetDirectorySecurity, directory.CanonicalPath)
            .ConfigureAwait(false);
        EnsureVerifiedParent(directory);
        NativeMethods.SetDirectorySecurity(directory.Handle, security.GetSecurityDescriptorBinaryForm());
    }

    internal async ValueTask<NativeFileMutation> ReplaceFileAsync(
        VerifiedNativeDirectory parent,
        string temporaryLiteralChild,
        string destinationLiteralChild,
        ReadOnlyMemory<byte> content,
        NativeFileIdentity? expectedDestinationIdentity,
        CancellationToken cancellationToken = default,
        bool allowMatchingTemporary = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(parent);
        if (!IsLiteralChild(temporaryLiteralChild) ||
            !IsLiteralChild(destinationLiteralChild) ||
            string.Equals(temporaryLiteralChild, destinationLiteralChild, StringComparison.OrdinalIgnoreCase))
        {
            return NativeFileMutation.Refused("foreign-child-name");
        }

        var existingTemporary = await ReadLiteralFileAsync(
            parent,
            temporaryLiteralChild,
            cancellationToken).ConfigureAwait(false);
        if (existingTemporary is not null &&
            (!allowMatchingTemporary || !existingTemporary.Content.AsSpan().SequenceEqual(content.Span)))
        {
            return NativeFileMutation.Refused("unknown-temporary-file");
        }

        var destinationValidation = ValidateDestination(parent, destinationLiteralChild, expectedDestinationIdentity);
        if (destinationValidation is not null) return destinationValidation;

        NativeFileIdentity temporaryIdentity;
        if (existingTemporary is null)
        {
            using var temporary = NativeMethods.OpenRelative(
                parent.Handle,
                temporaryLiteralChild,
                NativeMethods.FileWriteAccess,
                NativeMethods.ShareReadWrite,
                NativeMethods.FileCreate,
                NativeMethods.FileOpenOptions);
            EnsureSafeFile(temporary);
            temporaryIdentity = NativeMethods.GetIdentity(temporary);
            await using var stream = new FileStream(temporary, FileAccess.Write);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        else
        {
            temporaryIdentity = existingTemporary.Identity;
        }

        await InvokeBeforeMutationAsync(NativeFileOperation.ReplaceFile, destinationLiteralChild).ConfigureAwait(false);
        EnsureVerifiedParent(parent);

        using var finalTemporary = NativeMethods.OpenRelative(
            parent.Handle,
            temporaryLiteralChild,
            NativeMethods.FileRenameAndReadAccess,
            NativeMethods.ShareRead,
            NativeMethods.FileOpen,
            NativeMethods.FileOpenOptions);
        if (!TryValidateIdentity(finalTemporary, temporaryIdentity, out var temporaryReason))
        {
            return NativeFileMutation.Refused(temporaryReason!);
        }

        if (!await ContentMatchesAsync(finalTemporary, content, cancellationToken).ConfigureAwait(false))
        {
            return NativeFileMutation.Refused("temporary-payload-changed");
        }

        await InvokeBeforeMutationAsync(
            NativeFileOperation.ReplaceFileAfterTemporaryValidation,
            destinationLiteralChild).ConfigureAwait(false);

        destinationValidation = ValidateDestination(parent, destinationLiteralChild, expectedDestinationIdentity);
        if (destinationValidation is not null) return destinationValidation;

        await InvokeBeforeMutationAsync(
            NativeFileOperation.ReplaceFileAfterDestinationValidation,
            destinationLiteralChild).ConfigureAwait(false);

        SafeFileHandle? destinationGuard = null;
        string? guardedBackupLiteralChild = null;
        try
        {
            if (expectedDestinationIdentity is not null)
            {
                if (!NativeMethods.TryOpenRelative(
                        parent.Handle,
                        destinationLiteralChild,
                        NativeMethods.FileMutationAccess,
                        NativeMethods.ShareReadWrite,
                        NativeMethods.FileOpen,
                        NativeMethods.FileOpenOptions,
                        out destinationGuard))
                {
                    return NativeFileMutation.Refused("file-identity-changed");
                }

                if (!TryValidateIdentity(destinationGuard!, expectedDestinationIdentity.Value, out var destinationReason))
                {
                    return NativeFileMutation.Refused(destinationReason!);
                }

                guardedBackupLiteralChild = destinationLiteralChild + ".replace-backup.tmp";
                if (!IsLiteralChild(guardedBackupLiteralChild))
                {
                    return NativeFileMutation.Refused("foreign-child-name");
                }

                var backupValidation = ValidateDestination(parent, guardedBackupLiteralChild, expectedDestinationIdentity: null);
                if (backupValidation is not null)
                {
                    return NativeFileMutation.Refused("unknown-replacement-backup");
                }

                NativeMethods.RenameRelative(
                    destinationGuard!,
                    parent.Handle,
                    guardedBackupLiteralChild,
                    replaceIfExists: false);
                await InvokeBeforeMutationAsync(
                    NativeFileOperation.ReplaceFileAfterBackupMove,
                    destinationLiteralChild).ConfigureAwait(false);
            }
            else
            {
                destinationValidation = ValidateDestination(parent, destinationLiteralChild, expectedDestinationIdentity);
                if (destinationValidation is not null) return destinationValidation;
            }

            try
            {
                await InvokeBeforeMutationAsync(
                    NativeFileOperation.ReplaceFileBeforeInstall,
                    destinationLiteralChild).ConfigureAwait(false);
                NativeMethods.RenameRelative(
                    finalTemporary,
                    parent.Handle,
                    destinationLiteralChild,
                    replaceIfExists: false);
            }
            catch (IOException)
            {
                if (LiteralChildExists(parent, destinationLiteralChild))
                {
                    return NativeFileMutation.Refused("foreign-destination-occupied");
                }

                return NativeFileMutation.Refused("file-install-interrupted");
            }

            using var verified = NativeMethods.OpenRelative(
                parent.Handle,
                destinationLiteralChild,
                NativeMethods.FileReadAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.FileOpenOptions);
            if (!TryValidateIdentity(verified, temporaryIdentity, out _))
            {
                throw new InvalidDataException("file-replace-verification-failed");
            }

            if (destinationGuard is not null)
            {
                await InvokeBeforeMutationAsync(
                    NativeFileOperation.ReplaceFileBeforeBackupDelete,
                    destinationLiteralChild).ConfigureAwait(false);
                if (!NativeMethods.TryMarkForDeletion(destinationGuard, out _))
                {
                    throw new IOException("The verified prior destination could not be deleted.");
                }
            }
        }
        finally
        {
            destinationGuard?.Dispose();
        }

        return NativeFileMutation.Completed(temporaryIdentity);
    }

    internal IReadOnlyList<string> EnumerateLiteralChildren(VerifiedNativeDirectory parent)
    {
        EnsureVerifiedParent(parent);
        return NativeMethods.EnumerateDirectoryNames(parent.Handle);
    }

    internal NativeLiteralChild InspectLiteralChild(VerifiedNativeDirectory parent, string literalChild)
    {
        EnsureVerifiedParent(parent);
        EnsureLiteralChild(literalChild);
        using var handle = NativeMethods.OpenRelative(
            parent.Handle,
            literalChild,
            NativeMethods.FileAttributesAccess,
            NativeMethods.ShareAll,
            NativeMethods.FileOpen,
            NativeMethods.OpenAnyOptions);
        var information = NativeMethods.GetInformation(handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
            throw new UnauthorizedAccessException("Reparse points are not accepted below a verified native directory.");
        return new NativeLiteralChild(
            literalChild,
            NativeMethods.ToIdentity(information),
            (information.FileAttributes & NativeMethods.FileAttributeDirectory) != 0);
    }

    internal ValueTask<NativeFileMutation> DeleteTreeContentsAsync(
        VerifiedNativeDirectory root,
        CancellationToken cancellationToken = default)
    {
        return DeleteTreeContentsCoreAsync(root, cancellationToken, [0]);
    }

    private async ValueTask<NativeFileMutation> DeleteTreeContentsCoreAsync(
        VerifiedNativeDirectory parent,
        CancellationToken cancellationToken,
        int[] visited)
    {
        foreach (var name in EnumerateLiteralChildren(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited[0] > 25_000) return NativeFileMutation.Refused("native-tree-entry-limit-exceeded");
            var child = InspectLiteralChild(parent, name);
            if (child.IsDirectory)
            {
                using var directory = OpenDirectory(parent, name);
                if (directory.Identity != child.Identity)
                    return NativeFileMutation.Refused("file-identity-changed");
                var nested = await DeleteTreeContentsCoreAsync(directory, cancellationToken, visited)
                    .ConfigureAwait(false);
                if (!nested.Changed) return nested;
            }

            var deleted = await DeleteLiteralChildAsync(parent, name, child.Identity, cancellationToken)
                .ConfigureAwait(false);
            if (!deleted.Changed) return deleted;
        }

        return NativeFileMutation.Completed(parent.Identity);
    }

    internal async ValueTask<NativeFileMutation> DeleteLiteralChildAsync(
        VerifiedNativeDirectory parent,
        string literalChild,
        NativeFileIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(parent);
        if (!IsLiteralChild(literalChild)) return NativeFileMutation.Refused("foreign-child-name");

        var initial = TryOpenMutationTarget(parent, literalChild, expectedIdentity, NativeMethods.ShareAll);
        if (initial.Result is not null) return initial.Result;
        initial.Handle!.Dispose();

        await InvokeBeforeMutationAsync(NativeFileOperation.DeleteLiteralChild, literalChild).ConfigureAwait(false);
        EnsureVerifiedParent(parent);

        var final = TryOpenMutationTarget(parent, literalChild, expectedIdentity, NativeMethods.ShareReadWrite);
        if (final.Result is not null) return final.Result;
        using var target = final.Handle!;

        if (!NativeMethods.TryMarkForDeletion(target, out var errorCode))
        {
            return errorCode == NativeMethods.ErrorDirectoryNotEmpty
                ? NativeFileMutation.Refused("literal-child-not-empty")
                : NativeFileMutation.Refused("literal-child-delete-failed");
        }

        return NativeFileMutation.Completed(expectedIdentity);
    }

    internal async ValueTask<NativeFileMutation> DeleteLiteralChildWhileGuardingFileAsync(
        VerifiedNativeDirectory parent,
        string guardedLiteralChild,
        NativeFileIdentity expectedGuardedIdentity,
        ReadOnlyMemory<byte> expectedGuardedContent,
        string deletedLiteralChild,
        NativeFileIdentity expectedDeletedIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(parent);
        if (!IsLiteralChild(guardedLiteralChild) || !IsLiteralChild(deletedLiteralChild))
        {
            return NativeFileMutation.Refused("foreign-child-name");
        }

        SafeFileHandle? guarded = null;
        try
        {
            if (!NativeMethods.TryOpenRelative(
                    parent.Handle,
                    guardedLiteralChild,
                    NativeMethods.FileReadAccess,
                    NativeMethods.ShareRead,
                    NativeMethods.FileOpen,
                    NativeMethods.FileOpenOptions,
                    out guarded))
            {
                return NativeFileMutation.Refused("guarded-file-changed");
            }
        }
        catch (IOException)
        {
            return NativeFileMutation.Refused("guarded-file-changed");
        }

        using (guarded)
        {
            if (!TryValidateIdentity(guarded!, expectedGuardedIdentity, out _) ||
                !await ContentMatchesAsync(guarded!, expectedGuardedContent, cancellationToken).ConfigureAwait(false))
            {
                return NativeFileMutation.Refused("guarded-file-changed");
            }

            return await DeleteLiteralChildAsync(
                parent,
                deletedLiteralChild,
                expectedDeletedIdentity,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<NativeFileMutation> MoveLiteralChildAsync(
        VerifiedNativeDirectory sourceParent,
        string sourceLiteralChild,
        NativeFileIdentity expectedSourceIdentity,
        VerifiedNativeDirectory destinationParent,
        string destinationLiteralChild,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(sourceParent);
        EnsureVerifiedParent(destinationParent);
        if (!IsLiteralChild(sourceLiteralChild) || !IsLiteralChild(destinationLiteralChild))
        {
            return NativeFileMutation.Refused("foreign-child-name");
        }

        var initial = TryOpenMutationTarget(sourceParent, sourceLiteralChild, expectedSourceIdentity, NativeMethods.ShareAll);
        if (initial.Result is not null) return initial.Result;
        initial.Handle!.Dispose();

        if (NativeMethods.TryOpenRelative(
                destinationParent.Handle,
                destinationLiteralChild,
                NativeMethods.FileReadAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.OpenAnyOptions,
                out var existingDestination))
        {
            existingDestination!.Dispose();
            return NativeFileMutation.Refused("destination-exists");
        }

        await InvokeBeforeMutationAsync(NativeFileOperation.MoveLiteralChild, sourceLiteralChild).ConfigureAwait(false);
        EnsureVerifiedParent(sourceParent);
        EnsureVerifiedParent(destinationParent);

        var final = TryOpenMutationTarget(sourceParent, sourceLiteralChild, expectedSourceIdentity, NativeMethods.ShareReadWrite);
        if (final.Result is not null) return final.Result;
        using var source = final.Handle!;

        if (NativeMethods.TryOpenRelative(
                destinationParent.Handle,
                destinationLiteralChild,
                NativeMethods.FileReadAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.OpenAnyOptions,
                out existingDestination))
        {
            existingDestination!.Dispose();
            return NativeFileMutation.Refused("destination-exists");
        }

        NativeMethods.RenameRelative(source, destinationParent.Handle, destinationLiteralChild, replaceIfExists: false);
        return NativeFileMutation.Completed(expectedSourceIdentity);
    }

    internal async ValueTask<NativeFileMutation> CopyLiteralChildAsync(
        VerifiedNativeDirectory sourceParent,
        string sourceLiteralChild,
        NativeFileIdentity expectedSourceIdentity,
        VerifiedNativeDirectory destinationParent,
        string destinationLiteralChild,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(sourceParent);
        EnsureVerifiedParent(destinationParent);
        if (!IsLiteralChild(sourceLiteralChild) || !IsLiteralChild(destinationLiteralChild))
            return NativeFileMutation.Refused("foreign-child-name");
        var sourceDescription = InspectLiteralChild(sourceParent, sourceLiteralChild);
        if (sourceDescription.IsDirectory || sourceDescription.Identity != expectedSourceIdentity)
            return NativeFileMutation.Refused("file-identity-changed");
        if (ValidateDestination(destinationParent, destinationLiteralChild, expectedDestinationIdentity: null) is { } occupied)
            return occupied;

        await InvokeBeforeMutationAsync(NativeFileOperation.CopyLiteralChild, sourceLiteralChild).ConfigureAwait(false);
        EnsureVerifiedParent(sourceParent);
        EnsureVerifiedParent(destinationParent);

        using var source = NativeMethods.OpenRelative(
            sourceParent.Handle,
            sourceLiteralChild,
            NativeMethods.FileReadAccess,
            NativeMethods.ShareRead,
            NativeMethods.FileOpen,
            NativeMethods.FileOpenOptions);
        if (!TryValidateIdentity(source, expectedSourceIdentity, out var reason))
            return NativeFileMutation.Refused(reason!);
        if (ValidateDestination(destinationParent, destinationLiteralChild, expectedDestinationIdentity: null) is { } lateOccupant)
            return lateOccupant;

        using var destination = NativeMethods.OpenRelative(
            destinationParent.Handle,
            destinationLiteralChild,
            NativeMethods.FileWriteAccess | NativeMethods.FileAttributesAccess,
            NativeMethods.ShareNone,
            NativeMethods.FileCreate,
            NativeMethods.FileOpenOptions);
        EnsureSafeFile(destination);
        var destinationIdentity = NativeMethods.GetIdentity(destination);
        try
        {
            await using var sourceStream = new FileStream(source, FileAccess.Read);
            await using var destinationStream = new FileStream(destination, FileAccess.Write);
            await sourceStream.CopyToAsync(destinationStream, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            destinationStream.Flush(flushToDisk: true);
        }
        catch
        {
            NativeMethods.TryMarkForDeletion(destination, out _);
            throw;
        }

        return NativeFileMutation.Completed(destinationIdentity);
    }

    internal async ValueTask<NativeLiteralFile?> ReadLiteralFileAsync(
        VerifiedNativeDirectory parent,
        string literalChild,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVerifiedParent(parent);
        EnsureLiteralChild(literalChild);
        if (!NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.FileReadAccess,
                NativeMethods.ShareAll,
                NativeMethods.FileOpen,
                NativeMethods.FileOpenOptions,
                out var handle))
        {
            return null;
        }

        using (handle)
        {
            EnsureSafeFile(handle!);
            var identity = NativeMethods.GetIdentity(handle!);
            await using var stream = new FileStream(handle!, FileAccess.Read);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            return new NativeLiteralFile(memory.ToArray(), identity);
        }
    }

    internal (VerifiedNativeDirectory Parent, SafeFileHandle Handle) OpenOrCreateStableFile(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var canonicalPath = Path.GetFullPath(absolutePath);
        var fileName = Path.GetFileName(canonicalPath);
        EnsureLiteralChild(fileName);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new UnauthorizedAccessException("The stable file has no parent directory.");
        var parent = OpenDirectory(parentPath);
        try
        {
            var handle = NativeMethods.OpenRelative(
                parent.Handle,
                fileName,
                NativeMethods.LockFileAccess,
                NativeMethods.ShareNone,
                NativeMethods.FileOpenIf,
                NativeMethods.FileOpenOptions);
            EnsureSafeFile(handle);
            return (parent, handle);
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    private static NativeFileMutation? ValidateDestination(
        VerifiedNativeDirectory parent,
        string destinationLiteralChild,
        NativeFileIdentity? expectedDestinationIdentity)
    {
        var exists = NativeMethods.TryOpenRelative(
            parent.Handle,
            destinationLiteralChild,
            NativeMethods.FileReadAccess,
            NativeMethods.ShareAll,
            NativeMethods.FileOpen,
            NativeMethods.FileOpenOptions,
            out var destination);
        if (expectedDestinationIdentity is null)
        {
            destination?.Dispose();
            return exists ? NativeFileMutation.Refused("destination-exists") : null;
        }

        if (!exists) return NativeFileMutation.Refused("file-identity-changed");
        using (destination)
        {
            return TryValidateIdentity(destination!, expectedDestinationIdentity.Value, out var reason)
                ? null
                : NativeFileMutation.Refused(reason!);
        }
    }

    private static bool LiteralChildExists(VerifiedNativeDirectory parent, string literalChild)
    {
        try
        {
            if (!NativeMethods.TryOpenRelative(
                    parent.Handle,
                    literalChild,
                    NativeMethods.FileAttributesAccess,
                    NativeMethods.ShareAll,
                    NativeMethods.FileOpen,
                    NativeMethods.OpenAnyOptions,
                    out var occupant))
            {
                return false;
            }

            occupant!.Dispose();
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static (SafeFileHandle? Handle, NativeFileMutation? Result) TryOpenMutationTarget(
        VerifiedNativeDirectory parent,
        string literalChild,
        NativeFileIdentity expectedIdentity,
        uint shareMode)
    {
        if (!NativeMethods.TryOpenRelative(
                parent.Handle,
                literalChild,
                NativeMethods.FileMutationAccess,
                shareMode,
                NativeMethods.FileOpen,
                NativeMethods.OpenAnyOptions,
                out var target))
        {
            return (null, NativeFileMutation.Refused("file-identity-changed"));
        }

        if (!TryValidateIdentity(target!, expectedIdentity, out var reason))
        {
            target!.Dispose();
            return (null, NativeFileMutation.Refused(reason!));
        }

        return (target, null);
    }

    private static bool TryValidateIdentity(
        SafeFileHandle handle,
        NativeFileIdentity expectedIdentity,
        out string? reason)
    {
        var information = NativeMethods.GetInformation(handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
        {
            reason = "reparse-point-not-allowed";
            return false;
        }

        if (NativeMethods.ToIdentity(information) != expectedIdentity)
        {
            reason = "file-identity-changed";
            return false;
        }

        reason = null;
        return true;
    }

    private static async ValueTask<bool> ContentMatchesAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        if (RandomAccess.GetLength(handle) != expected.Length) return false;
        var buffer = new byte[Math.Min(expected.Length, 8192)];
        var offset = 0;
        while (offset < expected.Length)
        {
            var count = Math.Min(buffer.Length, expected.Length - offset);
            var read = await RandomAccess.ReadAsync(
                handle,
                buffer.AsMemory(0, count),
                offset,
                cancellationToken).ConfigureAwait(false);
            if (read != count || !buffer.AsSpan(0, read).SequenceEqual(expected.Span.Slice(offset, read)))
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static void EnsureVerifiedParent(VerifiedNativeDirectory parent)
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
            throw new UnauthorizedAccessException("The verified parent directory identity changed.");
        }
    }

    private static void EnsureSafeDirectory(SafeFileHandle handle)
    {
        var information = NativeMethods.GetInformation(handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("reparse-point-not-allowed");
        }

        if ((information.FileAttributes & NativeMethods.FileAttributeDirectory) == 0)
        {
            throw new UnauthorizedAccessException("The expected directory is not a directory.");
        }
    }

    private static void EnsureSafeFile(SafeFileHandle handle)
    {
        var information = NativeMethods.GetInformation(handle);
        if ((information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("reparse-point-not-allowed");
        }

        if ((information.FileAttributes & NativeMethods.FileAttributeDirectory) != 0)
        {
            throw new UnauthorizedAccessException("The expected file is a directory.");
        }
    }

    private ValueTask InvokeBeforeMutationAsync(NativeFileOperation operation, string literalChild) =>
        _beforeMutation?.Invoke(operation, literalChild) ?? ValueTask.CompletedTask;

    private static void EnsureLiteralChild(string literalChild)
    {
        if (!IsLiteralChild(literalChild))
        {
            throw new UnauthorizedAccessException("foreign-child-name");
        }
    }

    private static bool IsLiteralChild(string? literalChild) =>
        !string.IsNullOrWhiteSpace(literalChild) &&
        literalChild is not "." and not ".." &&
        !literalChild.Contains(Path.DirectorySeparatorChar) &&
        !literalChild.Contains(Path.AltDirectorySeparatorChar) &&
        !literalChild.Contains(':') &&
        !literalChild.Contains('*') &&
        !literalChild.Contains('?') &&
        !literalChild.EndsWith(' ') &&
        !literalChild.EndsWith('.');

    private static class NativeMethods
    {
        internal const uint FileAttributeDirectory = 0x00000010;
        internal const uint FileAttributeReparsePoint = 0x00000400;
        internal const int ErrorDirectoryNotEmpty = 145;

        private const uint Delete = 0x00010000;
        private const uint WriteDac = 0x00040000;
        private const uint Synchronize = 0x00100000;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileReadData = 0x00000001;
        private const uint FileWriteData = 0x00000002;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileWriteAttributes = 0x00000100;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileDirectoryFile = 0x00000001;
        private const uint FileSynchronousIoNonalert = 0x00000020;
        private const uint FileNonDirectoryFile = 0x00000040;
        private const uint FileOpenReparsePoint = 0x00200000;
        private const uint ObjCaseInsensitive = 0x00000040;
        private const int FileDispositionInfo = 4;
        private const int NativeFileRenameInformation = 10;
        private const int NativeFileNamesInformation = 12;
        private const int StatusNoMoreFiles = unchecked((int)0x80000006);
        private const int StatusBufferOverflow = unchecked((int)0x80000005);
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        internal const uint FileOpen = 1;
        internal const uint FileCreate = 2;
        internal const uint FileOpenIf = 3;
        internal const uint ShareNone = 0;
        internal const uint ShareRead = FileShareRead;
        internal const uint ShareReadWrite = FileShareRead | FileShareWrite;
        internal const uint ShareAll = FileShareRead | FileShareWrite | FileShareDelete;
        internal const uint DirectoryReadAccess = FileListDirectory | FileReadAttributes | Synchronize;
        internal const uint FileReadAccess = FileReadData | FileReadAttributes | Synchronize;
        internal const uint FileAttributesAccess = FileReadAttributes | Synchronize;
        internal const uint FileWriteAccess = FileWriteData | FileReadAttributes | FileWriteAttributes | Delete | Synchronize;
        internal const uint FileRenameAndReadAccess = Delete | FileReadData | FileReadAttributes | Synchronize;
        internal const uint FileMutationAccess = Delete | FileReadAttributes | Synchronize;
        internal const uint LockFileAccess = FileReadData | FileWriteData | FileReadAttributes | Synchronize;
        internal const uint DirectoryOpenOptions = FileDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint;
        internal const uint FileOpenOptions = FileNonDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint;
        internal const uint OpenAnyOptions = FileSynchronousIoNonalert | FileOpenReparsePoint;

#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKernelObjectSecurity(
            SafeFileHandle handle,
            uint securityInformation,
            byte[] securityDescriptor);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryDirectoryFile(
            SafeFileHandle fileHandle,
            IntPtr eventHandle,
            IntPtr apcRoutine,
            IntPtr apcContext,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass,
            [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
            IntPtr fileName,
            [MarshalAs(UnmanagedType.U1)] bool restartScan);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);
#pragma warning restore SYSLIB1054

        internal static SafeFileHandle OpenAbsoluteDirectory(string path)
        {
            var handle = CreateFile(
                path,
                DirectoryReadAccess,
                ShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid) return handle;

            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("The native directory could not be opened.", new Win32Exception(error));
        }

        internal static SafeFileHandle OpenRelative(
            SafeFileHandle parent,
            string literalChild,
            uint desiredAccess,
            uint shareAccess,
            uint disposition,
            uint options)
        {
            if (TryOpenRelative(parent, literalChild, desiredAccess, shareAccess, disposition, options, out var handle))
            {
                return handle!;
            }

            throw new FileNotFoundException("The literal native child could not be opened.", literalChild);
        }

        internal static bool TryOpenRelative(
            SafeFileHandle parent,
            string literalChild,
            uint desiredAccess,
            uint shareAccess,
            uint disposition,
            uint options,
            out SafeFileHandle? handle)
        {
            var nameBuffer = Marshal.StringToHGlobalUni(literalChild);
            var unicodeStringPointer = IntPtr.Zero;
            try
            {
                var unicodeString = new UnicodeString
                {
                    Length = checked((ushort)(literalChild.Length * sizeof(char))),
                    MaximumLength = checked((ushort)((literalChild.Length + 1) * sizeof(char))),
                    Buffer = nameBuffer
                };
                unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodeStringPointer,
                    Attributes = ObjCaseInsensitive
                };
                var status = NtCreateFile(
                    out var rawHandle,
                    desiredAccess,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    0,
                    shareAccess,
                    disposition,
                    options,
                    IntPtr.Zero,
                    0);
                GC.KeepAlive(parent);
                if (status >= 0)
                {
                    handle = new SafeFileHandle(rawHandle, ownsHandle: true);
                    return true;
                }

                var error = checked((int)RtlNtStatusToDosError(status));
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                {
                    handle = null;
                    return false;
                }

                throw new IOException("The handle-relative native child operation failed.", new Win32Exception(error));
            }
            finally
            {
                if (unicodeStringPointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeStringPointer);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        internal static ByHandleFileInformation GetInformation(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException(
                    "The native file identity could not be read.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return information;
        }

        internal static NativeFileIdentity GetIdentity(SafeFileHandle handle) => ToIdentity(GetInformation(handle));

        internal static void SetDirectorySecurity(SafeFileHandle handle, byte[] securityDescriptor)
        {
            const uint daclSecurityInformation = 0x00000004;
            const uint protectedDaclSecurityInformation = 0x80000000;
            if (!SetKernelObjectSecurity(
                    handle,
                    daclSecurityInformation | protectedDaclSecurityInformation,
                    securityDescriptor))
            {
                throw new IOException(
                    "The verified native directory ACL could not be applied.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }

        internal static NativeFileIdentity ToIdentity(ByHandleFileInformation information) =>
            new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);

        internal static bool TryMarkForDeletion(SafeFileHandle handle, out int errorCode)
        {
            var buffer = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(buffer, 1);
                if (SetFileInformationByHandle(handle, FileDispositionInfo, buffer, 1))
                {
                    errorCode = 0;
                    return true;
                }

                errorCode = Marshal.GetLastPInvokeError();
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static void RenameRelative(
            SafeFileHandle source,
            SafeFileHandle destinationParent,
            string destinationLiteralChild,
            bool replaceIfExists)
        {
            var rootOffset = IntPtr.Size == 8 ? 8 : 4;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(uint);
            var name = destinationLiteralChild.ToCharArray();
            var bufferSize = checked(nameOffset + name.Length * sizeof(char));
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                for (var index = 0; index < bufferSize; index++) Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteByte(buffer, 0, replaceIfExists ? (byte)1 : (byte)0);
                Marshal.WriteIntPtr(buffer, rootOffset, destinationParent.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, checked(name.Length * sizeof(char)));
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                var status = NtSetInformationFile(
                    source,
                    out _,
                    buffer,
                    checked((uint)bufferSize),
                    NativeFileRenameInformation);
                if (status < 0)
                {
                    throw new IOException(
                        "The handle-relative literal child could not be moved.",
                        new Win32Exception(checked((int)RtlNtStatusToDosError(status))));
                }

                GC.KeepAlive(destinationParent);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static IReadOnlyList<string> EnumerateDirectoryNames(SafeFileHandle directory)
        {
            const int bufferSize = 64 * 1024;
            var names = new List<string>();
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var restartScan = true;
                while (true)
                {
                    var status = NtQueryDirectoryFile(
                        directory,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out var ioStatus,
                        buffer,
                        bufferSize,
                        NativeFileNamesInformation,
                        returnSingleEntry: false,
                        IntPtr.Zero,
                        restartScan);
                    GC.KeepAlive(directory);
                    restartScan = false;
                    if (status == StatusNoMoreFiles) break;
                    if (status < 0 && status != StatusBufferOverflow)
                    {
                        throw new IOException(
                            "The verified native directory could not be inspected.",
                            new Win32Exception(checked((int)RtlNtStatusToDosError(status))));
                    }

                    var bytesReturned = checked((int)ioStatus.Information.ToInt64());
                    if (bytesReturned <= 0) break;
                    var offset = 0;
                    while (offset < bytesReturned)
                    {
                        var entry = IntPtr.Add(buffer, offset);
                        var nextOffset = Marshal.ReadInt32(entry, 0);
                        var nameLength = Marshal.ReadInt32(entry, 8);
                        if (nameLength < 0 || (nameLength & 1) != 0 || offset + 12 + nameLength > bytesReturned)
                        {
                            throw new InvalidDataException("native-directory-entry-invalid");
                        }

                        var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 12), nameLength / sizeof(char));
                        if (name is not null && name is not "." and not "..") names.Add(name);
                        if (nextOffset == 0) break;
                        if (nextOffset < 12 || offset + nextOffset > bytesReturned)
                        {
                            throw new InvalidDataException("native-directory-entry-invalid");
                        }

                        offset += nextOffset;
                    }
                }

                return names;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
