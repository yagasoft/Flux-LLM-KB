using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Deterministic root crawl. Reparse points are reported but never traversed.</summary>
public sealed class LocalSourceEnumerator : ISourceFileEnumerator
{
    private readonly Func<SafeFileHandle, string> _readIdentity;
    private IReadOnlyList<SourceEnumerationEvidence> _lastEvidence = [];

    public LocalSourceEnumerator(Func<SafeFileHandle, string>? readIdentity = null) =>
        _readIdentity = readIdentity ?? PhysicalFileIdentity.Get;

    public IReadOnlyList<SourceEnumerationEvidence> LastEvidence => _lastEvidence;

    public async IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
        SourceRootConfiguration sourceRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        _lastEvidence = [];
        var errors = new List<string>();
        if (!TryRevalidateRoot(sourceRoot, errors))
        {
            _lastEvidence = errors.Select(ParseEvidence).ToArray();
            yield break;
        }
        var candidates = new List<string>();
        CollectFiles(sourceRoot, sourceRoot.CanonicalPath, candidates, errors);
        foreach (var path in candidates.OrderBy(path => Path.GetRelativePath(sourceRoot.CanonicalPath, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var relativePath = Path.GetRelativePath(sourceRoot.CanonicalPath, path);
            if (!MatchesPolicy(relativePath, sourceRoot.IncludePatterns, sourceRoot.ExcludePatterns))
            {
                continue;
            }

            byte[] classificationBuffer;
            bool hasFullBoundedBuffer;
            string contentHash;
            string stableIdentity;
            try
            {
                var snapshot = await ReadSnapshotAsync(path, sourceRoot.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
                classificationBuffer = snapshot.Buffer;
                hasFullBoundedBuffer = snapshot.HasFullBuffer;
                contentHash = snapshot.Hash;
                stableIdentity = snapshot.StableIdentity;
                info.Refresh();
                if (snapshot.Length != info.Length || snapshot.LastWriteAtUtc != info.LastWriteTimeUtc ||
                    !string.Equals(stableIdentity, PhysicalFileIdentity.Get(path), StringComparison.Ordinal))
                {
                    errors.Add($"changed:{relativePath}:SourceChangedDuringDiscovery");
                    continue;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                errors.Add($"permission:{relativePath}:{exception.GetType().Name}");
                continue;
            }
            catch (IOException exception)
            {
                errors.Add($"io:{relativePath}:{exception.GetType().Name}");
                continue;
            }

            var classification = SourceClassifier.Classify(
                path,
                classificationBuffer,
                info.Length,
                hasFullBoundedBuffer,
                Math.Min(sourceRoot.MaximumFileBytes, SourceClassifier.MaximumAcceptedTextBytes));
            yield return new SourceDiscoveredFile(
                Path.GetFullPath(path),
                relativePath,
                stableIdentity,
                classificationBuffer,
                hasFullBoundedBuffer,
                contentHash,
                info.Length,
                info.LastWriteTimeUtc,
                classification);
        }

        _lastEvidence = errors.Select(ParseEvidence).ToArray();
    }

    private static void CollectFiles(
        SourceRootConfiguration root,
        string directory,
        ICollection<string> files,
        ICollection<string> errors)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToArray();
        }
        catch (UnauthorizedAccessException exception)
        {
            errors.Add($"permission:{Path.GetRelativePath(root.CanonicalPath, directory)}:{exception.GetType().Name}");
            return;
        }
        catch (IOException exception)
        {
            errors.Add($"io:{Path.GetRelativePath(root.CanonicalPath, directory)}:{exception.GetType().Name}");
            return;
        }

        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                errors.Add($"reparse:{Path.GetRelativePath(root.CanonicalPath, entry.FullName)}");
                continue;
            }

            if (entry is FileInfo)
            {
                files.Add(entry.FullName);
            }
            else if (root.Recursive && entry is DirectoryInfo)
            {
                CollectFiles(root, entry.FullName, files, errors);
            }
        }
    }

    private static bool MatchesPolicy(
        string relativePath,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes)
    {
        var normalised = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var included = includes.Count == 0 || includes.Any(pattern => GlobMatches(normalised, pattern));
        return included && !excludes.Any(pattern => GlobMatches(normalised, pattern));
    }

    private static bool GlobMatches(string path, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Replace('\\', '/'))
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            path, regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private async Task<(string Hash, byte[] Buffer, bool HasFullBuffer, long Length, DateTimeOffset LastWriteAtUtc, string StableIdentity)> ReadSnapshotAsync(
        string path,
        long maximumFileBytes,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        const int signatureLimit = 8192;
        var before = new FileInfo(path);
        var effectiveTextLimit = Math.Min(maximumFileBytes, SourceClassifier.MaximumAcceptedTextBytes);
        var retained = before.Length <= effectiveTextLimit
            ? new MemoryStream(checked((int)before.Length))
            : new MemoryStream(signatureLimit);
        var readBuffer = new byte[bufferSize];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var stableIdentity = _readIdentity(stream.SafeFileHandle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(readBuffer, 0, read);
            total += read;
            var remaining = before.Length <= effectiveTextLimit
                ? before.Length - retained.Length
                : signatureLimit - retained.Length;
            if (remaining > 0)
            {
                retained.Write(readBuffer, 0, (int)Math.Min(remaining, read));
            }
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), retained.ToArray(),
            before.Length <= effectiveTextLimit && total == before.Length,
            total, before.LastWriteTimeUtc, stableIdentity);
    }

    private static bool TryRevalidateRoot(SourceRootConfiguration sourceRoot, ICollection<string> errors)
    {
        try
        {
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(sourceRoot.CanonicalPath);
            var actual = PhysicalFileIdentity.GetDirectory(sourceRoot.CanonicalPath);
            if (sourceRoot.RequiresPhysicalIdentityValidation && string.IsNullOrWhiteSpace(sourceRoot.PhysicalIdentityFingerprint))
            {
                errors.Add("identity:.:SourceRootIdentityMissing");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sourceRoot.PhysicalIdentityFingerprint) &&
                !IsValidFingerprint(sourceRoot.PhysicalIdentityFingerprint))
            {
                errors.Add("identity:.:SourceRootIdentityMalformed");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sourceRoot.PhysicalIdentityFingerprint) &&
                !string.Equals(actual.IdentityFingerprint, sourceRoot.PhysicalIdentityFingerprint, StringComparison.Ordinal))
            {
                errors.Add("identity:.:SourceRootIdentityMismatch");
                return false;
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add("permission:.:SourceRootAccessDenied");
            return false;
        }
        catch (IOException)
        {
            errors.Add("identity:.:SourceRootRevalidationFailed");
            return false;
        }
    }

    private static bool IsValidFingerprint(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static SourceEnumerationEvidence ParseEvidence(string raw)
    {
        var parts = raw.Split(':', 3);
        return new SourceEnumerationEvidence(
            parts.ElementAtOrDefault(0) ?? "io",
            (parts.ElementAtOrDefault(1) ?? ".")[..Math.Min((parts.ElementAtOrDefault(1) ?? ".").Length, 768)],
            (parts.ElementAtOrDefault(2) ?? "filesystem-error")[..Math.Min((parts.ElementAtOrDefault(2) ?? "filesystem-error").Length, 256)]);
    }
}
