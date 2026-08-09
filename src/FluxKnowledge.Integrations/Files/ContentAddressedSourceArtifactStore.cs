using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Integrations.Files;

public sealed class ContentAddressedSourceArtifactStore : ISourceArtifactStore
{
    private readonly string _root;
    private readonly PhysicalDirectoryIdentity _rootIdentity;
    private readonly PhysicalDirectoryLease _rootLease;
    private readonly Func<CancellationToken, ValueTask>? _beforeSourceRead;
    private readonly Func<CancellationToken, ValueTask>? _beforeArtifactWrite;
    private readonly Action? _beforeShardCreation;

    public ContentAddressedSourceArtifactStore(
        string configuredRoot,
        IEnumerable<string>? protectedRoots = null,
        Func<CancellationToken, ValueTask>? beforeSourceRead = null,
        Func<CancellationToken, ValueTask>? beforeArtifactWrite = null,
        Action? beforeShardCreation = null)
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
    }

    public async ValueTask<SourceArtifactReceipt> PutFileAsync(
        SourceDiscoveredFile snapshot,
        SourceArtifactMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.ByteLength != snapshot.ByteLength || !string.Equals(metadata.ContentSha256, snapshot.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact metadata does not match the source snapshot.");
        }

        EnsureRootCurrent();
        EnsureSnapshotCurrent(snapshot);
        var hash = snapshot.ContentSha256.ToLowerInvariant();
        var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        using var destination = EnsureArtifactDestinationDirectory(hash);
        var finalPath = Path.Combine(destination.Path, $"{hash}.bin");
        await RunBeforeArtifactWriteAsync(destination, cancellationToken).ConfigureAwait(false);
        if (File.Exists(finalPath))
        {
            var current = await CopyAndHashAsync(snapshot, temporaryPath: null, cancellationToken).ConfigureAwait(false);
            if (current.Length != snapshot.ByteLength || !string.Equals(current.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceSnapshotChangedException("The source changed while its retained artifact was being verified.");
            }

            EnsureSnapshotCurrent(snapshot);
            await VerifyExistingAsync(finalPath, hash, snapshot.ByteLength, cancellationToken).ConfigureAwait(false);
            return Receipt(metadata, hash, relativePath, existing: true);
        }

        var temporaryPath = Path.Combine(Path.GetDirectoryName(finalPath)!, $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            var result = await CopyAndHashAsync(snapshot, temporaryPath, cancellationToken).ConfigureAwait(false);
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
                return Receipt(metadata, hash, relativePath, existing: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyExistingAsync(finalPath, hash, snapshot.ByteLength, cancellationToken).ConfigureAwait(false);
                return Receipt(metadata, hash, relativePath, existing: true);
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
        using var destination = EnsureArtifactDestinationDirectory(actualHash);
        var finalPath = Path.Combine(destination.Path, $"{actualHash}.bin");
        await RunBeforeArtifactWriteAsync(destination, cancellationToken).ConfigureAwait(false);
        if (File.Exists(finalPath))
        {
            await VerifyExistingAsync(finalPath, actualHash, content.Length, cancellationToken).ConfigureAwait(false);
            return Receipt(metadata, actualHash, relativePath, existing: true);
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
                return Receipt(metadata, actualHash, relativePath, existing: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                await VerifyExistingAsync(finalPath, actualHash, content.Length, cancellationToken).ConfigureAwait(false);
                return Receipt(metadata, actualHash, relativePath, existing: true);
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

    private static SourceArtifactReceipt Receipt(
        SourceArtifactMetadata metadata,
        string hash,
        string relativePath,
        bool existing) =>
        new(SourceArtifactId.New(), hash, relativePath, metadata.ByteLength, existing);

    private static void EnsureSnapshotCurrent(SourceDiscoveredFile snapshot)
    {
        var info = new FileInfo(snapshot.CanonicalPath);
        info.Refresh();
        if (!info.Exists || info.Length != snapshot.ByteLength || info.LastWriteTimeUtc != snapshot.LastWriteAtUtc ||
            !string.Equals(PhysicalFileIdentity.Get(snapshot.CanonicalPath), snapshot.StableSourceIdentity, StringComparison.Ordinal))
        {
            throw new SourceSnapshotChangedException("The source changed after discovery.");
        }
    }

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
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var length = 0L;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(snapshot.CanonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!string.Equals(PhysicalFileIdentity.Get(input.SafeFileHandle), snapshot.StableSourceIdentity, StringComparison.Ordinal))
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
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            if (output is not null)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            length += read;
        }
        if (output is not null)
        {
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }

    private static async Task VerifyExistingAsync(
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
        var actualHash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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

        var physicalRoot = PhysicalFileIdentity.GetProjectedPhysicalPath(root);
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

            var physicalProtectedRoot = PhysicalFileIdentity.GetProjectedPhysicalPath(canonicalProtectedRoot);
            if (Overlaps(root, canonicalProtectedRoot) ||
                Overlaps(physicalRoot, physicalProtectedRoot))
            {
                throw new ArgumentException("Artifact storage root cannot overlap a deployment, SQL or derived-index root.", nameof(configuredRoot));
            }
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
}
