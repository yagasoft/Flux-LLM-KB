using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalNativeTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task Supported_native_aggregation_can_hide_repeated_directory_security_changes(bool holdReader)
    {
        var root = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalDirectorySecurity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = new DirectoryInfo(root);
            var original = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            using var persistent = OpenDirectory(root, 0x80, 7, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
            Assert.False(persistent.IsInvalid);
            var identity = PhysicalFileIdentity.Get(persistent).Split(':')[^1];
            if (!holdReader) persistent.Dispose();
            var native = new WindowsSourceChangeJournalReader();
            var before = await native.QueryAsync(root, default);
            ChangeAcl(FileSystemRights.ReadData);
            var start = await native.QueryAsync(root, default);
            var firstAcl = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            Assert.True(await ObservedAsync(before, start), "Qualification setup did not emit its first SECURITY_CHANGE.");
            ChangeAcl(FileSystemRights.ReadData | FileSystemRights.WriteData);
            Assert.NotEqual(Convert.ToHexString(firstAcl), Convert.ToHexString(directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm()));
            var end = await native.QueryAsync(root, default);
            // The failed qualification established this counterexample with distinct
            // DACLs and a passing no-reader control. Keep the required backstop explicit.
            Assert.Equal(!holdReader, await ObservedAsync(start, end));

            async Task<bool> ObservedAsync(JournalCapability from, JournalCapability through)
            {
            var cursor = new JournalCursor(from.VolumeIdentity, from.JournalId, from.NextUsn);
            var found = false;
            for (var pageCount = 0; cursor.NextUsn < through.NextUsn && pageCount < 200; pageCount++)
            {
                var page = await native.ReadAsync(cursor, 65536, default);
                Assert.True(page.Next.NextUsn > cursor.NextUsn);
                found |= page.Changes.Any(x => x.FileId == identity && x.Usn >= from.NextUsn && x.Usn < through.NextUsn && (x.Reason & 0x800) != 0);
                cursor = page.Next;
            }
            Assert.True(cursor.NextUsn >= through.NextUsn);
            return found;
            }

            void ChangeAcl(FileSystemRights rights)
            {
                var acl = new DirectorySecurity();
                acl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.NullSid, null), rights, AccessControlType.Allow));
                directory.SetAccessControl(acl);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [NativeSqlServerFact]
    public async Task Experimental_forced_close_must_restore_change_evidence_without_modifying_fixture_bytes()
    {
        // Synthetic qualification only; production enablement requires its separate qualification gate.
        var root = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalCloseQualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "force-close.txt");
            await File.WriteAllBytesAsync(path, new byte[256 * 1024]);
            var stamp = File.GetLastWriteTimeUtc(path);
            var identity = PhysicalFileIdentity.Get(path).Split(':')[^1];
            var native = new WindowsSourceChangeJournalReader();
            using var persistent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (var cycle = 0; cycle < 3; cycle++)
            {
            Overwrite(0);
            File.SetLastWriteTimeUtc(path, stamp);
            var beforeBytes = await File.ReadAllBytesAsync(path);
            long forcedUsn;
            using (var guard = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!WriteCloseRecord(guard, (9u << 16) | (59u << 2) | 3u, IntPtr.Zero, 0, out forcedUsn, 8, out var count, IntPtr.Zero))
                    throw new IOException("Synthetic forced-close qualification failed", new Win32Exception(Marshal.GetLastPInvokeError()));
                Assert.Equal(8u, count);
                Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(path));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            }
            var boundary = await native.QueryAsync(root, default);
            var closePage = await native.ReadAsync(new(boundary.VolumeIdentity, boundary.JournalId, forcedUsn), 65536, default);
            Assert.Contains(closePage.Changes, x => x.Usn == forcedUsn && x.FileId == identity && (x.Reason & 0x80000000) != 0);

            Overwrite(128 * 1024);
            File.SetLastWriteTimeUtc(path, stamp);
            var end = await native.QueryAsync(root, default);
            var cursor = new JournalCursor(boundary.VolumeIdentity, boundary.JournalId, boundary.NextUsn);
            var observed = false;
            for (var pageCount = 0; cursor.NextUsn < end.NextUsn && pageCount < 200; pageCount++)
            {
                var page = await native.ReadAsync(cursor, 65536, default);
                Assert.True(page.Next.NextUsn > cursor.NextUsn);
                observed |= page.Changes.Any(x => x.FileId == identity && x.Usn >= boundary.NextUsn && x.Usn < end.NextUsn && x.ContentChanged);
                cursor = page.Next;
            }
            Assert.True(cursor.NextUsn >= end.NextUsn);
            Assert.True(observed, "Forcing CLOSE did not establish a new repeated-write boundary; it cannot replace unsealed fallback.");
            }

            void Overwrite(int offset)
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Position = offset;
                writer.WriteByte(42);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteCloseRecord(SafeFileHandle handle, uint code, IntPtr input, uint inputBytes,
        out long usn, uint outputBytes, out uint returned, IntPtr overlapped);

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supported_native_aggregation_can_hide_repeated_writes_while_another_reader_remains_open(bool holdReader)
    {
        var root = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalAggregation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "repeated-write.txt");
            await File.WriteAllBytesAsync(path, new byte[256 * 1024]);
            var stamp = File.GetLastWriteTimeUtc(path);
            var identity = PhysicalFileIdentity.Get(path).Split(':')[^1];
            var native = new WindowsSourceChangeJournalReader();
            using var persistent = holdReader ? new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete) : null;
            Overwrite(0);
            File.SetLastWriteTimeUtc(path, stamp); // Both reason kinds already occurred before S.
            JournalCapability start;
            var source = FluxKnowledge.Domain.Sources.SourceRootConfiguration.Create(root, "Aggregation fixture", true, false, 1024 * 1024);
            using (var first = new WindowsSourceObservationReader().Open(source, "repeated-write.txt"))
            {
                first.ValidateCurrent();
                start = await native.QueryAsync(root, default);
            }
            Overwrite(128 * 1024); // Reopened writer, while another reader may retain aggregation state.
            File.SetLastWriteTimeUtc(path, stamp);
            using (var final = new WindowsSourceObservationReader().Open(source, "repeated-write.txt")) final.ValidateCurrent();
            var end = await native.QueryAsync(root, default);
            var cursor = new JournalCursor(start.VolumeIdentity, start.JournalId, start.NextUsn);
            var changes = new List<JournalChange>();
            for (var pageCount = 0; cursor.NextUsn < end.NextUsn && pageCount < 200; pageCount++)
            {
                var page = await native.ReadAsync(cursor, 65536, default);
                Assert.True(page.Next.NextUsn > cursor.NextUsn);
                changes.AddRange(page.Changes.Where(x => x.FileId == identity && x.Usn >= start.NextUsn && x.Usn < end.NextUsn));
                cursor = page.Next;
            }
            Assert.True(cursor.NextUsn >= end.NextUsn);
            // Qualification corrected the original, disproved assumption that every reopened
            // writer gets a fresh record. Preserve this counterexample as a native regression.
            Assert.Equal(!holdReader, changes.Any(x => x.ContentChanged));

            void Overwrite(int offset)
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Position = offset;
                writer.WriteByte(42);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [NativeSqlServerFact]
    public async Task Existing_journal_reports_a_real_isolated_file_edit_without_source_enumeration()
    {
        var root = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalNative-" + Guid.NewGuid().ToString("N"));
        var reader = new WindowsSourceChangeJournalReader();
        var before = await reader.QueryAsync(root, default);
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "one-edit.txt");
            await File.WriteAllTextAsync(path, "journal-fixture");
            var identity = PhysicalFileIdentity.Get(path).Split(':')[^1];
            var through = await reader.QueryAsync(root, default);
            var cursor = new JournalCursor(before.VolumeIdentity, before.JournalId, before.NextUsn);
            var changes = new List<JournalChange>();
            for (var count = 0; cursor.NextUsn < through.NextUsn && count < 200; count++)
            {
                var page = await reader.ReadAsync(cursor, 65536, default);
                Assert.True(page.Next.NextUsn > cursor.NextUsn);
                changes.AddRange(page.Changes.Where(x => x.FileId == identity));
                cursor = page.Next;
            }
            Assert.True(cursor.NextUsn >= through.NextUsn);
            Assert.Contains(changes, x => x.Name == "one-edit.txt" && x.ContentChanged);
            Assert.Contains(changes, x => x.ParentPath is { } parent &&
                parent.Equals(root, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
