namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal sealed partial class HandleRelativeNativeFileSystem
{
    internal sealed class IndexRetirementIntentLease(Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] content) : IDisposable
    {
        internal byte[] Content => content;
        internal void Complete()
        {
            if (!NativeMethods.TryMarkForDeletion(handle, out _)) throw new IOException("index-retirement-intent-unlink-failed");
        }
        public void Dispose() => handle.Dispose();
    }

    // This bounded metadata intent survives a process/SQL retry. Incomplete
    // writes remain a refusal; never delete them and silently recapture targets.
    internal async ValueTask<IndexRetirementIntentLease> OpenIndexRetirementIntentAsync(VerifiedNativeDirectory parent,
        string name, byte[] initialContent, CancellationToken cancellationToken)
    {
        EnsureVerifiedParent(parent);
        EnsureLiteralChild(name);
        if (initialContent.Length is < 1 or > 4096) throw new InvalidDataException("index-retirement-intent-invalid");
        var created = !NativeMethods.TryOpenRelative(parent.Handle, name, NativeMethods.FileRenameAndReadAccess,
            NativeMethods.ShareNone, NativeMethods.FileOpen, NativeMethods.FileOpenOptions, out var handle);
        if (created) handle = NativeMethods.OpenRelative(parent.Handle, name, NativeMethods.FileWriteAccess | NativeMethods.FileReadAccess,
            NativeMethods.ShareNone, NativeMethods.FileCreate, NativeMethods.FileOpenOptions);
        try
        {
            EnsureSafeFile(handle!);
            if (NativeMethods.GetInformation(handle!).NumberOfLinks != 1) throw new InvalidDataException("index-retirement-intent-invalid");
            if (created)
            {
                await RandomAccess.WriteAsync(handle!, initialContent, 0, cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(handle!);
            }
            var length = RandomAccess.GetLength(handle!);
            if (length is < 1 or > 4096) throw new InvalidDataException("index-retirement-intent-invalid");
            var content = new byte[(int)length];
            var offset = 0;
            while (offset < content.Length)
            {
                var count = await RandomAccess.ReadAsync(handle!, content.AsMemory(offset), offset, cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new InvalidDataException("index-retirement-intent-truncated");
                offset += count;
            }
            return new(handle!, content);
        }
        catch { handle?.Dispose(); throw; }
    }

    /// <summary>Metadata checks and optional unlink share one no-follow handle that denies writes/replacement.</summary>
    internal async ValueTask<NativeFileIdentity?> VerifyOrDeleteIndexFileAsync(VerifiedNativeDirectory parent, string name,
        string checksum, long? expectedLength, long maximumLength, bool delete, CancellationToken cancellationToken,
        NativeFileIdentity? expectedIdentity = null)
    {
        EnsureVerifiedParent(parent);
        EnsureLiteralChild(name);
        if (checksum.Length != 64 || checksum.Any(value => !char.IsAsciiHexDigitLower(value)) || maximumLength < 0 || expectedLength < 0)
            throw new InvalidDataException("index-retirement-file-proof-invalid");
        if (delete) await InvokeBeforeMutationAsync(NativeFileOperation.DeleteLiteralChild, name);
        if (!NativeMethods.TryOpenRelative(parent.Handle, name, NativeMethods.FileRenameAndReadAccess,
            NativeMethods.ShareRead, NativeMethods.FileOpen, NativeMethods.FileOpenOptions, out var handle)) return null;
        using (handle)
        {
            EnsureSafeFile(handle!);
            var information = NativeMethods.GetInformation(handle!);
            var identity = NativeMethods.GetIdentity(handle!);
            var length = RandomAccess.GetLength(handle!);
            if (information.NumberOfLinks != 1 || length > maximumLength || expectedLength.HasValue && length != expectedLength.Value ||
                delete && (expectedIdentity is null || identity != expectedIdentity.Value))
                throw new InvalidDataException("index-retirement-file-identity-or-length-changed");
            cancellationToken.ThrowIfCancellationRequested();
            EnsureVerifiedParent(parent);
            if (delete && !NativeMethods.TryMarkForDeletion(handle!, out _)) throw new IOException("index-retirement-unlink-failed");
            return identity;
        }
    }
}
