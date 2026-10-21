using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal static class SourceReadGrant
{
    internal const int ReadMask = 0x100081;

    // Called only by the separate privileged broker, never by the IIS repair client.
    internal static byte[] Propose(byte[] before, SecurityIdentifier reader, bool inherit)
    {
        var descriptor = new RawSecurityDescriptor(before, 0);
        var acl = descriptor.DiscretionaryAcl;
        if (acl is null || !new CommonSecurityDescriptor(inherit, false, descriptor).IsDiscretionaryAclCanonical ||
            acl.Cast<GenericAce>().Any(ace => ace is not CommonAce || ace is CommonAce { IsCallback: true }))
            throw new InvalidDataException("SourceAclNotSupported");

        var missing = ReadMask;
        foreach (var ace in acl.Cast<CommonAce>())
        {
            if (ace.SecurityIdentifier != reader || ace.AceQualifier != AceQualifier.AccessAllowed ||
                (ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (inherit && ((ace.AceFlags & (AceFlags.ObjectInherit | AceFlags.ContainerInherit)) !=
                    (AceFlags.ObjectInherit | AceFlags.ContainerInherit) || (ace.AceFlags & AceFlags.NoPropagateInherit) != 0)) continue;
            missing &= ~MapGenericFileRights(ace.AccessMask);
        }
        if (missing == 0) return before.ToArray();

        var index = 0;
        while (index < acl.Count && acl[index] is CommonAce { IsInherited: false, AceQualifier: AceQualifier.AccessDenied }) index++;
        acl.InsertAce(index, new CommonAce(inherit ? AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None,
            AceQualifier.AccessAllowed, missing, reader, false, null));
        return Bytes(descriptor);
    }

    internal static byte[] Apply(SafeFileHandle handle, byte[] expectedBefore, SecurityIdentifier reader, bool inherit,
        Action? beforeWriteAttempt = null)
    {
        var before = Read(handle);
        if (!before.AsSpan().SequenceEqual(expectedBefore)) throw new InvalidDataException("SourcePermissionsChanged");
        var proposed = Propose(before, reader, inherit);
        if (proposed.AsSpan().SequenceEqual(before)) return before;

        var native = new RawSecurityDescriptor(proposed, 0);
        // The native setter otherwise drops AI. AR requests retention of the existing
        // inheritance model; this handle call does not recursively walk child objects.
        // The expected persisted descriptor still has exactly the original control bits.
        if (native.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited))
            native.SetFlags(native.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired);
        beforeWriteAttempt?.Invoke();
        var status = NtSetSecurityObject(handle, 4, Bytes(native)); // DACL only, no owner/group/SACL/protection change.
        if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
        var after = Read(handle);
        if (!after.AsSpan().SequenceEqual(proposed)) throw new InvalidDataException("SourceRepairUnconfirmed");
        return after;
    }

    internal static byte[] Read(SafeFileHandle handle)
    {
        // GetSecurityInfo synthesises inherited ACE/protection flags for legacy
        // descriptors. Query the actual object descriptor before preserving it.
        var buffer = new byte[1024];
        var status = NtQuerySecurityObject(handle, 7, buffer, (uint)buffer.Length, out var needed);
        if (unchecked((uint)status) is 0xc0000023 or 0x80000005)
        {
            if (needed <= buffer.Length || needed > 1024 * 1024) throw new InvalidDataException("SourceAclNotSupported");
            buffer = new byte[checked((int)needed)];
            status = NtQuerySecurityObject(handle, 7, buffer, (uint)buffer.Length, out _);
        }
        if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
        return Bytes(new RawSecurityDescriptor(buffer, 0));
    }

    internal static int MapGenericFileRights(int mask)
    {
        var mapped = mask & 0x0fffffff;
        if ((mask & unchecked((int)0x80000000)) != 0) mapped |= 0x120089; // FILE_GENERIC_READ
        if ((mask & 0x40000000) != 0) mapped |= 0x120116;
        if ((mask & 0x20000000) != 0) mapped |= 0x1200a0;
        if ((mask & 0x10000000) != 0) mapped |= 0x1f01ff;
        return mapped;
    }

    private static byte[] Bytes(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySecurityObject(SafeFileHandle handle, uint information, byte[] buffer, uint length, out uint needed);
    [DllImport("ntdll.dll")]
    private static extern int NtSetSecurityObject(SafeFileHandle handle, uint information, byte[] descriptor);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
