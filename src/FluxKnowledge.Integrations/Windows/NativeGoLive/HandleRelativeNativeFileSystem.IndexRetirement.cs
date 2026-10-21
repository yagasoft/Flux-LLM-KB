using System.Security.Cryptography;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal sealed partial class HandleRelativeNativeFileSystem
{
    /// <summary>Hash and optional unlink share one no-follow handle that denies writes/replacement.</summary>
    internal async ValueTask VerifyOrDeleteIndexFileAsync(VerifiedNativeDirectory parent, string name,
        string checksum, long? expectedLength, long maximumLength, bool delete, CancellationToken cancellationToken)
    {
        EnsureVerifiedParent(parent);
        EnsureLiteralChild(name);
        if (checksum.Length != 64 || checksum.Any(value => !char.IsAsciiHexDigitLower(value)) || maximumLength < 0 || expectedLength < 0)
            throw new InvalidDataException("index-retirement-file-proof-invalid");
        if (delete) await InvokeBeforeMutationAsync(NativeFileOperation.DeleteLiteralChild, name);
        if (!NativeMethods.TryOpenRelative(parent.Handle, name, NativeMethods.FileRenameAndReadAccess,
            NativeMethods.ShareRead, NativeMethods.FileOpen, NativeMethods.FileOpenOptions, out var handle)) return;
        using (handle)
        {
            EnsureSafeFile(handle!);
            var information = NativeMethods.GetInformation(handle!);
            var length = RandomAccess.GetLength(handle!);
            if (information.NumberOfLinks != 1 || length > maximumLength || expectedLength.HasValue && length != expectedLength.Value)
                throw new InvalidDataException("index-retirement-file-identity-or-length-changed");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = RandomAccess.Read(handle!, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (count == 0) throw new IOException("index-retirement-file-truncated");
                hash.AppendData(buffer.AsSpan(0, count)); offset += count;
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != checksum)
                throw new InvalidDataException("index-retirement-file-checksum-changed");
            cancellationToken.ThrowIfCancellationRequested();
            EnsureVerifiedParent(parent);
            if (delete && !NativeMethods.TryMarkForDeletion(handle!, out _)) throw new IOException("index-retirement-unlink-failed");
        }
    }
}
