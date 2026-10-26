using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class CloudSourcePathTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Experimental_forced_close_qualifies_on_a_local_cloud_file_with_restricted_read_only_authority()
    {
        // Synthetic Cloud Files provider only; this does not qualify production OneDrive.
        using var fixture = new CloudSourceFixture(@"I:\Codex Workspaces\Staging\FluxKnowledge");
        var directory = fixture.Directory("documents");
        var path = fixture.File("documents/available.txt", new string('x', 256 * 1024));
        fixture.MakeCloudDirectory(directory);
        var stamp = File.GetLastWriteTimeUtc(path);
        var native = new WindowsSourceChangeJournalReader();
        var identity = PhysicalFileIdentity.Get(path).Split(':')[^1];
        using var persistent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Overwrite(0);
        File.SetLastWriteTimeUtc(path, stamp);
        var before = await File.ReadAllBytesAsync(path);
        long forcedUsn = 0;
        using (var token = SourceAccessBrokerTests.RestrictedToken())
            WindowsIdentity.RunImpersonated(token, () =>
            {
                Assert.False(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
                using var parent = WindowsSourcePathAccess.OpenDirectoryPath(directory);
                using var guard = WindowsSourcePathAccess.OpenRelative(parent.Handle, "available.txt", 0x00100081, 1, 0x60, path);
                WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(guard));
                if (!ForceSyntheticClose(guard, (9u << 16) | (59u << 2) | 3u, IntPtr.Zero, 0, out forcedUsn, 8, out var count, IntPtr.Zero))
                    throw new IOException("Synthetic restricted cloud close failed", new Win32Exception(Marshal.GetLastPInvokeError()));
                Assert.Equal(8u, count);
            });
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        var start = await native.QueryAsync(directory, default);
        var exact = await native.ReadAsync(new(start.VolumeIdentity, start.JournalId, forcedUsn), 65536, default);
        Assert.Contains(exact.Changes, x => x.Usn == forcedUsn && x.FileId == identity && (x.Reason & 0x80000000) != 0);
        Overwrite(128 * 1024);
        File.SetLastWriteTimeUtc(path, stamp);
        var end = await native.QueryAsync(directory, default);
        var cursor = new JournalCursor(start.VolumeIdentity, start.JournalId, start.NextUsn);
        var observed = false;
        for (var pageCount = 0; cursor.NextUsn < end.NextUsn && pageCount < 200; pageCount++)
        {
            var page = await native.ReadAsync(cursor, 65536, default);
            Assert.True(page.Next.NextUsn > cursor.NextUsn);
            observed |= page.Changes.Any(x => x.FileId == identity && x.Usn >= start.NextUsn && x.Usn < end.NextUsn && x.ContentChanged);
            cursor = page.Next;
        }
        Assert.True(cursor.NextUsn >= end.NextUsn);
        Assert.True(observed);

        void Overwrite(int offset)
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Position = offset;
            writer.WriteByte(42);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ForceSyntheticClose(SafeFileHandle handle, uint code, IntPtr input, uint inputBytes,
        out long usn, uint outputBytes, out uint returned, IntPtr overlapped);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absence_requires_complete_membership_in_the_root_and_traversed_parent(bool nested)
    {
        using var fixture = new CloudSourceFixture();
        var directory = fixture.Directory("documents");
        var parent = nested ? fixture.Directory("documents/nested") : directory;
        fixture.MakeCloudDirectory(parent);
        var root = SourceRootConfiguration.Create(directory, "Cloud documents", true, false, 1024);

        var error = Assert.Throws<SourceContentUnavailableException>(() => new WindowsSourceObservationReader()
            .OpenAbsence(root, nested ? "nested/missing.txt" : "missing.txt"));
        Assert.Equal("SourceNamespaceIncomplete", error.Message); // Refuse from membership before consulting missing-child lookup.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absence_rechecks_membership_completeness_after_lease_acquisition(bool nested)
    {
        using var fixture = new CloudSourceFixture();
        var directory = fixture.Directory("documents");
        var parent = nested ? fixture.Directory("documents/nested") : directory;
        var root = SourceRootConfiguration.Create(directory, "Cloud documents", true, false, 1024);
        using var absence = Assert.IsAssignableFrom<ISourceAbsenceLease>(new WindowsSourceObservationReader()
            .OpenAbsence(root, nested ? "nested/missing.txt" : "missing.txt"));

        fixture.MakeCloudDirectory(parent);

        var error = Assert.Throws<SourceContentUnavailableException>(absence.ValidateCurrent);
        Assert.Equal("SourceNamespaceIncomplete", error.Message);
    }

    [Theory]
    [InlineData(0x9000001au)]
    [InlineData(0x9000601au)]
    [InlineData(0x9000701au)]
    [InlineData(0x9000f01au)]
    public void Cloud_tags_are_allowed_without_requiring_the_in_sync_flag(uint tag) =>
        WindowsSourcePathAccess.EnsureContentAvailable(new(0x400, tag, 1));

    [Theory]
    [InlineData(0xa000000cu)] // symbolic link
    [InlineData(0xa0000003u)] // junction
    [InlineData(0x9000101bu)] // unsupported Microsoft tag
    [InlineData(0x1000001au)] // not the Microsoft cloud family
    public void Unsupported_reparse_tags_are_rejected(uint tag) =>
        Assert.Throws<SourcePathSecurityException>(() => WindowsSourcePathAccess.ValidateTag(new(0x410, tag, 0)));

    [Theory]
    [InlineData(0x1000u, 0u)]
    [InlineData(0x40000u, 0u)]
    [InlineData(0x400000u, 0u)]
    [InlineData(0u, 0x10u)]
    [InlineData(0u, 0x20u)]
    [InlineData(0u, uint.MaxValue)]
    public void Missing_or_unvalidated_content_is_unavailable(uint attributes, uint state) =>
        Assert.Throws<SourceContentUnavailableException>(() =>
            WindowsSourcePathAccess.EnsureContentAvailable(new(attributes | 0x400, 0x9000701a, state)));

    [Fact]
    public void Root_admission_accepts_a_real_cloud_directory_and_retains_physical_identity()
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("documents");
        fixture.MakeCloudDirectory(path);
        var metadata = CloudSourceFixture.Metadata(path);
        // Current Cloud Files may expose recall state without a public reparse bit.
        Assert.True((metadata.Attributes & 0x00400400) != 0);
        var policy = new SourceRootPathPolicy(new LocalIngressOptions([fixture.Root]));

        var result = policy.ValidateAndCanonicalise(new SourceRootCreateRequest(
            path, "Cloud documents", false, [], [], false, 1024, [], TimeSpan.FromMinutes(15), "test"));

        Assert.Equal(PhysicalFileIdentity.GetDirectory(path).IdentityFingerprint, result.PhysicalIdentity.IdentityFingerprint);
        Assert.Equal(Path.GetFullPath(path), result.CanonicalPath);
    }

    [Fact]
    public async Task Discovery_reads_a_hydrated_placeholder_and_reports_a_disconnected_cloud_only_sibling()
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("documents");
        fixture.File("documents/available.txt", "Available cloud document");
        fixture.File("documents/offline.txt", "Unavailable cloud document", dehydrate: true);
        fixture.MakeCloudDirectory(path);
        var root = SourceRootConfiguration.Create(path, "Cloud documents", false, false, 1024);
        var enumerator = new LocalSourceEnumerator();
        var files = new List<string>();

        await foreach (var file in enumerator.EnumerateAsync(root, CancellationToken.None))
        {
            files.Add(file.RelativePath);
            Assert.Equal("Available cloud document", file.Classification.Text);
        }

        Assert.Equal(["available.txt"], files);
        Assert.Contains(enumerator.LastEvidence, item => item.Kind == "unavailable" && item.RelativePath == "offline.txt");
        Assert.Contains(enumerator.LastEvidence, item => item.Detail == "CloudDirectoryNamespaceIncomplete");
    }

    [Fact]
    public async Task Content_lease_prevents_dehydration_and_parent_replacement_until_read_finishes()
    {
        using var fixture = new CloudSourceFixture();
        var directory = fixture.Directory("documents");
        var path = fixture.File("documents/available.txt", "local bytes");
        using (var lease = WindowsSourcePathAccess.OpenRead(directory, path))
        {
            Assert.Equal(32, CloudSourceFixture.TryDehydrate(path)); // ERROR_SHARING_VIOLATION before provider access
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
            using var reader = new StreamReader(new FileStream(lease.Handle, FileAccess.Read, 4096, true));
            Assert.Equal("local bytes", await reader.ReadToEndAsync());
        }
        // This disconnected fixture has no provider to complete dehydration; the lease is released.
        Assert.NotEqual(32, CloudSourceFixture.TryDehydrate(path));
    }

    [Fact]
    public async Task Available_cloud_content_reaches_retention_and_both_supported_file_readers()
    {
        using var fixture = new CloudSourceFixture();
        var directory = fixture.Directory("documents");
        var path = fixture.File("documents/available.txt", "Available café document");
        fixture.MakeCloudDirectory(directory);
        var files = new List<SourceDiscoveredFile>();
        await foreach (var file in new LocalSourceEnumerator().EnumerateAsync(
            SourceRootConfiguration.Create(directory, "Cloud documents", false, false, 1024), CancellationToken.None)) files.Add(file);
        var snapshot = Assert.Single(files);
        var storageRoot = System.IO.Directory.CreateTempSubdirectory("FluxKnowledgeCloudRetention_").FullName;
        try
        {
            using var store = new ContentAddressedSourceArtifactStore(storageRoot);
            var receipt = await store.PutFileAsync(snapshot,
                new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), CancellationToken.None);
            var retained = await ContainedFileReader.ReadAsync(storageRoot, receipt.StoreRelativePath, 1024,
                CancellationToken.None, snapshot.ContentSha256, snapshot.ByteLength);
            var ingress = await new Utf8FileSourceReader(new LocalIngressOptions([directory])).ReadAsync(path, CancellationToken.None);

            Assert.Equal(snapshot.ClassificationBuffer, retained.Bytes);
            Assert.Equal(snapshot.ContentSha256, ingress.ContentHash);
            Assert.Equal("Available café document", ingress.Text);
        }
        finally { System.IO.Directory.Delete(storageRoot, true); }
    }

    [Fact]
    public async Task Retention_refuses_content_that_became_offline_after_discovery()
    {
        using var fixture = new CloudSourceFixture();
        var path = Path.Combine(fixture.Root, "available.txt");
        await System.IO.File.WriteAllTextAsync(path, "local bytes");
        var files = new List<SourceDiscoveredFile>();
        await foreach (var file in new LocalSourceEnumerator().EnumerateAsync(
            SourceRootConfiguration.Create(fixture.Root, "Cloud documents", false, false, 1024), CancellationToken.None)) files.Add(file);
        var snapshot = Assert.Single(files);
        fixture.MakeCloudOnlyFile(path);
        var storageRoot = System.IO.Directory.CreateTempSubdirectory("FluxKnowledgeCloudRetention_").FullName;
        try
        {
            using var store = new ContentAddressedSourceArtifactStore(storageRoot);
            await Assert.ThrowsAsync<SourceContentUnavailableException>(() => store.PutFileAsync(snapshot,
                new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), CancellationToken.None).AsTask());
            Assert.Empty(System.IO.Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories));
        }
        finally { System.IO.Directory.Delete(storageRoot, true); }
    }

    [Fact]
    public async Task Directory_becoming_partial_during_traversal_keeps_the_inventory_incomplete()
    {
        using var fixture = new CloudSourceFixture();
        var directory = fixture.Directory("documents");
        fixture.File("documents/available.txt", "local bytes");
        var enumerator = new LocalSourceEnumerator(beforeDirectoryRecheck: fixture.MakeCloudDirectory);
        var files = new List<SourceDiscoveredFile>();

        await foreach (var file in enumerator.EnumerateAsync(
            SourceRootConfiguration.Create(directory, "Cloud documents", false, false, 1024), CancellationToken.None)) files.Add(file);

        Assert.Single(files);
        Assert.Contains(enumerator.LastEvidence, item => item.Kind == "partial" && item.Detail == "CloudDirectoryNamespaceIncomplete");
    }

    [Fact]
    public async Task Source_scan_rejects_a_final_file_link_even_when_its_target_is_available()
    {
        using var fixture = new CloudSourceFixture();
        var target = fixture.File("target.txt", "Authorised bytes do not authorise a file link.");
        var link = Path.Combine(fixture.Root, "linked.txt");
        System.IO.File.CreateSymbolicLink(link, target);
        try
        {
            var enumerator = new LocalSourceEnumerator();
            var root = SourceRootConfiguration.Create(fixture.Root, "Cloud", false, false, 1024, includePatterns: ["linked.txt"]);
            await foreach (var _ in enumerator.EnumerateAsync(root, default)) Assert.Fail("A linked source file was read.");
            Assert.Contains(enumerator.LastEvidence, item => item.Kind == "security" && item.Detail == "SourceReparseTypeNotSupported" && item.RelativePath == "linked.txt");
        }
        finally { System.IO.File.Delete(link); }
    }
}
