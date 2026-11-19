using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal sealed partial class HandleRelativeNativeFileSystem
{
    internal static async ValueTask PublishTranscriptAsync(SafeFileHandle parent, string stagingName, string targetName,
        ReadOnlyMemory<byte> content, Action validateSource, CancellationToken cancellationToken)
    {
        using var staging = NativeMethods.OpenRelative(parent, stagingName,
            NativeMethods.FileWriteAccess | NativeMethods.FileRenameAndReadAccess,
            NativeMethods.ShareNone, NativeMethods.FileCreate, NativeMethods.FileOpenOptions);
        var published = false;
        try
        {
            await RandomAccess.WriteAsync(staging, content, fileOffset: 0, cancellationToken).ConfigureAwait(false);
            NativeMethods.Flush(staging);
            validateSource();
            cancellationToken.ThrowIfCancellationRequested();
            NativeMethods.RenameRelative(staging, parent, targetName, replaceIfExists: false);
            published = true;
        }
        finally
        {
            if (!published)
                _ = NativeMethods.TryMarkForDeletion(staging, out _);
        }
    }
}
