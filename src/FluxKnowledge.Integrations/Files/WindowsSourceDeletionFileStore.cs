using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Files;

/// <summary>
/// Deletes only SQL-captured retained blobs or generation directories through pinned,
/// handle-relative Windows operations. A malformed or replaced target is never removed.
/// </summary>
public sealed class WindowsSourceDeletionFileStore(string artifactRoot, string indexRoot) : ISourceDeletionFileStore
{
    private readonly string _artifactRoot = CanonicalLocalDirectory(artifactRoot);
    private readonly string _indexRoot = CanonicalLocalDirectory(indexRoot);
    private readonly HandleRelativeNativeFileSystem _fileSystem = new();

    public async ValueTask<SourceDeletionFileResult> DeleteAsync(
        SourceDeletionFileTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.StorageKind switch
        {
            1 => await DeleteArtifactAsync(target, cancellationToken).ConfigureAwait(false),
            2 => await DeleteGenerationAsync(target, cancellationToken).ConfigureAwait(false),
            _ => new SourceDeletionFileResult(false, "source-delete-storage-kind-invalid")
        };
    }

    private async ValueTask<SourceDeletionFileResult> DeleteArtifactAsync(
        SourceDeletionFileTarget target,
        CancellationToken cancellationToken)
    {
        if (target.ContentSha256 is not { Length: 64 } hash || target.ByteLength is not { } byteLength ||
            byteLength < 0 || !IsLowerHex(hash) ||
            !string.Equals(target.RelativePath, Path.Combine("sha256", hash[..2], $"{hash}.bin"), StringComparison.Ordinal))
        {
            return new SourceDeletionFileResult(false, "source-delete-artifact-path-invalid");
        }

        try
        {
            using var root = _fileSystem.OpenReadOnlyDirectory(_artifactRoot);
            using var sha256 = _fileSystem.OpenReadOnlyDirectory(root, "sha256");
            using var shard = _fileSystem.OpenReadOnlyDirectory(sha256, hash[..2]);
            var file = await _fileSystem.ReadLiteralFileAsync(shard, $"{hash}.bin", cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                return new SourceDeletionFileResult(true);
            }

            if (file.Content.LongLength != byteLength || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(file.Content), Convert.FromHexString(hash)))
            {
                return new SourceDeletionFileResult(false, "source-delete-artifact-binding-invalid");
            }

            var deleted = await _fileSystem.DeleteLiteralChildAsync(shard, $"{hash}.bin", file.Identity, cancellationToken).ConfigureAwait(false);
            return deleted.Changed
                ? new SourceDeletionFileResult(true)
                : new SourceDeletionFileResult(false, deleted.Reason ?? "source-delete-artifact-delete-failed");
        }
        catch (FileNotFoundException)
        {
            return new SourceDeletionFileResult(true);
        }
        catch (DirectoryNotFoundException)
        {
            return new SourceDeletionFileResult(true);
        }
        catch (UnauthorizedAccessException)
        {
            return new SourceDeletionFileResult(false, "source-delete-path-unsafe");
        }
        catch (IOException)
        {
            return new SourceDeletionFileResult(false, "source-delete-artifact-io-failed");
        }
    }

    private async ValueTask<SourceDeletionFileResult> DeleteGenerationAsync(
        SourceDeletionFileTarget target,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(target.RelativePath, "N", out var generationId))
        {
            return new SourceDeletionFileResult(false, "source-delete-index-path-invalid");
        }

        try
        {
            using var root = _fileSystem.OpenReadOnlyDirectory(_indexRoot);
            using var generations = _fileSystem.OpenReadOnlyDirectory(root, "generations");
            var name = generationId.ToString("N");
            var child = _fileSystem.InspectLiteralChild(generations, name);
            if (!child.IsDirectory)
            {
                return new SourceDeletionFileResult(false, "source-delete-index-path-invalid");
            }

            using (var generation = _fileSystem.OpenDirectory(generations, name))
            {
                if (generation.Identity != child.Identity)
                {
                    return new SourceDeletionFileResult(false, "source-delete-index-identity-changed");
                }

                var contents = await _fileSystem.DeleteTreeContentsAsync(generation, cancellationToken).ConfigureAwait(false);
                if (!contents.Changed)
                {
                    return new SourceDeletionFileResult(false, contents.Reason ?? "source-delete-index-delete-failed");
                }
            }

            var deleted = await _fileSystem.DeleteLiteralChildAsync(generations, name, child.Identity, cancellationToken).ConfigureAwait(false);
            return deleted.Changed
                ? new SourceDeletionFileResult(true)
                : new SourceDeletionFileResult(false, deleted.Reason ?? "source-delete-index-delete-failed");
        }
        catch (FileNotFoundException)
        {
            return new SourceDeletionFileResult(true);
        }
        catch (DirectoryNotFoundException)
        {
            return new SourceDeletionFileResult(true);
        }
        catch (UnauthorizedAccessException)
        {
            return new SourceDeletionFileResult(false, "source-delete-path-unsafe");
        }
        catch (IOException)
        {
            return new SourceDeletionFileResult(false, "source-delete-index-io-failed");
        }
    }

    private static string CanonicalLocalDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(canonical) || canonical.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("A local absolute storage root is required.", nameof(path));
        }

        return canonical;
    }

    private static bool IsLowerHex(string value) => value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
