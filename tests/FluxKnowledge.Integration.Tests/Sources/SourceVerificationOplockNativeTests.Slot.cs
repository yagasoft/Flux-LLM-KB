using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Fact]
    public void Reader_slot_is_nonblocking_across_instances_and_preserves_the_same_lock_file()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName), []);
        var slot = new WindowsSourceVerificationReaderSlot(fixture.Root);
        using (var first = slot.TryAcquire())
        {
            Assert.NotNull(first);
            using var other = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire();
            Assert.Null(other);
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName)));
        }
        var before = PhysicalFileIdentity.Get(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName));
        using (var next = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire())
            Assert.NotNull(next);
        Assert.Equal(before, PhysicalFileIdentity.Get(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName)));
    }

    [Fact]
    public void Slot_does_not_depend_on_the_acquiring_threads_lifetime()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName), []);
        IDisposable? acquired = null;
        Exception? failure = null;
        var issuer = new Thread(() =>
        {
            try { acquired = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire(); }
            catch (Exception error) { failure = error; }
        });
        issuer.Start();
        Assert.True(issuer.Join(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Null(failure);
            Assert.NotNull(acquired);
            using var other = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire();
            Assert.Null(other);
        }
        finally { acquired?.Dispose(); }
        using var next = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire();
        Assert.NotNull(next);
    }

    [Fact]
    public void Unsafe_slot_location_is_reported_instead_of_treated_as_another_reader()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName));
        var error = Record.Exception(() => new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire()?.Dispose());
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Unsafe slot was silently ignored");
    }

    [Fact]
    public void Missing_reader_slot_fails_without_creating_a_runtime_file()
    {
        using var fixture = new Fixture();
        Assert.Throws<FileNotFoundException>(() => new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire()?.Dispose());
        Assert.False(File.Exists(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName)));
    }

    [Fact]
    public void Reader_slot_works_with_the_production_Runtime_read_execute_mask()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName);
        File.WriteAllBytes(path, []);
        var before = PhysicalFileIdentity.Get(path);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        var directory = new DirectoryInfo(fixture.Root);
        PrepareReadOnlyRuntime(fixture.Root, identity.User!);
        var sddl = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var admin = new SecurityIdentifier("S-1-5-32-544");
        var bytes = new byte[admin.BinaryLength];
        admin.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!CreateRestrictedSlotToken(identity.AccessToken, 1, 1,
                    [new() { Sid = pin.AddrOfPinnedObject() }], 0, IntPtr.Zero, 0, IntPtr.Zero, out var token))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            using (token)
            {
                using var held = WindowsIdentity.RunImpersonated(token,
                    () => new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire());
                Assert.NotNull(held);
                Assert.Null(new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire());
            }
        }
        finally { pin.Free(); }
        Assert.Equal(before, PhysicalFileIdentity.Get(path));
        Assert.Equal(sddl, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public void Installer_provisions_once_preserves_identity_and_refuses_a_held_or_unsafe_existing_slot()
    {
        using var fixture = new Fixture();
        using var identity = WindowsIdentity.GetCurrent();
        var readerSid = identity.User!;
        PrepareReadOnlyRuntime(fixture.Root, readerSid);
        var directory = new DirectoryInfo(fixture.Root);
        var sddl = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var path = Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName);
        NativeGoLiveWindowsAclPort.EnsureSourceVerificationReaderFile(fixture.Root, readerSid);
        var before = PhysicalFileIdentity.Get(path);
        NativeGoLiveWindowsAclPort.EnsureSourceVerificationReaderFile(fixture.Root, readerSid);
        Assert.Equal(before, PhysicalFileIdentity.Get(path));
        using (var held = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire())
        {
            Assert.NotNull(held);
            Assert.Throws<IOException>(() => NativeGoLiveWindowsAclPort.EnsureSourceVerificationReaderFile(fixture.Root, readerSid));
        }
        var file = new FileInfo(path);
        var unsafeAcl = file.GetAccessControl();
        unsafeAcl.AddAccessRule(new(readerSid, FileSystemRights.Write, AccessControlType.Allow));
        file.SetAccessControl(unsafeAcl);
        var unsafeSddl = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.Throws<NativeGoLiveContractException>(() => NativeGoLiveWindowsAclPort.EnsureSourceVerificationReaderFile(fixture.Root, readerSid));
        Assert.Equal(unsafeSddl, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        Assert.Equal(before, PhysicalFileIdentity.Get(path));
        Assert.Equal(sddl, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    private static void PrepareReadOnlyRuntime(string path, SecurityIdentifier reader)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new(reader, (FileSystemRights)0x1200a9,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SlotSidAndAttributes { internal IntPtr Sid; internal uint Attributes; }

    [DllImport("advapi32.dll", EntryPoint = "CreateRestrictedToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedSlotToken(SafeAccessTokenHandle token, uint flags, uint disableCount,
        SlotSidAndAttributes[] disable, uint deleteCount, IntPtr delete, uint restrictedCount, IntPtr restricted,
        out SafeAccessTokenHandle result);
}
