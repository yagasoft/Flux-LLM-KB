using System.Buffers;
using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Integrations.Files;

public sealed class ContentAddressedSourceArtifactStore : ICapturedSourceArtifactStore, IDisposable
{
    private readonly string _root;
    private readonly PhysicalDirectoryIdentity _rootIdentity;
    private readonly PhysicalDirectoryLease _rootLease;
    private readonly Func<CancellationToken, ValueTask>? _beforeSourceRead;
    private readonly Func<CancellationToken, ValueTask>? _beforeArtifactWrite;
    private readonly Action? _beforeShardCreation;
    private readonly ISourceArtifactPublicationGate? _publicationGate;
    private readonly ISourceAccessRepairClient? _sourceAccessRepairClient;
    internal Action<int>? SourceBytesRead { get; init; }
    internal Action<int>? RetainedBytesRead { get; init; }

    public ContentAddressedSourceArtifactStore(
        string configuredRoot,
        IEnumerable<string>? protectedRoots = null,
        Func<CancellationToken, ValueTask>? beforeSourceRead = null,
        Func<CancellationToken, ValueTask>? beforeArtifactWrite = null,
        Action? beforeShardCreation = null,
        ISourceArtifactPublicationGate? publicationGate = null,
        ISourceAccessRepairClient? sourceAccessRepairClient = null)
    {
        var effectiveProtectedRoots = protectedRoots?.ToArray();
        _root = ValidateRoot(configuredRoot, effectiveProtectedRoots);
        Directory.CreateDirectory(_root);
        _ = ValidateRoot(_root, effectiveProtectedRoots);
        _rootLease = PhysicalFileIdentity.OpenDirectoryLease(_root);
        _rootIdentity = _rootLease.Identity;
        _beforeSourceRead = beforeSourceRead;
        _beforeArtifactWrite = beforeArtifactWrite;
        _beforeShardCreation = beforeShardCreation;
        _publicationGate = publicationGate;
        _sourceAccessRepairClient = sourceAccessRepairClient;
    }

    public ValueTask<SourceArtifactReceipt> PutFileAsync(
        SourceDiscoveredFile snapshot,
        SourceArtifactMetadata metadata,
        CancellationToken cancellationToken) => PutFileCoreAsync(snapshot, metadata, cancellationToken, null);

    public ValueTask<SourceArtifactReceipt> PutCapturedFileAsync(ISourceFileCapture capture,
        SourceArtifactMetadata metadata, CancellationToken cancellationToken)
    {
        if (capture is not LocalSourceFileCapture local || local.Snapshot.ScanOwnership is null ||
            local.Snapshot.SourcePath is null || local.Snapshot.GitInventory is not null)
            throw new InvalidDataException("Retention requires an owned live local discovery capture.");
        local.BeginRetention();
        return PutFileCoreAsync(local.Snapshot, metadata, cancellationToken, local);
    }

    private async ValueTask<SourceArtifactReceipt> PutFileCoreAsync(SourceDiscoveredFile snapshot,
        SourceArtifactMetadata metadata, CancellationToken cancellationToken, LocalSourceFileCapture? discovery)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.ByteLength != snapshot.ByteLength || !string.Equals(metadata.ContentSha256, snapshot.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact metadata does not match the source snapshot.");
        }

        EnsureRootCurrent();
        // Recover original-source access before taking any SQL publication fence.
        // Later capture/commit revalidation is strictly observational.
        if (discovery is null)
            EnsureSnapshotCurrent(snapshot, snapshot.SourcePath is null ? null : _sourceAccessRepairClient, cancellationToken);
        else discovery.ValidateCurrent();
        var hash = snapshot.ContentSha256.ToLowerInvariant();
        var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        var publicationLease = await AcquirePublicationLeaseAsync(hash, cancellationToken).ConfigureAwait(false);
        try
        {
        if (discovery is not null) publicationLease = SourceCaptureLease.Adopt(discovery, publicationLease);
        using var destination = EnsureArtifactDestinationDirectory(hash);
        var finalPath = Path.Combine(destination.Path, $"{hash}.bin");
        await RunBeforeArtifactWriteAsync(destination, cancellationToken).ConfigureAwait(false);
        // Only a real owned filesystem capture carries positive per-file authority.
        // Synthetic writes, unowned callers and Git inventory retain their contracts.
        if (discovery is null && snapshot.ScanOwnership is not null && snapshot.SourcePath is not null && snapshot.GitInventory is null)
            publicationLease = SourceCaptureLease.Open(snapshot, publicationLease);
        var capture = publicationLease as SourceCaptureLease;
        if (File.Exists(finalPath))
        {
            if (discovery is null)
            {
                var current = await CopyAndHashAsync(snapshot, temporaryPath: null, cancellationToken, capture).ConfigureAwait(false);
                if (current.Length != snapshot.ByteLength || !string.Equals(current.Hash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new SourceSnapshotChangedException("The source changed while its retained artifact was being verified.");
            }

            EnsureSnapshotCurrent(snapshot);
            await VerifyExistingAsync(finalPath, hash, snapshot.ByteLength, cancellationToken).ConfigureAwait(false);
            return Receipt(metadata, hash, relativePath, existing: true, publicationLease);
        }

        var temporaryPath = Path.Combine(Path.GetDirectoryName(finalPath)!, $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (discovery is not null) discovery.Input.Position = 0;
            var result = await CopyAndHashAsync(snapshot, temporaryPath, cancellationToken, capture).ConfigureAwait(false);
            if (result.Length != snapshot.ByteLength || !string.Equals(result.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceSnapshotChangedException("The source changed while its retained artifact was being written.");
            }

            EnsureSnapshotCurrent(snapshot);
            await VerifyExistingAsync(temporaryPath, hash, snapshot.ByteLength, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureRootCurrent();
                File.Move(temporaryPath, finalPath, overwrite: false);
                return Receipt(metadata, hash, relativePath, existing: false, publicationLease);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyExistingAsync(finalPath, hash, snapshot.ByteLength, cancellationToken).ConfigureAwait(false);
                return Receipt(metadata, hash, relativePath, existing: true, publicationLease);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        }
        catch
        {
            await DisposePublicationLeaseAsync(publicationLease).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SourceArtifactReceipt> PutAsync(
        ReadOnlyMemory<byte> content,
        SourceArtifactMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (content.Length != metadata.ByteLength)
        {
            throw new InvalidDataException("Artifact byte length does not match its metadata.");
        }

        EnsureRootCurrent();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        if (!string.Equals(actualHash, metadata.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact checksum does not match the retained bytes.");
        }

        var relativePath = Path.Combine("sha256", actualHash[..2], $"{actualHash}.bin");
        var publicationLease = await AcquirePublicationLeaseAsync(actualHash, cancellationToken).ConfigureAwait(false);
        try
        {
        using var destination = EnsureArtifactDestinationDirectory(actualHash);
        var finalPath = Path.Combine(destination.Path, $"{actualHash}.bin");
        await RunBeforeArtifactWriteAsync(destination, cancellationToken).ConfigureAwait(false);
        if (File.Exists(finalPath))
        {
            await VerifyExistingAsync(finalPath, actualHash, content.Length, cancellationToken).ConfigureAwait(false);
            return Receipt(metadata, actualHash, relativePath, existing: true, publicationLease);
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{actualHash}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await VerifyExistingAsync(temporaryPath, actualHash, content.Length, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureRootCurrent();
                File.Move(temporaryPath, finalPath, overwrite: false);
                return Receipt(metadata, actualHash, relativePath, existing: false, publicationLease);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyExistingAsync(finalPath, actualHash, content.Length, cancellationToken).ConfigureAwait(false);
                return Receipt(metadata, actualHash, relativePath, existing: true, publicationLease);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        }
        catch
        {
            await DisposePublicationLeaseAsync(publicationLease).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Streams untrusted retained content to a temporary file before atomically binding it to its computed SHA-256 path.</summary>
    public async ValueTask<SourceArtifactReceipt> PutStreamAsync(
        Stream content,
        long maximumByteLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new InvalidDataException("The retained artifact stream is not readable.");
        }
        if (maximumByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumByteLength));
        }

        EnsureRootCurrent();
        var temporaryPath = Path.Combine(_root, $".stream.{Guid.NewGuid():N}.tmp");
        ISourceArtifactPublicationLease? publicationLease = null;
        try
        {
            var streamed = await CopyStreamAndHashAsync(content, temporaryPath, maximumByteLength, cancellationToken).ConfigureAwait(false);
            var metadata = new SourceArtifactMetadata(streamed.Hash, "application/octet-stream", streamed.Length);
            var relativePath = Path.Combine("sha256", streamed.Hash[..2], $"{streamed.Hash}.bin");
            publicationLease = await AcquirePublicationLeaseAsync(streamed.Hash, cancellationToken).ConfigureAwait(false);
            using var destination = EnsureArtifactDestinationDirectory(streamed.Hash);
            var finalPath = Path.Combine(destination.Path, $"{streamed.Hash}.bin");
            await RunBeforeArtifactWriteAsync(destination, cancellationToken).ConfigureAwait(false);
            if (File.Exists(finalPath))
            {
                await VerifyExistingAsync(finalPath, streamed.Hash, streamed.Length, cancellationToken).ConfigureAwait(false);
                return Receipt(metadata, streamed.Hash, relativePath, existing: true, publicationLease);
            }

            await VerifyExistingAsync(temporaryPath, streamed.Hash, streamed.Length, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureRootCurrent();
                File.Move(temporaryPath, finalPath, overwrite: false);
                return Receipt(metadata, streamed.Hash, relativePath, existing: false, publicationLease);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyExistingAsync(finalPath, streamed.Hash, streamed.Length, cancellationToken).ConfigureAwait(false);
                return Receipt(metadata, streamed.Hash, relativePath, existing: true, publicationLease);
            }
        }
        catch
        {
            await DisposePublicationLeaseAsync(publicationLease).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SourceArtifactReceipt Receipt(
        SourceArtifactMetadata metadata,
        string hash,
        string relativePath,
        bool existing,
        ISourceArtifactPublicationLease? publicationLease)
    {
        if (publicationLease is SourceCaptureLease capture) capture.Seal(hash, metadata.ByteLength);
        return new(SourceArtifactId.New(), hash, relativePath, metadata.ByteLength, existing, publicationLease);
    }

    private ValueTask<ISourceArtifactPublicationLease?> AcquirePublicationLeaseAsync(
        string contentSha256,
        CancellationToken cancellationToken) =>
        _publicationGate is null
            ? ValueTask.FromResult<ISourceArtifactPublicationLease?>(null)
            : AcquireConfiguredPublicationLeaseAsync(contentSha256, cancellationToken);

    private async ValueTask<ISourceArtifactPublicationLease?> AcquireConfiguredPublicationLeaseAsync(
        string contentSha256,
        CancellationToken cancellationToken) =>
        await _publicationGate!.AcquireSharedAsync(contentSha256, cancellationToken).ConfigureAwait(false);

    private static async ValueTask DisposePublicationLeaseAsync(ISourceArtifactPublicationLease? publicationLease)
    {
        if (publicationLease is not null)
        {
            await publicationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void EnsureSnapshotCurrent(SourceDiscoveredFile snapshot, ISourceAccessRepairClient? accessRepair = null,
        CancellationToken cancellationToken = default)
    {
        using var source = OpenSource(snapshot, accessRepair, cancellationToken);
        if (RandomAccess.GetLength(source.Handle) != snapshot.ByteLength ||
            File.GetLastWriteTimeUtc(source.Handle) != snapshot.LastWriteAtUtc)
        {
            throw new SourceSnapshotChangedException("The source changed after discovery.");
        }
    }

    private static WindowsSourcePathAccess.SourceReadLease OpenSource(SourceDiscoveredFile snapshot, ISourceAccessRepairClient? accessRepair = null,
        CancellationToken cancellationToken = default) =>
        SourceAccessRecovery.Run(snapshot.SourcePath?.RootPath ?? Path.GetDirectoryName(snapshot.CanonicalPath)!,
            snapshot.SourcePath?.RootIdentityFingerprint, accessRepair, () => WindowsSourcePathAccess.OpenRead(
                snapshot.SourcePath?.RootPath ?? Path.GetDirectoryName(snapshot.CanonicalPath)!, snapshot.CanonicalPath,
                snapshot.SourcePath?.RootIdentityFingerprint, snapshot.StableSourceIdentity), cancellationToken);

    private void EnsureRootCurrent()
    {
        var current = PhysicalFileIdentity.GetDirectory(_root);
        if (!string.Equals(current.CanonicalPath, _rootIdentity.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.IdentityFingerprint, _rootIdentity.IdentityFingerprint, StringComparison.Ordinal))
        {
            throw new IOException("The artifact storage root changed after startup.");
        }
    }

    private ArtifactDestination EnsureArtifactDestinationDirectory(string hash)
    {
        var sha256Directory = Path.Combine(_root, "sha256");
        var destinationDirectory = Path.Combine(sha256Directory, hash[..2]);
        ValidateProjectedContained(sha256Directory);
        ValidateProjectedContained(destinationDirectory);
        Directory.CreateDirectory(sha256Directory);
        ValidateProjectedContained(sha256Directory);
        var sha256Lease = PhysicalFileIdentity.OpenDirectoryLease(sha256Directory);
        try
        {
            ValidateLeaseContained(sha256Lease);
            _beforeShardCreation?.Invoke();
            ValidateProjectedContained(sha256Directory);
            ValidateLeaseContained(sha256Lease);
            ValidateProjectedContained(destinationDirectory);
            Directory.CreateDirectory(destinationDirectory);
            ValidateProjectedContained(destinationDirectory);
            var shardLease = PhysicalFileIdentity.OpenDirectoryLease(destinationDirectory);
            try
            {
                ValidateLeaseContained(shardLease);
                return new ArtifactDestination(destinationDirectory, sha256Lease, shardLease);
            }
            catch
            {
                shardLease.Dispose();
                throw;
            }
        }
        catch
        {
            sha256Lease.Dispose();
            throw;
        }
    }

    private void ValidateProjectedContained(string directory)
    {
        var physical = PhysicalFileIdentity.GetProjectedPhysicalPath(directory);
        if (!IsWithin(_rootIdentity.CanonicalPath, physical))
        {
            throw new IOException("The artifact destination escapes the configured storage root.");
        }
    }

    private void ValidateLeaseContained(PhysicalDirectoryLease lease)
    {
        if (!IsWithin(_rootIdentity.CanonicalPath, lease.Identity.CanonicalPath))
        {
            throw new IOException("The artifact destination escapes the configured storage root.");
        }
    }

    private async ValueTask RunBeforeArtifactWriteAsync(ArtifactDestination destination, CancellationToken cancellationToken)
    {
        EnsureRootCurrent();
        if (_beforeArtifactWrite is not null)
        {
            await _beforeArtifactWrite(cancellationToken).ConfigureAwait(false);
        }
        EnsureRootCurrent();
        destination.ValidateCurrent(this);
    }

    private async Task<(string Hash, long Length)> CopyAndHashAsync(
        SourceDiscoveredFile snapshot,
        string? temporaryPath,
        CancellationToken cancellationToken,
        SourceCaptureLease? capture = null)
    {
        var buffer = new byte[128 * 1024];
        var length = 0L;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var ownedSource = capture is null ? OpenSource(snapshot) : null;
        await using var ownedInput = capture is null
            ? new FileStream(ownedSource!.Handle, FileAccess.Read, buffer.Length, isAsync: true) : null;
        var source = capture?.Source ?? ownedSource!;
        var input = capture?.Input ?? ownedInput!;
        if (input.Length != snapshot.ByteLength || File.GetLastWriteTimeUtc(source.Handle) != snapshot.LastWriteAtUtc)
        {
            throw new SourceSnapshotChangedException("The source identity changed after discovery.");
        }

        if (_beforeSourceRead is not null)
        {
            await _beforeSourceRead(cancellationToken).ConfigureAwait(false);
        }

        await using var output = temporaryPath is null
            ? null
            : new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.WriteThrough);
        while (length < snapshot.ByteLength)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, snapshot.ByteLength - length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new SourceSnapshotChangedException("The source was truncated while being retained.");
            SourceBytesRead?.Invoke(read);
            hash.AppendData(buffer, 0, read);
            if (output is not null)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            length += read;
        }
        WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(source.Handle));
        if (input.Length != snapshot.ByteLength || File.GetLastWriteTimeUtc(source.Handle) != snapshot.LastWriteAtUtc)
            throw new SourceSnapshotChangedException("The source changed while being retained.");
        if (output is not null)
        {
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }

    private static async Task<(string Hash, long Length)> CopyStreamAndHashAsync(
        Stream content,
        string temporaryPath,
        long maximumByteLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var length = 0L;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (read > maximumByteLength - length)
            {
                throw new InvalidDataException("The retained artifact stream exceeds its configured byte limit.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length += read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }

    private async Task VerifyExistingAsync(
        string path,
        string expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedLength)
        {
            throw new InvalidDataException("The existing artifact has an unexpected byte length.");
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, 81920), cancellationToken).ConfigureAwait(false)) != 0)
            {
                RetainedBytesRead?.Invoke(read);
                hash.AppendData(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The existing artifact checksum is invalid.");
        }
    }

    public static string ValidateRoot(string configuredRoot, IEnumerable<string>? protectedRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Artifact storage root must be a non-root absolute directory.", nameof(configuredRoot));
        }

        _ = PhysicalFileIdentity.GetProjectedPhysicalPath(root);
        foreach (var protectedRoot in protectedRoots ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(protectedRoot))
            {
                continue;
            }

            var canonicalProtectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (Overlaps(root, canonicalProtectedRoot))
            {
                throw new ArgumentException("Artifact storage root cannot overlap a deployment, SQL or derived-index root.", nameof(configuredRoot));
            }

            // Protected roots may intentionally deny the web identity all handle access
            // (for example SQL-owned data and log directories). The artifact root itself
            // is still physically verified above and rejects reparse traversal.

        }

        return root;
    }

    private static bool Overlaps(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    private static bool IsWithin(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class ArtifactDestination(string path, PhysicalDirectoryLease sha256Lease, PhysicalDirectoryLease shardLease) : IDisposable
    {
        public string Path { get; } = path;

        public void ValidateCurrent(ContentAddressedSourceArtifactStore store)
        {
            store.ValidateProjectedContained(System.IO.Path.GetDirectoryName(Path)!);
            store.ValidateProjectedContained(Path);
            store.ValidateLeaseContained(sha256Lease);
            store.ValidateLeaseContained(shardLease);
        }

        public void Dispose()
        {
            shardLease.Dispose();
            sha256Lease.Dispose();
        }
    }

    public void Dispose() => _rootLease.Dispose();

    private sealed class SourceCaptureLease : ISourceCaptureLease
    {
        private readonly ISourceArtifactPublicationLease? _publication;
        private readonly LocalSourceFileCapture? _discovery;
        private int _disposed;
        private bool _verified;
        public SourceDiscoveredFile Snapshot { get; }
        internal WindowsSourcePathAccess.SourceReadLease Source { get; }
        internal FileStream Input { get; }

        private SourceCaptureLease(SourceDiscoveredFile snapshot, WindowsSourcePathAccess.SourceReadLease source,
            FileStream input, ISourceArtifactPublicationLease? publication, LocalSourceFileCapture? discovery = null)
        {
            Snapshot = snapshot; Source = source; Input = input; _publication = publication; _discovery = discovery;
        }

        internal static SourceCaptureLease Adopt(LocalSourceFileCapture discovery, ISourceArtifactPublicationLease? publication) =>
            new(discovery.Snapshot, discovery.Source, discovery.Input, publication, discovery);

        internal static SourceCaptureLease Open(SourceDiscoveredFile snapshot, ISourceArtifactPublicationLease? publication)
        {
            var source = OpenSource(snapshot);
            try { return new(snapshot, source, new FileStream(source.Handle, FileAccess.Read, 128 * 1024, isAsync: true), publication); }
            catch { source.Dispose(); throw; }
        }

        internal void Seal(string hash, long length)
        {
            if (Snapshot.ContentSha256 != hash || Snapshot.ByteLength != length)
                throw new InvalidDataException("Verified capture and retained artifact differ.");
            _verified = true;
            ValidateCurrent();
        }

        public bool HasSingleLink
        {
            get { ValidateCurrent(); return PhysicalFileIdentity.GetLinkCount(Source.Handle) == 1; }
        }

        public void ValidateCurrent()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_verified) throw new InvalidOperationException("The physical capture is not yet verified.");
            if (_discovery is not null) _discovery.ValidateCurrent();
            else LocalSourceFileCapture.ValidateSourceCurrent(Snapshot, Source);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (_discovery is not null) await _discovery.DisposeAsync().ConfigureAwait(false);
                else
                {
                    try { await Input.DisposeAsync().ConfigureAwait(false); }
                    finally { Source.Dispose(); }
                }
            }
            finally
            {
                if (_publication is not null) await _publication.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
