using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using System.Runtime.InteropServices;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class SourceCaptureLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_capture_keeps_its_actual_read_handle_until_reference_publication_finishes(bool existingBlob)
    {
        using var source = new CloudSourceFixture();
        var path = source.File("available.txt", "Verified source content.");
        using var storage = new CloudSourceFixture();
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        var snapshot = await SnapshotAsync(source.Root);
        if (existingBlob)
        {
            var initial = await artifacts.PutFileAsync(snapshot with { ScanOwnership = null }, Metadata(snapshot), default);
            if (initial.PublicationLease is not null) await initial.PublicationLease.DisposeAsync();
        }

        var receipt = await artifacts.PutFileAsync(snapshot, Metadata(snapshot), default);
        Assert.Equal(existingBlob, receipt.ExistingArtifact);
        try
        {
            var capture = Assert.IsAssignableFrom<ISourceCaptureLease>(receipt.PublicationLease);
            Assert.Same(snapshot, capture.Snapshot);
            capture.ValidateCurrent();
            Assert.True(capture.HasSingleLink);
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        }
        finally
        {
            if (receipt.PublicationLease is not null) await receipt.PublicationLease.DisposeAsync();
        }
        using var writable = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.True(writable.CanWrite);
        await receipt.PublicationLease!.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => ((ISourceCaptureLease)receipt.PublicationLease).ValidateCurrent());
    }

    [Fact]
    public async Task A_hard_link_created_after_capture_withdraws_single_path_authority()
    {
        using var source = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "available.txt");
        await File.WriteAllTextAsync(path, "Ordinary local file.");
        using var storage = new CloudSourceFixture();
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        var snapshot = await SnapshotAsync(source.Root);
        var receipt = await artifacts.PutFileAsync(snapshot, Metadata(snapshot), default);
        try
        {
            var capture = Assert.IsAssignableFrom<ISourceCaptureLease>(receipt.PublicationLease);
            Assert.True(capture.HasSingleLink);
            Assert.True(CreateHardLink(Path.Combine(source.Root, "alias.txt"), path, IntPtr.Zero),
                $"Disposable hard-link creation failed: {Marshal.GetLastPInvokeError()}.");
            capture.ValidateCurrent();
            Assert.False(capture.HasSingleLink);
        }
        finally { await receipt.PublicationLease!.DisposeAsync(); }
    }

    [Fact]
    public async Task Locally_available_content_becoming_offline_invalidates_the_held_capture_without_hydration()
    {
        using var source = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "available.txt");
        await File.WriteAllTextAsync(path, "Local metadata change.");
        using var storage = new CloudSourceFixture();
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        var snapshot = await SnapshotAsync(source.Root);
        var receipt = await artifacts.PutFileAsync(snapshot, Metadata(snapshot), default);
        try
        {
            var capture = Assert.IsAssignableFrom<ISourceCaptureLease>(receipt.PublicationLease);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
            Assert.Throws<SourceContentUnavailableException>(capture.ValidateCurrent);
        }
        finally { await receipt.PublicationLease!.DisposeAsync(); }
    }

    [Fact]
    public async Task Cancelled_capture_releases_the_source_instead_of_leaking_a_file_lock()
    {
        using var source = new CloudSourceFixture();
        var path = source.File("available.txt", "Cancellation source.");
        using var storage = new CloudSourceFixture();
        using var cancellation = new CancellationTokenSource();
        var gate = new RecordingGate();
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root,
            beforeSourceRead: _ => { cancellation.Cancel(); return ValueTask.CompletedTask; }, publicationGate: gate);
        var snapshot = await SnapshotAsync(source.Root);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => artifacts.PutFileAsync(
            snapshot, Metadata(snapshot), cancellation.Token).AsTask());

        using var writable = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.True(writable.CanWrite);
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(1, gate.Releases);
    }

    private static SourceArtifactMetadata Metadata(SourceDiscoveredFile snapshot) =>
        new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength);

    private static async Task<SourceDiscoveredFile> SnapshotAsync(string root)
    {
        var files = new List<SourceDiscoveredFile>();
        await foreach (var file in new LocalSourceEnumerator().EnumerateAsync(
            SourceRootConfiguration.Create(root, "Owned capture", false, false, 1024), default)) files.Add(file);
        return Assert.Single(files) with
        {
            ScanOwnership = new(SourceScanRequestId.New(), new(Guid.NewGuid(), "capture-test", 1))
        };
    }

    private sealed class RecordingGate : ISourceArtifactPublicationGate
    {
        public int Releases { get; private set; }
        public ValueTask<ISourceArtifactPublicationLease> AcquireSharedAsync(string hash, CancellationToken token) =>
            ValueTask.FromResult<ISourceArtifactPublicationLease>(new Lease(this));
        public ValueTask<ISourceArtifactPublicationLease?> TryAcquireExclusiveAsync(string hash, CancellationToken token) =>
            throw new NotSupportedException();
        private sealed class Lease(RecordingGate owner) : ISourceArtifactPublicationLease
        {
            public ValueTask DisposeAsync() { owner.Releases++; return ValueTask.CompletedTask; }
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);
#pragma warning restore SYSLIB1054
}
