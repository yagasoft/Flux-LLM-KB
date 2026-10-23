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
    [InlineData(0, true, 1024)]
    [InlineData(16384, true, 1024)]
    [InlineData(16384, true, 67108864)]
    [InlineData(33554432, true, 67108864)]
    [InlineData(33554433, true, 67108864)]
    [InlineData(0, false, 1024)]
    [InlineData(16384, false, 67108864)]
    [InlineData(33554432, false, 67108864)]
    [InlineData(33554433, false, 67108864)]
    public async Task Discovery_capture_reads_existing_original_once_and_streams_missing_blobs(int length, bool existing, long maximumFileBytes)
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "capture.txt");
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        await File.WriteAllBytesAsync(path, bytes);
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, maximumFileBytes);
        var ownership = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "capture-test", 1));
        long discoveryReads = 0, copyReads = 0, retainedReads = 0;
        var enumerator = new LocalSourceEnumerator { SourceBytesRead = count => discoveryReads += count };
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root)
        {
            SourceBytesRead = count => copyReads += count,
            RetainedBytesRead = count => retainedReads += count
        };
        if (existing)
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            _ = await artifacts.PutAsync(bytes, new(hash, "text/plain", length), default);
            retainedReads = 0;
        }

        await using var iterator = enumerator.EnumerateCapturedAsync(root, ownership, default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var capture = iterator.Current;
        var snapshot = capture.Snapshot;
        Assert.Same(ownership, snapshot.ScanOwnership);
        var receipt = await artifacts.PutCapturedFileAsync(capture, Metadata(snapshot), default);
        try
        {
            Assert.Same(snapshot, Assert.IsAssignableFrom<ISourceCaptureLease>(receipt.PublicationLease).Snapshot);
            Assert.Equal(existing, receipt.ExistingArtifact);
            Assert.Equal(length, discoveryReads);
            Assert.Equal(existing ? 0L : length, copyReads);
            Assert.Equal(length, retainedReads);
            Assert.Equal(length <= Math.Min(maximumFileBytes, 33554432) ? length : Math.Min(length, 8192), snapshot.ClassificationBuffer.Length);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(storage.Root, receipt.StoreRelativePath)));
            Assert.Throws<IOException>(() => { using var writable = File.OpenWrite(path); });
            Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { await receipt.PublicationLease!.DisposeAsync(); }
        using (var writable = File.OpenWrite(path)) Assert.True(writable.CanWrite);
        Assert.False(await iterator.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_at_the_last_discovery_read_does_not_yield_a_live_capture()
    {
        using var source = new CloudSourceFixture();
        var path = source.File("cancel.txt", "Cancel after this complete read.");
        using var cancellation = new CancellationTokenSource();
        var enumerator = new LocalSourceEnumerator { SourceBytesRead = _ => cancellation.Cancel() };
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        var ownership = new SourceScanOwnership(SourceScanRequestId.New(), new(Guid.NewGuid(), "capture-test", 1));
        await using (var iterator = enumerator.EnumerateCapturedAsync(root, ownership, cancellation.Token).GetAsyncEnumerator())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_is_single_use_and_releases_the_source_even_if_publication_disposal_fails(bool failDisposal)
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = source.File("one.txt", "One capture, one receipt.");
        var gate = new RecordingGate { FailRelease = failDisposal };
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root, publicationGate: gate);
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        await using var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var capture = iterator.Current;
        var receipt = await artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default).AsTask());
        if (failDisposal) await Assert.ThrowsAsync<IOException>(() => receipt.PublicationLease!.DisposeAsync().AsTask());
        else await receipt.PublicationLease!.DisposeAsync();
        await receipt.PublicationLease!.DisposeAsync();
        Assert.Equal(1, gate.Releases);
        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default).AsTask());
    }

    [Fact]
    public async Task Existing_retained_corruption_is_rejected_without_overwrite_or_a_leaked_capture()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = source.File("corrupt.txt", "Original evidence.");
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        await using var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var capture = iterator.Current;
        var original = "Original evidence."u8.ToArray();
        var seed = await artifacts.PutAsync(original, Metadata(capture.Snapshot), default);
        var retainedPath = Path.Combine(storage.Root, seed.StoreRelativePath);
        var corrupt = Enumerable.Repeat((byte)'?', original.Length).ToArray();
        await File.WriteAllBytesAsync(retainedPath, corrupt);

        await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default).AsTask());

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(retainedPath));
        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task Classification_bytes_are_not_authority_and_new_capture_detects_a_timestamp_restored_edit()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = source.File("same.txt", "before");
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        string originalHash;
        DateTimeOffset originalTime;
        await using (var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            var capture = iterator.Current;
            originalHash = capture.Snapshot.ContentSha256;
            originalTime = capture.Snapshot.LastWriteAtUtc;
            Array.Fill(capture.Snapshot.ClassificationBuffer, (byte)'?');
            var receipt = await artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default);
            try { Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(storage.Root, receipt.StoreRelativePath))); }
            finally { await receipt.PublicationLease!.DisposeAsync(); }
        }
        await File.WriteAllTextAsync(path, "after!");
        File.SetLastWriteTimeUtc(path, originalTime.UtcDateTime);
        await using var next = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator();
        Assert.True(await next.MoveNextAsync());
        Assert.NotEqual(originalHash, next.Current.Snapshot.ContentSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abandoning_or_throwing_from_capture_enumeration_releases_the_current_source(bool throwFromConsumer)
    {
        using var source = new CloudSourceFixture();
        var path = source.File("stop.txt", "Consumer stops.");
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        async Task ConsumeAsync()
        {
            await foreach (var capture in new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default))
            {
                Assert.Throws<IOException>(() => { using var writable = File.OpenWrite(path); });
                if (throwFromConsumer) throw new InvalidOperationException("Consumer failure");
                break;
            }
        }
        if (throwFromConsumer) await Assert.ThrowsAsync<InvalidOperationException>(ConsumeAsync);
        else await ConsumeAsync();
        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_retained_verification_or_streaming_copy_releases_capture_and_temporary_files(bool existing)
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = source.File("cancel.txt", new string('x', 262144));
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        using var cancellation = new CancellationTokenSource();
        using var seedStore = new ContentAddressedSourceArtifactStore(storage.Root);
        await using var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var capture = iterator.Current;
        if (existing) _ = await seedStore.PutAsync(await File.ReadAllBytesAsync(path), Metadata(capture.Snapshot), default);
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root)
        {
            SourceBytesRead = _ => cancellation.Cancel(),
            RetainedBytesRead = _ => cancellation.Cancel()
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), cancellation.Token).AsTask());

        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.tmp", SearchOption.AllDirectories));
    }

    private static SourceScanOwnership Ownership() => new(SourceScanRequestId.New(), new(Guid.NewGuid(), "capture-test", 1));

    [Fact]
    public async Task Public_or_disposed_captures_and_mismatched_metadata_cannot_publish_a_receipt()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = source.File("proof.txt", "Physical proof.");
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        await using var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var capture = iterator.Current;
        await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.PutCapturedFileAsync(
            new ForgedCapture(capture.Snapshot), Metadata(capture.Snapshot), default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.PutCapturedFileAsync(
            capture, Metadata(capture.Snapshot) with { ByteLength = 1 }, default).AsTask());
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.bin", SearchOption.AllDirectories));
        await capture.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default).AsTask());
        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("mtime")]
    [InlineData("link")]
    public async Task Discovery_receipt_revalidates_current_source_metadata_and_single_link_authority(string change)
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = Path.Combine(source.Root, "current.txt");
        await File.WriteAllTextAsync(path, "Current physical evidence.");
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root);
        await using var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var capture = iterator.Current;
        var receipt = await artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default);
        try
        {
            var verified = Assert.IsAssignableFrom<ISourceCaptureLease>(receipt.PublicationLease);
            if (change == "offline")
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
                Assert.Throws<SourceContentUnavailableException>(verified.ValidateCurrent);
            }
            else if (change == "mtime")
            {
                File.SetLastWriteTimeUtc(path, capture.Snapshot.LastWriteAtUtc.AddSeconds(1).UtcDateTime);
                Assert.Throws<SourceSnapshotChangedException>(verified.ValidateCurrent);
            }
            else
            {
                Assert.True(CreateHardLink(Path.Combine(source.Root, "alias.txt"), path, IntPtr.Zero),
                    $"Disposable hard-link creation failed: {Marshal.GetLastPInvokeError()}.");
                verified.ValidateCurrent();
                Assert.False(verified.HasSingleLink);
            }
        }
        finally { await receipt.PublicationLease!.DisposeAsync(); }
    }

    private sealed class ForgedCapture(SourceDiscoveredFile snapshot) : ISourceFileCapture
    {
        public SourceDiscoveredFile Snapshot => snapshot;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_retention_cannot_reuse_a_capture_waiting_for_publication()
    {
        using var source = new CloudSourceFixture();
        using var storage = new CloudSourceFixture();
        var path = source.File("concurrent.txt", "One physical read cursor.");
        var root = SourceRootConfiguration.Create(source.Root, "capture", false, false, 1024);
        var gate = new WaitingGate();
        using var cancellation = new CancellationTokenSource();
        using var artifacts = new ContentAddressedSourceArtifactStore(storage.Root, publicationGate: gate);
        await using (var iterator = new LocalSourceEnumerator().EnumerateCapturedAsync(root, Ownership(), default).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            var capture = iterator.Current;
            var first = artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), cancellation.Token).AsTask();
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(() => artifacts.PutCapturedFileAsync(capture, Metadata(capture.Snapshot), default).AsTask());
            }
            finally { cancellation.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        using var writable = File.OpenWrite(path);
        Assert.True(writable.CanWrite);
        Assert.Empty(Directory.EnumerateFiles(storage.Root, "*.bin", SearchOption.AllDirectories));
    }

    private sealed class WaitingGate : ISourceArtifactPublicationGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ISourceArtifactPublicationLease> AcquireSharedAsync(string hash, CancellationToken token)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable cancelled wait.");
        }
        public ValueTask<ISourceArtifactPublicationLease?> TryAcquireExclusiveAsync(string hash, CancellationToken token) =>
            throw new NotSupportedException();
    }

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
        public bool FailRelease { get; init; }
        public ValueTask<ISourceArtifactPublicationLease> AcquireSharedAsync(string hash, CancellationToken token) =>
            ValueTask.FromResult<ISourceArtifactPublicationLease>(new Lease(this));
        public ValueTask<ISourceArtifactPublicationLease?> TryAcquireExclusiveAsync(string hash, CancellationToken token) =>
            throw new NotSupportedException();
        private sealed class Lease(RecordingGate owner) : ISourceArtifactPublicationLease
        {
            public ValueTask DisposeAsync()
            {
                owner.Releases++;
                if (owner.FailRelease) throw new IOException("Injected publication disposal failure.");
                return ValueTask.CompletedTask;
            }
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);
#pragma warning restore SYSLIB1054
}
