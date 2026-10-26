using System.Runtime.Versioning;
using System.ComponentModel;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
public sealed class WindowsSourceVerificationReaderSlot(string runtimeDirectory) : ISourceVerificationReaderSlot
{
    public const string FileName = "source-verification.lock";
    private readonly string _path = Path.Combine(Path.GetFullPath(runtimeDirectory), FileName);
    public IDisposable? TryAcquire()
    {
        try
        {
            var (parent, handle) = new HandleRelativeNativeFileSystem().OpenOrCreateStableFile(_path);
            return new Lease(parent, handle);
        }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
        {
            return null; // Busy is the only ordinary refusal. Invalid paths/permissions stay visible.
        }
    }

    private sealed class Lease(VerifiedNativeDirectory parent, SafeFileHandle handle) : IDisposable
    {
        private SafeFileHandle? _handle = handle;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _handle, null) is not { } owned) return;
            try { owned.Dispose(); }
            finally { parent.Dispose(); }
            // Preserve this file: deleting/recreating it would allow separate objects to grant ownership.
        }
    }
}
