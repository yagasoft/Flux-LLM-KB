using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceAccessIntegrationTests
{
    [Fact]
    public async Task Preview_is_read_only_then_normal_discovery_repairs_and_has_no_obsolete_error()
    {
        using var fixture = new CloudSourceFixture();
        var path = Path.Combine(fixture.Root, "old.txt");
        await File.WriteAllTextAsync(path, "Recovered source text");
        var original = DenyRead(path);
        var client = new RestoringClient(path, original);
        var enumerator = new LocalSourceEnumerator(sourceAccessRepairClient: client);
        var root = SourceRootConfiguration.Create(fixture.Root, "Private source", false, false, 1024);
        try
        {
            await foreach (var _ in enumerator.PreviewAsync(root, SourcePreviewLimits.Default, default))
                Assert.Fail("Preview cannot read the denied file or mutate its permissions.");
            Assert.Empty(client.Requests);
            Assert.Contains(enumerator.LastEvidence, issue => issue.Kind == "permission");
            var files = await Files(enumerator, root);
            Assert.Equal("Recovered source text", Assert.Single(files).Classification.Text);
            Assert.Single(client.Requests);
            Assert.Empty(enumerator.LastEvidence);
        }
        finally { Restore(path, original); }
    }

    [Fact]
    public async Task Access_lost_between_discovery_and_retention_recovers_before_the_publication_gate()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "document.txt");
        await File.WriteAllTextAsync(path, "Available source content");
        var snapshot = Assert.Single(await Files(new LocalSourceEnumerator(),
            SourceRootConfiguration.Create(source.Root, "Private source", false, false, 1024)));
        var original = DenyRead(path);
        var client = new RestoringClient(path, original);
        var gate = new Gate(() => Assert.Single(client.Requests));
        using var store = new ContentAddressedSourceArtifactStore(storage.Root, publicationGate: gate, sourceAccessRepairClient: client);
        try
        {
            var receipt = await store.PutFileAsync(snapshot, new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default);
            if (receipt.PublicationLease is not null) await receipt.PublicationLease.DisposeAsync();
            var retained = await ContainedFileReader.ReadAsync(storage.Root, receipt.StoreRelativePath, 1024,
                default, snapshot.ContentSha256, snapshot.ByteLength);
            Assert.Equal("Available source content", System.Text.Encoding.UTF8.GetString(retained.Bytes));
            Assert.Single(client.Requests);
        }
        finally { Restore(path, original); }
    }

    [Fact]
    public async Task Capture_commit_revalidation_never_changes_source_permissions()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "document.txt");
        await File.WriteAllTextAsync(path, "Captured source content");
        var snapshot = Assert.Single(await Files(new LocalSourceEnumerator(),
            SourceRootConfiguration.Create(source.Root, "Private source", false, false, 1024))) with
            { ScanOwnership = new(SourceScanRequestId.New(), new(Guid.NewGuid(), "permission-test", 1)) };
        var original = new FileInfo(path).GetAccessControl();
        var client = new RestoringClient(path, original);
        using var store = new ContentAddressedSourceArtifactStore(storage.Root, sourceAccessRepairClient: client);
        var receipt = await store.PutFileAsync(snapshot, new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default);
        try
        {
            var capture = Assert.IsAssignableFrom<ISourceCaptureLease>(receipt.PublicationLease);
            _ = DenyRead(path);
            Assert.Throws<SourceAccessDeniedException>(capture.ValidateCurrent);
            Assert.Empty(client.Requests);
        }
        finally
        {
            Restore(path, original);
            await receipt.PublicationLease!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retained_storage_access_failure_cannot_request_a_source_grant()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "document.txt");
        await File.WriteAllTextAsync(path, "Source text");
        var snapshot = Assert.Single(await Files(new LocalSourceEnumerator(),
            SourceRootConfiguration.Create(source.Root, "Private source", false, false, 1024)));
        var client = new RestoringClient(path, new FileInfo(path).GetAccessControl());
        using var store = new ContentAddressedSourceArtifactStore(storage.Root,
            beforeArtifactWrite: _ => throw new SourceAccessDeniedException(storage.Root, true, new System.ComponentModel.Win32Exception(5)),
            sourceAccessRepairClient: client);
        await Assert.ThrowsAsync<SourceAccessDeniedException>(() => store.PutFileAsync(snapshot,
            new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default).AsTask());
        Assert.Empty(client.Requests);
    }

    private static FileSecurity DenyRead(string path)
    {
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        var denied = file.GetAccessControl();
        denied.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        file.SetAccessControl(denied);
        return original;
    }

    private static void Restore(string path, FileSecurity original)
    {
        var replacement = new FileSecurity();
        replacement.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(replacement);
    }

    private static async Task<List<SourceDiscoveredFile>> Files(LocalSourceEnumerator enumerator, SourceRootConfiguration root)
    {
        var result = new List<SourceDiscoveredFile>();
        await foreach (var file in enumerator.EnumerateAsync(root, default)) result.Add(file);
        return result;
    }

    // Integration seam only. Real broker tests separately prove additive grants and deny preservation.
    private sealed class RestoringClient(string path, FileSecurity original) : ISourceAccessRepairClient
    {
        internal List<SourceAccessRepairRequest> Requests { get; } = [];
        public SourceAccessRepairOutcome TryRepair(SourceAccessRepairRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Restore(path, original);
            return SourceAccessRepairOutcome.Repaired;
        }
    }

    private sealed class Gate(Action beforeAcquire) : ISourceArtifactPublicationGate
    {
        public ValueTask<ISourceArtifactPublicationLease> AcquireSharedAsync(string hash, CancellationToken token)
        { beforeAcquire(); return ValueTask.FromResult<ISourceArtifactPublicationLease>(new Lease()); }
        public ValueTask<ISourceArtifactPublicationLease?> TryAcquireExclusiveAsync(string hash, CancellationToken token) => throw new NotSupportedException();
        private sealed class Lease : ISourceArtifactPublicationLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
