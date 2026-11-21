using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal static class SourceFileInheritance
{
    private const ControlFlags LegacyFlags = ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent;
    private const ControlFlags ProtectedFlag = ControlFlags.DiscretionaryAclProtected;

    internal static bool HasLegacyDisabledInheritance(SafeFileHandle handle, byte[] rawBytes)
    {
        var raw = new RawSecurityDescriptor(rawBytes, 0);
        if (raw.ControlFlags != LegacyFlags || raw.Owner is null || raw.Group is null ||
            raw.DiscretionaryAcl is not { Count: > 0 } acl || acl.Cast<GenericAce>().Any(ace => ace.IsInherited))
            return false;

        // Win32 reports the Windows Security inheritance state, while the
        // raw descriptor remains authoritative for the compare and write.
        const uint fileObject = 1;
        const uint ownerGroupDacl = 7;
        var status = GetSecurityInfo(handle, fileObject, ownerGroupDacl, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, out var descriptor);
        try
        {
            if (status != 0 || descriptor == IntPtr.Zero) return false;
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > 1024 * 1024) return false;
            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var view = new RawSecurityDescriptor(bytes, 0);
            if (view.ControlFlags != (LegacyFlags | ProtectedFlag)) return false;
            view.SetFlags(LegacyFlags);
            return SourceReadGrant.Bytes(view).AsSpan().SequenceEqual(rawBytes) &&
                SourceReadGrant.Read(handle).AsSpan().SequenceEqual(rawBytes);
        }
        finally
        {
            if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor);
        }
    }

    internal static byte[] Propose(byte[] fileDescriptor, byte[] parentDescriptor, bool legacyDisabledInheritance = false)
    {
        var file = new RawSecurityDescriptor(fileDescriptor, 0);
        if (!file.ControlFlags.HasFlag(ProtectedFlag) && !(legacyDisabledInheritance && file.ControlFlags == LegacyFlags))
            throw new InvalidDataException("SourceAclNotProtected");

        var fileAcl = RequireSupportedDacl(file, isDirectory: false);
        _ = RequireSupportedDacl(new RawSecurityDescriptor(parentDescriptor, 0), isDirectory: true);
        var explicitAces = fileAcl.Cast<GenericAce>().Where(ace => !ace.IsInherited).ToArray();
        var creatorAcl = new RawAcl(fileAcl.Revision, explicitAces.Length);
        foreach (var ace in explicitAces)
            creatorAcl.InsertAce(creatorAcl.Count, ace.Copy());

        var flags = (file.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited) &
            ~(ControlFlags.DiscretionaryAclProtected | ControlFlags.DiscretionaryAclAutoInheritRequired);
        var creator = new RawSecurityDescriptor(flags, file.Owner, file.Group, null, creatorAcl);
        var projected = Inherit(parentDescriptor, SourceReadGrant.Bytes(creator));
        var projectedAcl = RequireSupportedDacl(projected, isDirectory: false);

        // Windows computes which parent ACEs apply to the file, including substitutions
        // for CREATOR OWNER and CREATOR GROUP. Verify that no explicit ACE was rewritten.
        if (projected.Owner != file.Owner || projected.Group != file.Group ||
            !projectedAcl.Cast<GenericAce>().Where(ace => !ace.IsInherited).SequenceEqual(explicitAces))
            throw new InvalidDataException("SourceAclNotSupported");

        file.DiscretionaryAcl = projectedAcl;
        file.SetFlags(flags);
        return SourceReadGrant.Bytes(file);
    }

    private static RawAcl RequireSupportedDacl(RawSecurityDescriptor descriptor, bool isDirectory)
    {
        var acl = descriptor.DiscretionaryAcl;
        if (descriptor.Owner is null || descriptor.Group is null || acl is null ||
            !new CommonSecurityDescriptor(isDirectory, false, descriptor).IsDiscretionaryAclCanonical ||
            acl.Cast<GenericAce>().Any(ace => ace is not CommonAce
                { IsCallback: false, AceQualifier: AceQualifier.AccessAllowed or AceQualifier.AccessDenied }))
            throw new InvalidDataException("SourceAclNotSupported");
        return acl;
    }

    private static RawSecurityDescriptor Inherit(byte[] parentDescriptor, byte[] creatorDescriptor)
    {
        const uint DaclAutoInherit = 0x01;
        const uint AvoidPrivilegeCheck = 0x08;
        const uint AvoidOwnerCheck = 0x10;
        // These flags apply to the in-memory projection only. The actual DACL
        // setter still requires WRITE_DAC on the exact guarded file handle.
        var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200a0, All = 0x1f01ff };
        if (!CreatePrivateObjectSecurityEx(parentDescriptor, creatorDescriptor, out var descriptor, IntPtr.Zero,
                false, DaclAutoInherit | AvoidPrivilegeCheck | AvoidOwnerCheck, IntPtr.Zero, ref mapping))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > 1024 * 1024) throw new InvalidDataException("SourceAclNotSupported");
            var buffer = new byte[length];
            Marshal.Copy(descriptor, buffer, 0, buffer.Length);
            return new RawSecurityDescriptor(buffer, 0);
        }
        finally { _ = DestroyPrivateObjectSecurity(ref descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping { public uint Read, Write, Execute, All; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePrivateObjectSecurityEx(byte[] parentDescriptor, byte[] creatorDescriptor,
        out IntPtr descriptor, IntPtr objectType, [MarshalAs(UnmanagedType.Bool)] bool isDirectory,
        uint autoInheritFlags, IntPtr token, ref GenericMapping mapping);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPrivateObjectSecurity(ref IntPtr descriptor);
}
