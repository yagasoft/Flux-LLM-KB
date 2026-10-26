using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal static class WindowsSourcePipeSecurity
{
    internal static SecurityIdentifier OwnerOf(SafePipeHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 1, out var owner, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception(checked((int)error));
        try { return new SecurityIdentifier(owner); }
        finally { _ = LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int type, uint information, out IntPtr owner,
        out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
