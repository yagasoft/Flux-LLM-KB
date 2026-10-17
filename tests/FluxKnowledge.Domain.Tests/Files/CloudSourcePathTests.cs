using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class CloudSourcePathTests
{
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
