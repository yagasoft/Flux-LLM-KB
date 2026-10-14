using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Local, handle-bound source crawl. Cloud content is read only when already available.</summary>
public sealed class LocalSourceEnumerator : IAuthoritativeSourceFileEnumerator, IBoundedSourceFileEnumerator
{
    private readonly Func<SafeFileHandle, string> _readIdentity;
    private readonly Action<string>? _beforeDirectoryRecheck;
    private IReadOnlyList<SourceEnumerationEvidence> _lastEvidence = [];

    public LocalSourceEnumerator(Func<SafeFileHandle, string>? readIdentity = null, Action<string>? beforeDirectoryRecheck = null)
    {
        _readIdentity = readIdentity ?? PhysicalFileIdentity.Get;
        _beforeDirectoryRecheck = beforeDirectoryRecheck;
    }

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

    public IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(SourceRootConfiguration root, CancellationToken cancellationToken) =>
        EnumerateCoreAsync(root, null, cancellationToken);

    public IAsyncEnumerable<SourceDiscoveredFile> PreviewAsync(SourceRootConfiguration root, SourcePreviewLimits limits, CancellationToken cancellationToken) =>
        EnumerateCoreAsync(root, limits, cancellationToken);

    private async IAsyncEnumerable<SourceDiscoveredFile> EnumerateCoreAsync(
        SourceRootConfiguration sourceRoot, SourcePreviewLimits? limits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        var errors = new List<SourceEnumerationEvidence>();
        _lastEvidence = errors; // Also remains visible when a consumer stops early.
        LastInventory = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limits is not null)
        {
            if (limits.MaximumEntries <= 0 || limits.MaximumFiles <= 0 || limits.MaximumReadBytes <= 0 || limits.MaximumDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(limits));
            deadline.CancelAfter(limits.MaximumDuration);
        }
        var budget = new CrawlBudget(limits, errors, deadline.Token, cancellationToken);
        using var rootLease = TryOpenRoot(sourceRoot, errors);
        if (rootLease is null) yield break;
        var candidates = new List<string>();
        GitTrackedSourceDiscovery.Inventory? inventory = null;
        if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked)
        {
            // Git's control-file and tracked-membership policies stay strict in Preview too.
            try
            {
                inventory = await new GitTrackedSourceDiscovery().ReadAsync(sourceRoot.CanonicalPath, deadline.Token, limits?.MaximumEntries).ConfigureAwait(false);
                if (sourceRoot.RequiresPhysicalIdentityValidation && (sourceRoot.RepositoryIdentityFingerprint is null ||
                    inventory.Repository.Identity != sourceRoot.RepositoryIdentityFingerprint)) throw new IOException("Git repository identity changed.");
                if (!inventory.IsComplete) errors.Add(new("limit", ".", "PreviewEntryLimit"));
                candidates.AddRange(inventory.Paths.Select(path => Path.GetFullPath(path, sourceRoot.CanonicalPath)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limits is not null)
            { budget.RecordLimit("PreviewTimeLimit"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { errors.Add(new("git", ".", exception.GetType().Name)); }
        }
        if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.Filesystem)
            CollectFiles(sourceRoot, sourceRoot.CanonicalPath, rootLease.Identity.IdentityFingerprint, candidates, errors, budget);
        else if (sourceRoot.DiscoveryMode != SourceDiscoveryMode.GitTracked)
            errors.Add(new("policy", ".", "UnknownDiscoveryMode"));

        foreach (var path in candidates.OrderBy(path => Path.GetRelativePath(sourceRoot.CanonicalPath, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(sourceRoot.CanonicalPath, path);
            if (!MatchesPolicy(relativePath, sourceRoot.IncludePatterns, sourceRoot.ExcludePatterns)) continue;
            if (!budget.StartFile()) break;
            SourceDiscoveredFile file;
            try
            {
                if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked)
                {
                    PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.GetDirectoryName(path)!);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Git file is a reparse point.");
                }
                file = await ReadSnapshotAsync(path, relativePath, sourceRoot, rootLease.Identity.IdentityFingerprint,
                    budget, deadline.Token).ConfigureAwait(false);
            }
            catch (FileNotFoundException) when (inventory is not null) { continue; }
            catch (DirectoryNotFoundException) when (inventory is not null) { continue; }
            catch (SourceContentUnavailableException)
            {
                errors.Add(new("unavailable", relativePath, "SourceContentNotAvailableLocally"));
                continue;
            }
            catch (PreviewLimitException)
            {
                errors.Add(new("limit", relativePath, "PreviewByteLimit"));
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limits is not null)
            {
                budget.RecordLimit("PreviewTimeLimit");
                break;
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(new("permission", relativePath, "SourcePathDenied"));
                continue;
            }
            catch (SourceSnapshotChangedException)
            {
                errors.Add(new("changed", relativePath, "SourceChangedDuringDiscovery"));
                continue;
            }
            catch (IOException exception)
            {
                errors.Add(new("io", relativePath, exception.GetType().Name));
                continue;
            }
            yield return file with { GitInventory = inventory is null ? null :
                new(inventory.Repository.Identity, inventory.Generation, inventory.TrackedCount, inventory.ExcludedCount) };
        }

        if (inventory is not null && errors.Count == 0 && limits is null)
        {
            LastInventory = new(inventory.Repository.Identity, inventory.Generation, inventory.TrackedCount, inventory.ExcludedCount);
            if (!await ValidateInventoryAsync(sourceRoot, cancellationToken).ConfigureAwait(false))
            {
                LastInventory = null;
                errors.Add(new("git", ".", "GitInventoryChanged"));
            }
        }
    }

    private void CollectFiles(SourceRootConfiguration root, string directory, string rootIdentity,
        ICollection<string> files, ICollection<SourceEnumerationEvidence> errors, CrawlBudget budget)
    {
        if (!budget.CheckTime()) return;
        var relative = Path.GetRelativePath(root.CanonicalPath, directory);
        try
        {
            using var lease = WindowsSourcePathAccess.OpenDirectoryPath(directory, enumerate: true);
            if (lease.IdentityAt(root.CanonicalPath).IdentityFingerprint != rootIdentity)
                throw new SourceSnapshotChangedException("The admitted source root changed.");
            if (lease.Metadata.NamespaceIncomplete)
                errors.Add(new("partial", relative, "CloudDirectoryNamespaceIncomplete"));
            foreach (var entry in lease.Entries(budget.CallerCancellation))
            {
                if (!budget.VisitEntry()) return;
                var path = Path.Combine(directory, entry.Name);
                var entryRelative = Path.GetRelativePath(root.CanonicalPath, path);
                if ((entry.Attributes & FileAttributes.Directory) == 0)
                {
                    if (MatchesPolicy(entryRelative, root.IncludePatterns, root.ExcludePatterns)) files.Add(path);
                }
                else if (root.Recursive && !root.ExcludePatterns.Any(pattern =>
                    pattern.Replace('\\', '/').EndsWith("/**", StringComparison.Ordinal) &&
                    GlobMatches(entryRelative.Replace('\\', '/'), pattern.Replace('\\', '/')[..^3])))
                {
                    CollectFiles(root, path, rootIdentity, files, errors, budget);
                }
            }
            _beforeDirectoryRecheck?.Invoke(directory);
            var after = lease.Metadata;
            WindowsSourcePathAccess.ValidateTag(after);
            if (after.NamespaceIncomplete && !errors.Any(item => item.RelativePath == relative && item.Detail == "CloudDirectoryNamespaceIncomplete"))
                errors.Add(new("partial", relative, "CloudDirectoryNamespaceIncomplete"));
        }
        catch (UnauthorizedAccessException) { errors.Add(new("reparse", relative, "SourceDirectoryDenied")); }
        catch (IOException exception) { errors.Add(new("io", relative, exception.GetType().Name)); }
    }

    private static bool MatchesPolicy(string relativePath, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        var normalised = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return (includes.Count == 0 || includes.Any(pattern => GlobMatches(normalised, pattern))) &&
            !excludes.Any(pattern => GlobMatches(normalised, pattern));
    }

    private static bool GlobMatches(string path, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Replace('\\', '/'))
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private async Task<SourceDiscoveredFile> ReadSnapshotAsync(string path, string relativePath,
        SourceRootConfiguration root, string rootIdentity, CrawlBudget budget, CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        const int signatureLimit = 8192;
        using var lease = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path, rootIdentity);
        var length = RandomAccess.GetLength(lease.Handle);
        budget.ReserveRead(length); // Reject before the first content read, not after hashing the file.
        var lastWrite = File.GetLastWriteTimeUtc(lease.Handle);
        var identity = _readIdentity(lease.Handle);
        var effectiveTextLimit = Math.Min(root.MaximumFileBytes, SourceClassifier.MaximumAcceptedTextBytes);
        using var retained = new MemoryStream(length <= effectiveTextLimit ? checked((int)length) : signatureLimit);
        await using var stream = new FileStream(lease.Handle, FileAccess.Read, bufferSize, isAsync: true);
        var readBuffer = new byte[bufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var total = 0L;
        while (total < length)
        {
            var read = await stream.ReadAsync(readBuffer.AsMemory(0, (int)Math.Min(bufferSize, length - total)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new SourceSnapshotChangedException("The source was truncated during discovery.");
            hash.AppendData(readBuffer, 0, read);
            total += read;
            var remaining = (length <= effectiveTextLimit ? length : signatureLimit) - retained.Length;
            if (remaining > 0) retained.Write(readBuffer, 0, (int)Math.Min(remaining, read));
        }
        WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(lease.Handle));
        if (RandomAccess.GetLength(lease.Handle) != length || File.GetLastWriteTimeUtc(lease.Handle) != lastWrite)
            throw new SourceSnapshotChangedException("The source changed during discovery.");
        var bytes = retained.ToArray();
        var hasFullBuffer = length <= effectiveTextLimit;
        return new(Path.GetFullPath(path), relativePath, identity, bytes, hasFullBuffer,
            Convert.ToHexStringLower(hash.GetHashAndReset()), length, lastWrite,
            SourceClassifier.Classify(path, bytes, length, hasFullBuffer, effectiveTextLimit, root.IndexSourceText),
            SourcePath: new(root.CanonicalPath, rootIdentity));
    }

    private static WindowsSourcePathAccess.DirectoryPathLease? TryOpenRoot(SourceRootConfiguration root, ICollection<SourceEnumerationEvidence> errors)
    {
        WindowsSourcePathAccess.DirectoryPathLease? lease = null;
        try
        {
            if (root.RequiresPhysicalIdentityValidation && string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint))
            {
                errors.Add(new("identity", ".", "SourceRootIdentityMissing"));
                return null;
            }
            if (!string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint) &&
                (root.PhysicalIdentityFingerprint.Length != 64 || !root.PhysicalIdentityFingerprint.All(char.IsAsciiHexDigit)))
            {
                errors.Add(new("identity", ".", "SourceRootIdentityMalformed"));
                return null;
            }
            if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked)
                PhysicalFileIdentity.EnsureNoReparsePointTraversal(root.CanonicalPath);
            lease = WindowsSourcePathAccess.OpenDirectoryPath(root.CanonicalPath);
            if (!string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint) &&
                lease.Identity.IdentityFingerprint != root.PhysicalIdentityFingerprint)
            {
                errors.Add(new("identity", ".", "SourceRootIdentityMismatch"));
                lease.Dispose();
                return null;
            }
            return lease;
        }
        catch (UnauthorizedAccessException) { errors.Add(new("permission", ".", "SourceRootAccessDenied")); }
        catch (IOException) { errors.Add(new("identity", ".", "SourceRootRevalidationFailed")); }
        lease?.Dispose();
        return null;
    }

    private sealed class PreviewLimitException : IOException;

    private sealed class CrawlBudget(SourcePreviewLimits? limits, ICollection<SourceEnumerationEvidence> errors,
        CancellationToken deadline, CancellationToken caller)
    {
        private int _entries;
        private int _files;
        private long _reservedBytes;
        public CancellationToken CallerCancellation => caller;
        public void RecordLimit(string reason)
        {
            if (!errors.Any(item => item.Kind == "limit" && item.Detail == reason)) errors.Add(new("limit", ".", reason));
        }
        public bool CheckTime()
        {
            caller.ThrowIfCancellationRequested();
            if (!deadline.IsCancellationRequested) return true;
            RecordLimit("PreviewTimeLimit");
            return false;
        }
        public bool VisitEntry()
        {
            if (!CheckTime()) return false;
            if (limits is null || _entries++ < limits.MaximumEntries) return true;
            RecordLimit("PreviewEntryLimit");
            return false;
        }
        public bool StartFile()
        {
            if (!CheckTime()) return false;
            if (limits is null || _files++ < limits.MaximumFiles) return true;
            RecordLimit("PreviewFileLimit");
            return false;
        }
        public void ReserveRead(long length)
        {
            if (limits is not null && length > limits.MaximumReadBytes - _reservedBytes) throw new PreviewLimitException();
            _reservedBytes += length;
        }
    }
}
