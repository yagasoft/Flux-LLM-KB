using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxKnowledge.Integrations.Files;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceReadGrantTests
{
    private static readonly SecurityIdentifier Reader = new("S-1-5-21-1-2-3-1000");

    [Theory]
    [InlineData("D:AI(A;ID;FA;;;SY)(A;ID;FR;;;BU)")]
    [InlineData("D:PAI(A;;FA;;;SY)(A;;FR;;;BU)")]
    public void Proposal_preserves_control_owner_group_and_every_existing_ace(string dacl)
    {
        var before = Bytes("O:BAG:SY" + dacl);
        var after = new RawSecurityDescriptor(SourceReadGrant.Propose(before, Reader, false), 0);
        var original = new RawSecurityDescriptor(before, 0);

        Assert.Equal(original.Owner, after.Owner);
        Assert.Equal(original.Group, after.Group);
        Assert.Equal(original.ControlFlags, after.ControlFlags);
        Assert.Equal(Aces(original), Aces(after).Where(ace => !ace.Contains(Reader.Value, StringComparison.Ordinal)));
        var added = Assert.Single(after.DiscretionaryAcl!.Cast<GenericAce>().OfType<CommonAce>(),
            ace => ace.SecurityIdentifier == Reader);
        Assert.Equal(0x100081, added.AccessMask);
        Assert.Equal(AceFlags.None, added.AceFlags);
    }

    [Fact]
    public void Repeated_file_and_directory_proposals_are_idempotent()
    {
        foreach (var directory in new[] { false, true })
        {
            var original = Bytes("O:BAG:SYD:PAI(A;;FA;;;SY)");
            var once = SourceReadGrant.Propose(original, Reader, directory);
            Assert.Equal(once, SourceReadGrant.Propose(once, Reader, directory));
            var ace = new RawSecurityDescriptor(once, 0).DiscretionaryAcl!.Cast<CommonAce>()
                .Single(item => item.SecurityIdentifier == Reader);
            Assert.Equal(directory ? AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None, ace.AceFlags);
        }
    }

    [Fact]
    public void Existing_partial_file_allow_is_preserved_and_only_missing_read_bits_are_added()
    {
        var before = Bytes($"O:BAG:SYD:P(A;;0x80;;;{Reader.Value})(A;;FA;;;SY)");
        var after = new RawSecurityDescriptor(SourceReadGrant.Propose(before, Reader, false), 0);
        var masks = after.DiscretionaryAcl!.Cast<CommonAce>().Where(ace => ace.SecurityIdentifier == Reader)
            .Select(ace => ace.AccessMask).Order().ToArray();
        Assert.Equal([0x80, 0x100001], masks);
    }

    [Fact]
    public void Null_or_noncanonical_acl_is_not_normalised_by_repair()
    {
        Assert.Throws<InvalidDataException>(() => SourceReadGrant.Propose(Bytes("O:BAG:SYD:NO_ACCESS_CONTROL"), Reader, false));
        Assert.Throws<InvalidDataException>(() => SourceReadGrant.Propose(
            Bytes("O:BAG:SYD:(A;;FA;;;SY)(D;;FR;;;BU)"), Reader, false));
    }

    [Fact]
    public void Native_write_preserves_naturally_stale_moved_acl_and_protected_inheritance()
    {
        using var fixture = new AclFixture();
        var old = fixture.Directory("old");
        var destination = fixture.Directory("new");
        var parent = new DirectoryInfo(old).GetAccessControl();
        parent.SetAccessRuleProtection(true, false);
        parent.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(old).SetAccessControl(parent);
        var file = Path.Combine(old, "moved.txt");
        File.WriteAllText(file, "Moved private fixture");
        new FileInfo(file).SetAccessControl(new FileInfo(file).GetAccessControl());
        var beforeMove = AclFixture.Read(file);
        Assert.True(new RawSecurityDescriptor(beforeMove, 0).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited));
        var moved = Path.Combine(destination, "moved.txt");
        File.Move(file, moved);
        Assert.Equal(beforeMove, AclFixture.Read(moved));
        ApplyAndAssert(moved, beforeMove);

        var protectedFile = Path.Combine(destination, "protected.txt");
        File.WriteAllText(protectedFile, "Protected private fixture");
        var protectedAcl = new FileInfo(protectedFile).GetAccessControl();
        protectedAcl.SetAccessRuleProtection(true, true);
        new FileInfo(protectedFile).SetAccessControl(protectedAcl);
        var protectedBefore = AclFixture.Read(protectedFile);
        var controls = new RawSecurityDescriptor(protectedBefore, 0).ControlFlags;
        Assert.True(controls.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.True(controls.HasFlag(ControlFlags.DiscretionaryAclAutoInherited));
        ApplyAndAssert(protectedFile, protectedBefore);
    }

    [Fact]
    public void Inheritable_root_grant_affects_new_files_without_rewriting_existing_descendants()
    {
        using var fixture = new AclFixture();
        var childDirectory = fixture.Directory("existing");
        var file = Path.Combine(childDirectory, "existing.txt");
        File.WriteAllText(file, "Existing fixture");
        var childBefore = AclFixture.Read(childDirectory);
        var fileBefore = AclFixture.Read(file);
        var rootBefore = AclFixture.Read(fixture.Root);
        using (var handle = AclFixture.Open(fixture.Root))
            SourceReadGrant.Apply(handle, rootBefore, Reader, true);

        Assert.Equal(childBefore, AclFixture.Read(childDirectory));
        Assert.Equal(fileBefore, AclFixture.Read(file));
        var fresh = Path.Combine(fixture.Root, "fresh.txt");
        File.WriteAllText(fresh, "New fixture");
        var inherited = Assert.Single(new RawSecurityDescriptor(AclFixture.Read(fresh), 0)
            .DiscretionaryAcl!.Cast<CommonAce>(), ace => ace.SecurityIdentifier == Reader);
        Assert.Equal(SourceReadGrant.ReadMask, inherited.AccessMask);
        // Legacy native descriptors do not mark inherited ACEs with ID; Win32
        // reports their inheritance, while the raw grant still comes from this parent.
        Assert.True(Assert.Single(new RawSecurityDescriptor(AclFixture.ReadReported(fresh), 0)
            .DiscretionaryAcl!.Cast<CommonAce>(), ace => ace.SecurityIdentifier == Reader).IsInherited);
        var newDirectory = fixture.Directory("new-directory");
        var grandchild = Path.Combine(newDirectory, "grandchild.txt");
        File.WriteAllText(grandchild, "New grandchild fixture");
        var grandchildRead = Assert.Single(new RawSecurityDescriptor(AclFixture.Read(grandchild), 0)
            .DiscretionaryAcl!.Cast<CommonAce>(), ace => ace.SecurityIdentifier == Reader);
        Assert.Equal(SourceReadGrant.ReadMask, grandchildRead.AccessMask);
        Assert.True(Assert.Single(new RawSecurityDescriptor(AclFixture.ReadReported(grandchild), 0)
            .DiscretionaryAcl!.Cast<CommonAce>(), ace => ace.SecurityIdentifier == Reader).IsInherited);
        Assert.Equal(childBefore, AclFixture.Read(childDirectory));
        Assert.Equal(fileBefore, AclFixture.Read(file));
    }

    [Fact]
    public void Win32_inheritance_reporting_does_not_replace_the_persisted_native_descriptor()
    {
        using var fixture = new AclFixture();
        var child = fixture.Directory("legacy-child");
        var before = AclFixture.Read(child);
        var reportedBefore = AclFixture.ReadReported(child);
        using (var handle = AclFixture.Open(fixture.Root))
            SourceReadGrant.Apply(handle, AclFixture.Read(fixture.Root), Reader, true);
        Assert.Equal(before, AclFixture.Read(child));
        var reportedAfter = AclFixture.ReadReported(child);
        Assert.Equal(before, AclFixture.Read(child));
        // Win32 may synthesise P/inherited ACE flags when a legacy child's old
        // ACL no longer matches its parent. It must never become our write baseline.
        if (!new RawSecurityDescriptor(before, 0).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited))
        {
            Assert.NotEqual(reportedBefore, reportedAfter);
            Assert.True(new RawSecurityDescriptor(reportedAfter, 0).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.False(new RawSecurityDescriptor(before, 0).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        }
    }

    [Fact]
    public void Stale_expected_acl_does_not_overwrite_another_permission_change()
    {
        using var fixture = new AclFixture();
        var file = Path.Combine(fixture.Root, "changed.txt");
        File.WriteAllText(file, "Fixture");
        var before = AclFixture.Read(file);
        var acl = new FileInfo(file).GetAccessControl();
        acl.AddAccessRule(new(Reader, FileSystemRights.ReadAttributes, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(acl);
        var changed = AclFixture.Read(file);
        using var handle = AclFixture.Open(file);
        Assert.Throws<InvalidDataException>(() => SourceReadGrant.Apply(handle, before, Reader, false));
        Assert.Equal(changed, AclFixture.Read(file));
    }

    private static void ApplyAndAssert(string file, byte[] before)
    {
        var identity = new FileInfo(file);
        var length = identity.Length;
        var lastWrite = identity.LastWriteTimeUtc;
        var proposed = SourceReadGrant.Propose(before, Reader, false);
        using (var handle = AclFixture.Open(file)) SourceReadGrant.Apply(handle, before, Reader, false);
        Assert.Equal(proposed, AclFixture.Read(file));
        using (var handle = AclFixture.Open(file)) SourceReadGrant.Apply(handle, proposed, Reader, false);
        Assert.Equal(proposed, AclFixture.Read(file));
        Assert.Equal(length, new FileInfo(file).Length);
        Assert.Equal(lastWrite, new FileInfo(file).LastWriteTimeUtc);
    }

    private static byte[] Bytes(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static string[] Aces(RawSecurityDescriptor descriptor) => descriptor.DiscretionaryAcl!
        .Cast<GenericAce>().Select(ace =>
        {
            var bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            return (ace is KnownAce known ? known.SecurityIdentifier.Value : "unknown") + ":" + Convert.ToHexString(bytes);
        }).ToArray();

    private sealed class AclFixture : IDisposable
    {
        public string Root { get; } = System.IO.Directory.CreateTempSubdirectory("FluxKnowledgeAcl_").FullName;
        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture escaped the test temporary root.");
            System.IO.Directory.Delete(full, true);
        }

        public static SafeFileHandle Open(string path)
        {
            var handle = CreateFile(path, 0x60080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        // Independent native baseline, without Win32 legacy-inheritance synthesis.
        public static byte[] Read(string path)
        {
            using var handle = Open(path);
            var bytes = new byte[65536];
            var status = NtQuerySecurityObject(handle, 7, bytes, (uint)bytes.Length, out _);
            if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
            return Normalise(bytes);
        }

        public static byte[] ReadReported(string path)
        {
            using var handle = Open(path);
            var error = GetSecurityInfo(handle, 1, 7, out _, out _, out _, out _, out var descriptor);
            if (error != 0) throw new Win32Exception((int)error);
            try
            {
                var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return Normalise(bytes);
            }
            finally { LocalFree(descriptor); }
        }

        private static byte[] Normalise(byte[] bytes)
        {
            var raw = new RawSecurityDescriptor(bytes, 0);
            var result = new byte[raw.BinaryLength];
            raw.GetBinaryForm(result, 0);
            return result;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySecurityObject(SafeFileHandle handle, uint info, byte[] buffer, uint length, out uint needed);
        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityInfo(SafeFileHandle file, int kind, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
