using System.Security.Cryptography;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Reads one bounded file through a no-follow handle after proving its final parent identity.</summary>
public static class ContainedFileReader
{
    public static async Task<VerifiedContainedFile> ReadAsync(
        string root,
        string relativePath,
        long maximumBytes,
        CancellationToken cancellationToken,
        string? expectedSha256 = null,
        long? expectedLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (maximumBytes < 0 || maximumBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("The contained file path is invalid.");
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidatePath = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        if (!IsWithin(canonicalRoot, candidatePath))
        {
            throw new InvalidDataException("The contained file path escapes its root.");
        }

        PhysicalFileIdentity.EnsureNoReparsePointTraversal(canonicalRoot);
        using var rootLease = PhysicalFileIdentity.OpenDirectoryLease(canonicalRoot);
        var parentPath = Path.GetDirectoryName(candidatePath)
            ?? throw new InvalidDataException("The contained file has no parent directory.");
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(parentPath);
        using var parentLease = PhysicalFileIdentity.OpenDirectoryLease(parentPath);
        if (!IsWithin(rootLease.Identity.CanonicalPath, parentLease.Identity.CanonicalPath))
        {
            throw new InvalidDataException("The contained file parent escapes its leased root.");
        }

        using var handle = PhysicalFileIdentity.OpenReadNoFollow(candidatePath);
        var finalPath = PhysicalFileIdentity.GetFinalPath(handle);
        var finalParentPath = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidDataException("The contained file final path has no parent directory.");
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(finalParentPath);
        var finalParent = PhysicalFileIdentity.GetDirectory(finalParentPath);
        if (!string.Equals(finalParent.IdentityFingerprint, parentLease.Identity.IdentityFingerprint, StringComparison.Ordinal) ||
            !string.Equals(finalPath, Path.Combine(parentLease.Identity.CanonicalPath, Path.GetFileName(candidatePath)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The contained file does not belong to its leased parent.");
        }

        await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 81920, isAsync: true);
        if (stream.Length < 0 || stream.Length > maximumBytes ||
            (expectedLength is not null && stream.Length != expectedLength.Value))
        {
            throw new InvalidDataException("The contained file has an unexpected byte length.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("The contained file ended before its recorded length.");
            }
            offset += read;
        }
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The contained file grew while it was read.");
        }
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expectedSha256 is not null && !string.Equals(sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The contained file checksum is invalid.");
        }

        var currentRoot = PhysicalFileIdentity.GetDirectory(canonicalRoot);
        if (!string.Equals(currentRoot.IdentityFingerprint, rootLease.Identity.IdentityFingerprint, StringComparison.Ordinal))
        {
            throw new IOException("The contained file root changed while it was read.");
        }
        return new VerifiedContainedFile(bytes, sha256, bytes.LongLength);
    }

    private static bool IsWithin(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record VerifiedContainedFile(byte[] Bytes, string ContentSha256, long ByteLength);
