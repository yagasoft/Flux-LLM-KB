using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Deterministic root crawl. Reparse points are reported but never traversed.</summary>
public sealed class LocalSourceEnumerator : IAuthoritativeSourceFileEnumerator
{
    private readonly Func<SafeFileHandle, string> _readIdentity;
    private IReadOnlyList<SourceEnumerationEvidence> _lastEvidence = [];

    public LocalSourceEnumerator(Func<SafeFileHandle, string>? readIdentity = null) =>
        _readIdentity = readIdentity ?? PhysicalFileIdentity.Get;

    public IReadOnlyList<SourceEnumerationEvidence> LastEvidence => _lastEvidence;
    public GitInventoryEvidence? LastInventory { get; private set; }

    public async ValueTask<bool> ValidateInventoryAsync(SourceRootConfiguration root, CancellationToken cancellationToken)
    {
        if (LastInventory is null) return false;
        try
        {
            var current = await new GitTrackedSourceDiscovery().ReadAsync(root.CanonicalPath, cancellationToken).ConfigureAwait(false);
            return current.Repository.Identity == LastInventory.RepositoryIdentity && current.Generation == LastInventory.Generation;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    public async IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
        SourceRootConfiguration sourceRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        _lastEvidence = [];
        LastInventory = null;
        var errors = new List<string>();
        if (!TryRevalidateRoot(sourceRoot, errors))
        {
            _lastEvidence = errors.Select(ParseEvidence).ToArray();
            yield break;
        }
        var candidates = new List<string>();
        GitTrackedSourceDiscovery.Inventory? inventory = null;
        if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked)
        {
            try
            {
                inventory = await new GitTrackedSourceDiscovery().ReadAsync(sourceRoot.CanonicalPath, cancellationToken).ConfigureAwait(false);
                if (sourceRoot.RequiresPhysicalIdentityValidation && (sourceRoot.RepositoryIdentityFingerprint is null ||
                    inventory.Repository.Identity != sourceRoot.RepositoryIdentityFingerprint)) throw new IOException("Git repository identity changed.");
                candidates.AddRange(inventory.Paths.Select(path => Path.GetFullPath(path, sourceRoot.CanonicalPath)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"git:.:{exception.GetType().Name}");
            }
        }
        else if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.Filesystem) CollectFiles(sourceRoot, sourceRoot.CanonicalPath, candidates, errors);
        else errors.Add("policy:.:UnknownDiscoveryMode");
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
                if (inventory is not null)
                {
                    PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.GetDirectoryName(path)!);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Git file is a reparse point.");
                }
                var snapshot = await ReadSnapshotAsync(path, sourceRoot.MaximumFileBytes, cancellationToken, inventory is not null ? sourceRoot.CanonicalPath : null).ConfigureAwait(false);
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
            catch (FileNotFoundException) when (inventory is not null) { continue; }
            catch (DirectoryNotFoundException) when (inventory is not null) { continue; }
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
                Math.Min(sourceRoot.MaximumFileBytes, SourceClassifier.MaximumAcceptedTextBytes),
                sourceRoot.IndexSourceText);
            yield return new SourceDiscoveredFile(
                Path.GetFullPath(path),
                relativePath,
                stableIdentity,
                classificationBuffer,
                hasFullBoundedBuffer,
                contentHash,
                info.Length,
                info.LastWriteTimeUtc,
                classification,
                inventory is null ? null : new GitInventoryEvidence(inventory.Repository.Identity, inventory.Generation, inventory.TrackedCount, inventory.ExcludedCount));
        }

        if (inventory is not null && errors.Count == 0)
        {
            LastInventory = new GitInventoryEvidence(inventory.Repository.Identity, inventory.Generation, inventory.TrackedCount, inventory.ExcludedCount);
            if (!await ValidateInventoryAsync(sourceRoot, cancellationToken).ConfigureAwait(false))
            {
                LastInventory = null;
                errors.Add("git:.:GitInventoryChanged");
            }
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
        CancellationToken cancellationToken, string? gitRoot = null)
    {
        const int bufferSize = 128 * 1024;
        const int signatureLimit = 8192;
        var before = new FileInfo(path);
        var effectiveTextLimit = Math.Min(maximumFileBytes, SourceClassifier.MaximumAcceptedTextBytes);
        var retained = before.Length <= effectiveTextLimit
            ? new MemoryStream(checked((int)before.Length))
            : new MemoryStream(signatureLimit);
        var readBuffer = new byte[bufferSize];
        using var rootLease = gitRoot is null ? null : PhysicalFileIdentity.OpenDirectoryLease(gitRoot);
        using var parentLease = gitRoot is null ? null : PhysicalFileIdentity.OpenDirectoryLease(Path.GetDirectoryName(path)!);
        await using var stream = gitRoot is not null
            ? new FileStream(PhysicalFileIdentity.OpenReadNoFollow(path), FileAccess.Read, bufferSize, isAsync: true)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (gitRoot is not null)
        {
            var finalPath = PhysicalFileIdentity.GetFinalPath(stream.SafeFileHandle);
            var finalParent = PhysicalFileIdentity.GetDirectory(Path.GetDirectoryName(finalPath)!);
            if (finalParent.IdentityFingerprint != parentLease!.Identity.IdentityFingerprint ||
                !string.Equals(finalPath, Path.Combine(parentLease.Identity.CanonicalPath, Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase) ||
                !finalPath.StartsWith(rootLease!.Identity.CanonicalPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Git source resolved outside its leased parent/root.");
        }
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
