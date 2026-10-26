using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
public static class SourceJournalPipeQualification
{
    // Invoke the actual published public clients under a token whose BA group is
    // deny-only. Reflection avoids loading a second application dependency graph
    // into the PowerShell compiler; this helper implements no pipe protocol.
    public static async Task<object> Journal(object client, string volume, object cursor)
    {
        using var token = RestrictedToken();
        return await WindowsIdentity.RunImpersonatedAsync(token, async () =>
        {
            var read = cursor != null;
            var method = client.GetType().GetMethod(read ? "ReadAsync" : "QueryAsync");
            var pending = method.Invoke(client, read
                ? new object[] { cursor, 65536, CancellationToken.None }
                : new object[] { volume, CancellationToken.None });
            var task = (Task)pending.GetType().GetMethod("AsTask").Invoke(pending, null);
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result").GetValue(task);
        }).ConfigureAwait(false);
    }

    public static object Repair(object client, object request)
    {
        using var token = RestrictedToken();
        return WindowsIdentity.RunImpersonated(token, () => client.GetType().GetMethod("TryRepair")
            .Invoke(client, new object[] { request, CancellationToken.None }));
    }

    private static SafeAccessTokenHandle RestrictedToken()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        var admin = new SecurityIdentifier("S-1-5-32-544");
        var bytes = new byte[admin.BinaryLength]; admin.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!CreateRestrictedToken(identity.AccessToken, 1, 1,
                new[] { new SidAndAttributes { Sid = pin.AddrOfPinnedObject() } }, 0, IntPtr.Zero, 0, IntPtr.Zero, out var token))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return token;
        }
        finally { pin.Free(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disableCount,
        SidAndAttributes[] disable, uint deleteCount, IntPtr delete, uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle result);
}
