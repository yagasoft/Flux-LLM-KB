using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal static class SourceAccessTokenPermissions
{
    internal static bool CanRead(byte[] descriptor, WindowsIdentity identity)
    {
        if (!DuplicateToken(identity.AccessToken, 1, out var token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token)
        {
            var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200a0, All = 0x1f01ff };
            var privileges = new byte[4096];
            var length = (uint)privileges.Length;
            if (!AccessCheck(descriptor, token, SourceReadGrant.ReadMask, ref mapping, privileges, ref length, out var granted, out var allowed))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return allowed && (granted & SourceReadGrant.ReadMask) == SourceReadGrant.ReadMask;
        }
    }

    internal static bool HasApplicableDeny(byte[] descriptor, WindowsIdentity identity)
    {
        var sids = new HashSet<string>(StringComparer.Ordinal) { identity.User!.Value };
        AddGroups(identity.AccessToken, 2, sids, restricted: false);
        AddGroups(identity.AccessToken, 11, sids, restricted: true);
        var dacl = new RawSecurityDescriptor(descriptor, 0).DiscretionaryAcl;
        return dacl is not null && dacl.Cast<GenericAce>().OfType<CommonAce>().Any(ace =>
            ace.AceQualifier == AceQualifier.AccessDenied && (ace.AceFlags & AceFlags.InheritOnly) == 0 &&
            (SourceReadGrant.MapGenericFileRights(ace.AccessMask) & SourceReadGrant.ReadMask) != 0 &&
            // OWNER RIGHTS applies through ownership rather than token-group membership.
            // Refuse its effective read denials conservatively, including inherited ones.
            (ace.SecurityIdentifier.Value == "S-1-3-4" || sids.Contains(ace.SecurityIdentifier.Value)));
    }

    private static void AddGroups(SafeAccessTokenHandle token, int informationClass, HashSet<string> result, bool restricted)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var length);
        if (length < 4 || length > 65536) throw new InvalidDataException("SourceTokenNotSupported");
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var count = Marshal.ReadInt32(buffer);
            var offset = IntPtr.Size;
            var stride = Marshal.SizeOf<SidAndAttributes>();
            if (count < 0 || count > 4096 || count > 0 && (long)offset + (long)count * stride > length)
                throw new InvalidDataException("SourceTokenNotSupported");
            for (var index = 0; index < count; index++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(buffer, offset + index * stride));
                // Disabled groups do not apply, but deny-only groups absolutely do.
                if (restricted || (group.Attributes & (0x4u | 0x10u)) != 0)
                    result.Add(new SecurityIdentifier(group.Sid).Value);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct GenericMapping { public uint Read, Write, Execute, All; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle token, int level, out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle token, uint desired, ref GenericMapping mapping,
        byte[] privileges, ref uint length, out uint granted, [MarshalAs(UnmanagedType.Bool)] out bool allowed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, uint length, out uint needed);
}
