using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Local, handle-bound source crawl. Cloud content is read only when already available.</summary>
public sealed partial class LocalSourceEnumerator : IAuthoritativeSourceFileEnumerator, IBoundedSourceFileEnumerator, IIncrementalSourceFileEnumerator
{
    private SourceEnumerationScope? _scope;
    private readonly List<SourceNamespaceObservation> _directories = [];
    public IReadOnlyList<SourceNamespaceObservation> LastDirectories => _directories;
    public int LastEnumeratedEntries { get; private set; }
    public IDisposable UseScope(SourceEnumerationScope scope)
    {
        if (_scope is not null) throw new InvalidOperationException("SourceEnumerationScopeAlreadyActive");
        _scope = scope;
        return new ScopeLease(() => _scope = null);
    }
    private readonly Func<SafeFileHandle, string> _readIdentity;
    private readonly Action<string>? _beforeDirectoryRecheck;
    private readonly ISourceAccessRepairClient? _sourceAccessRepairClient;
    private readonly TimeProvider _metadataTimeProvider;
    private IReadOnlyList<SourceEnumerationEvidence> _lastEvidence = [];
    internal Action<int>? SourceBytesRead { get; set; }
    internal Func<CancellationToken, ValueTask>? BeforeMetadataDelay { get; init; }

    public LocalSourceEnumerator(Func<SafeFileHandle, string>? readIdentity = null, Action<string>? beforeDirectoryRecheck = null,
        ISourceAccessRepairClient? sourceAccessRepairClient = null, TimeProvider? metadataTimeProvider = null)
    {
        _readIdentity = readIdentity ?? PhysicalFileIdentity.Get;
        _beforeDirectoryRecheck = beforeDirectoryRecheck;
        _sourceAccessRepairClient = sourceAccessRepairClient;
        _metadataTimeProvider = metadataTimeProvider ?? TimeProvider.System;
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
        EnumerateSnapshotsAsync(root, null, cancellationToken);

    public IAsyncEnumerable<SourceDiscoveredFile> PreviewAsync(SourceRootConfiguration root, SourcePreviewLimits limits, CancellationToken cancellationToken) =>
        EnumerateSnapshotsAsync(root, limits, cancellationToken);

    public async IAsyncEnumerable<ISourceFileCapture> EnumerateCapturedAsync(SourceRootConfiguration sourceRoot,
        SourceScanOwnership ownership, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (sourceRoot.DiscoveryMode is not (SourceDiscoveryMode.Filesystem or SourceDiscoveryMode.GitTracked))
            throw new InvalidOperationException("Live source capture requires supported local discovery.");
        await foreach (var capture in EnumerateCoreAsync(sourceRoot, null, ownership, cancellationToken).ConfigureAwait(false))
            yield return capture;
    }

    private async IAsyncEnumerable<SourceDiscoveredFile> EnumerateSnapshotsAsync(SourceRootConfiguration sourceRoot,
        SourcePreviewLimits? limits, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var capture in EnumerateCoreAsync(sourceRoot, limits, null, cancellationToken).ConfigureAwait(false))
        {
            var snapshot = capture.Snapshot;
            await capture.DisposeAsync().ConfigureAwait(false);
            yield return snapshot;
        }
    }

    private async IAsyncEnumerable<LocalSourceFileCapture> EnumerateCoreAsync(
        SourceRootConfiguration sourceRoot, SourcePreviewLimits? limits, SourceScanOwnership? ownership,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        var errors = new List<SourceEnumerationEvidence>();
        _lastEvidence = errors; // Also remains visible when a consumer stops early.
        LastInventory = null;
        _directories.Clear(); LastEnumeratedEntries = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limits is not null)
        {
            if (limits.MaximumEntries <= 0 || limits.MaximumFiles <= 0 || limits.MaximumReadBytes <= 0 || limits.MaximumDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(limits));
            deadline.CancelAfter(limits.MaximumDuration);
        }
        var budget = new CrawlBudget(limits, errors, deadline.Token, cancellationToken);
        // Preview is always read-only, including when recovery is enabled for normal scans.
        var accessRepair = limits is null ? _sourceAccessRepairClient : null;
        var metadataPacer = limits is null && _scope is { MetadataEntriesPerSecond: > 0 }
            ? new SourceMetadataPacer(_scope.MetadataEntriesPerSecond, _metadataTimeProvider) : null;
        Func<CancellationToken, ValueTask>? paceMetadata = metadataPacer is null ? null : PaceMetadataAsync;
        using var rootLease = TryOpenRoot(sourceRoot, errors, accessRepair, cancellationToken, metadataPacer is not null);
        if (rootLease is null) yield break;
        var rootIdentity = rootLease.Identity.IdentityFingerprint;
        if (metadataPacer is not null) rootLease.Dispose(); // No ancestor handle survives an artificial delay.
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
                if (!inventory.IsComplete) errors.Add(new("limit", ".", "PreviewEntryLimit") { NamespaceIncomplete = true });
                candidates.AddRange(inventory.Paths.Select(path => Path.GetFullPath(path, sourceRoot.CanonicalPath)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limits is not null)
            { budget.RecordLimit("PreviewTimeLimit"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { errors.Add(new("git", ".", exception.GetType().Name) { NamespaceIncomplete = true }); }
        }
        if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.Filesystem)
        {
            if (limits is null && _scope?.Targets is { } targets)
            {
                foreach (var relative in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var target = Path.GetFullPath(relative, sourceRoot.CanonicalPath);
                    if (!WindowsSourcePathAccess.IsWithin(sourceRoot.CanonicalPath, target)) throw new UnauthorizedAccessException("SourceTargetOutsideRoot");
                    var admittedRelative = Path.GetRelativePath(sourceRoot.CanonicalPath, target);
                    if (!sourceRoot.Recursive && admittedRelative.Contains(Path.DirectorySeparatorChar)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(target);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (sourceRoot.Recursive || admittedRelative == ".")
                                await CollectFilesAsync(sourceRoot, target, rootIdentity, candidates, errors, budget, accessRepair, paceMetadata).ConfigureAwait(false);
                        }
                        else candidates.Add(target);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { errors.Add(FailureEvidence(exception, relative) with { NamespaceIncomplete = true }); }
                }
            }
            else await CollectFilesAsync(sourceRoot, sourceRoot.CanonicalPath, rootIdentity, candidates, errors, budget, accessRepair, paceMetadata).ConfigureAwait(false);
        }
        else if (sourceRoot.DiscoveryMode != SourceDiscoveryMode.GitTracked)
            errors.Add(new("policy", ".", "UnknownDiscoveryMode") { NamespaceIncomplete = true });

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => Path.GetRelativePath(sourceRoot.CanonicalPath, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(sourceRoot.CanonicalPath, path);
            if (!sourceRoot.Recursive && relativePath.Contains(Path.DirectorySeparatorChar)) continue;
            if (limits is null && _scope?.Targets is { } selected &&
                !selected.Any(target => SourcePathScope.Contains(target, true, relativePath))) continue;
            if (!MatchesPolicy(relativePath, sourceRoot.IncludePatterns, sourceRoot.ExcludePatterns)) continue;
            if (!budget.StartFile()) break;
            LocalSourceFileCapture capture;
            try
            {
                if (paceMetadata is not null) await paceMetadata(cancellationToken).ConfigureAwait(false);
                if (limits is null && _scope?.CaptureNeeded is { } filter &&
                    !await filter(relativePath, cancellationToken).ConfigureAwait(false)) continue;
                if (sourceRoot.DiscoveryMode == SourceDiscoveryMode.GitTracked)
                {
                    PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.GetDirectoryName(path)!);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Git file is a reparse point.");
                }
                capture = await ReadCaptureAsync(path, relativePath, sourceRoot, rootIdentity,
                    budget, deadline.Token, accessRepair, ownership, inventory is null ? null :
                        new(inventory.Repository.Identity, inventory.Generation, inventory.TrackedCount, inventory.ExcludedCount)).ConfigureAwait(false);
            }
            catch (FileNotFoundException) when (inventory is not null) { continue; }
            catch (DirectoryNotFoundException) when (inventory is not null) { continue; }
            catch (SourceContentUnavailableException exception)
            {
                errors.Add(FailureEvidence(exception, relativePath));
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
            catch (UnauthorizedAccessException exception)
            {
                errors.Add(FailureEvidence(exception, relativePath));
                continue;
            }
            catch (SourceSnapshotChangedException)
            {
                errors.Add(new("changed", relativePath, "SourceChangedDuringDiscovery"));
                continue;
            }
            catch (IOException exception)
            {
                errors.Add(FailureEvidence(exception, relativePath));
                continue;
            }
            await using (capture.ConfigureAwait(false))
                yield return capture;
        }

        if (inventory is not null && errors.Count == 0 && limits is null)
        {
            LastInventory = new(inventory.Repository.Identity, inventory.Generation, inventory.TrackedCount, inventory.ExcludedCount);
            if (!await ValidateInventoryAsync(sourceRoot, cancellationToken).ConfigureAwait(false))
            {
                LastInventory = null;
                errors.Add(new("git", ".", "GitInventoryChanged") { NamespaceIncomplete = true });
            }
        }

        async ValueTask PaceMetadataAsync(CancellationToken ct)
        {
            var wait = metadataPacer!.WaitAsync(ct);
            if (!wait.IsCompletedSuccessfully && BeforeMetadataDelay is { } beforeDelay)
                await beforeDelay(ct).ConfigureAwait(false);
            await wait.ConfigureAwait(false);
        }
    }

    private async Task CollectFilesAsync(SourceRootConfiguration root, string directory, string rootIdentity,
        ICollection<string> files, ICollection<SourceEnumerationEvidence> errors, CrawlBudget budget, ISourceAccessRepairClient? accessRepair,
        Func<CancellationToken, ValueTask>? paceMetadata)
    {
        if (paceMetadata is not null)
        {
            await CollectMetadataFilesAsync(root, directory, rootIdentity, files, errors, budget, accessRepair, paceMetadata).ConfigureAwait(false);
            return;
        }
        if (!budget.CheckTime()) return;
        var relative = Path.GetRelativePath(root.CanonicalPath, directory);
        try
        {
            using var lease = SourceAccessRecovery.Run(root.CanonicalPath, rootIdentity, accessRepair,
                () => WindowsSourcePathAccess.OpenDirectoryPath(directory, enumerate: true, allowNamespaceChanges: paceMetadata is not null), budget.CallerCancellation);
            var parentIdentity = lease.HeldComponents.Count > 1 ? PhysicalFileIdentity.Get(lease.HeldComponents[^2].Handle).Split(':')[^1] : "";
            _directories.Add(new(relative, PhysicalFileIdentity.Get(lease.Handle).Split(':')[^1], parentIdentity, false));
            if (lease.IdentityAt(root.CanonicalPath).IdentityFingerprint != rootIdentity)
                throw new SourceSnapshotChangedException("The admitted source root changed.");
            if (lease.Metadata.NamespaceIncomplete)
                errors.Add(new("partial", relative, "CloudDirectoryNamespaceIncomplete") { NamespaceIncomplete = true });
            foreach (var entry in lease.Entries(budget.CallerCancellation,
                paceMetadata is null ? null : root.CanonicalPath, rootIdentity,
                () => RecordIncompleteNamespace()))
            {
                if (paceMetadata is not null) await paceMetadata(budget.CallerCancellation).ConfigureAwait(false);
                LastEnumeratedEntries++;
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
                    await CollectFilesAsync(root, path, rootIdentity, files, errors, budget, accessRepair, paceMetadata).ConfigureAwait(false);
                }
            }
            _beforeDirectoryRecheck?.Invoke(directory);
            var after = lease.Metadata;
            WindowsSourcePathAccess.ValidateTag(after);
            if (after.NamespaceIncomplete && !errors.Any(item => item.RelativePath == relative && item.Detail == "CloudDirectoryNamespaceIncomplete"))
                errors.Add(new("partial", relative, "CloudDirectoryNamespaceIncomplete") { NamespaceIncomplete = true });

            void RecordIncompleteNamespace()
            {
                if (!errors.Any(item => item.RelativePath == relative && item.Detail == "CloudDirectoryNamespaceIncomplete"))
                    errors.Add(new("partial", relative, "CloudDirectoryNamespaceIncomplete") { NamespaceIncomplete = true });
            }
        }
        catch (UnauthorizedAccessException exception) { errors.Add(FailureEvidence(exception, relative) with { NamespaceIncomplete = true }); }
        catch (IOException exception) { errors.Add(FailureEvidence(exception, relative) with { NamespaceIncomplete = true }); }
    }

    public static bool MatchesPolicy(string relativePath, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
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

    private async Task<LocalSourceFileCapture> ReadCaptureAsync(string path, string relativePath,
        SourceRootConfiguration root, string rootIdentity, CrawlBudget budget, CancellationToken cancellationToken,
        ISourceAccessRepairClient? accessRepair, SourceScanOwnership? ownership, GitInventoryEvidence? inventory)
    {
        const int bufferSize = 128 * 1024;
        const int signatureLimit = 8192;
        var lease = SourceAccessRecovery.Run(root.CanonicalPath, rootIdentity, accessRepair,
            () => WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path, rootIdentity), cancellationToken);
        FileStream? stream = null;
        try
        {
            var length = RandomAccess.GetLength(lease.Handle);
            budget.ReserveRead(length); // Reject before the first content read, not after hashing the file.
            var lastWrite = File.GetLastWriteTimeUtc(lease.Handle);
            var identity = _readIdentity(lease.Handle);
            var effectiveTextLimit = Math.Min(root.MaximumFileBytes, SourceClassifier.MaximumAcceptedTextBytes);
            using var retained = new MemoryStream(length <= effectiveTextLimit ? checked((int)length) : signatureLimit);
            stream = new FileStream(lease.Handle, FileAccess.Read, bufferSize, isAsync: true);
            var readBuffer = new byte[bufferSize];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = 0L;
            while (total < length)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(0, (int)Math.Min(bufferSize, length - total)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new SourceSnapshotChangedException("The source was truncated during discovery.");
                SourceBytesRead?.Invoke(read);
                hash.AppendData(readBuffer, 0, read);
                total += read;
                var remaining = (length <= effectiveTextLimit ? length : signatureLimit) - retained.Length;
                if (remaining > 0) retained.Write(readBuffer, 0, (int)Math.Min(remaining, read));
            }
            cancellationToken.ThrowIfCancellationRequested();
            WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(lease.Handle));
            if (RandomAccess.GetLength(lease.Handle) != length || File.GetLastWriteTimeUtc(lease.Handle) != lastWrite)
                throw new SourceSnapshotChangedException("The source changed during discovery.");
            var bytes = retained.ToArray();
            var hasFullBuffer = length <= effectiveTextLimit;
            var snapshot = new SourceDiscoveredFile(Path.GetFullPath(path), relativePath, identity, bytes, hasFullBuffer,
                Convert.ToHexStringLower(hash.GetHashAndReset()), length, lastWrite,
                SourceClassifier.Classify(path, bytes, length, hasFullBuffer, effectiveTextLimit, root.IndexSourceText),
                GitInventory: inventory, ScanOwnership: ownership, SourcePath: new(root.CanonicalPath, rootIdentity),
                Observation: WindowsSourceObservationReader.Describe(lease));
            return new(snapshot, lease, stream);
        }
        catch
        {
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            finally { lease.Dispose(); }
            throw;
        }
    }

    private static WindowsSourcePathAccess.DirectoryPathLease? TryOpenRoot(SourceRootConfiguration root,
        ICollection<SourceEnumerationEvidence> errors, ISourceAccessRepairClient? accessRepair, CancellationToken cancellationToken, bool allowNamespaceChanges = false)
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
            lease = SourceAccessRecovery.Run(root.CanonicalPath, root.PhysicalIdentityFingerprint, accessRepair,
                () => WindowsSourcePathAccess.OpenDirectoryPath(root.CanonicalPath, allowNamespaceChanges: allowNamespaceChanges), cancellationToken);
            if (!string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint) &&
                lease.Identity.IdentityFingerprint != root.PhysicalIdentityFingerprint)
            {
                errors.Add(new("identity", ".", "SourceRootIdentityMismatch"));
                lease.Dispose();
                return null;
            }
            return lease;
        }
        catch (UnauthorizedAccessException exception) { errors.Add(FailureEvidence(exception, ".")); }
        catch (IOException exception) { errors.Add(FailureEvidence(exception, ".")); }
        lease?.Dispose();
        return null;
    }

    private static SourceEnumerationEvidence FailureEvidence(Exception exception, string relativePath)
    {
        var nativeCode = exception.InnerException is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null;
        var (kind, reason) = exception switch
        {
            SourcePathSecurityException security => ("security", security.ReasonCode),
            SourceAccessDeniedException denied => ("permission", denied.ReasonCode),
            SourceContentUnavailableException => ("unavailable", "SourceContentNotAvailableLocally"),
            UnauthorizedAccessException when nativeCode == 5 => ("permission", "SourceAccessDenied"),
            UnauthorizedAccessException => ("security", "SourcePathSafetyRefused"),
            SourceSnapshotChangedException => ("changed", "SourceChangedDuringDiscovery"),
            FileNotFoundException or DirectoryNotFoundException => ("changed", "SourcePathNoLongerExists"),
            IOException when nativeCode == 32 => ("io", "SourceInUse"),
            _ => ("io", "SourceIoFailure")
        };
        return new(kind, relativePath, reason) { NativeErrorCode = nativeCode };
    }

    private sealed class PreviewLimitException : IOException;

    private sealed class ScopeLease(Action release) : IDisposable { public void Dispose() => release(); }

    private sealed class CrawlBudget(SourcePreviewLimits? limits, ICollection<SourceEnumerationEvidence> errors,
        CancellationToken deadline, CancellationToken caller)
    {
        private int _entries;
        private int _files;
        private long _reservedBytes;
        public CancellationToken CallerCancellation => caller;
        public void RecordLimit(string reason)
        {
            if (!errors.Any(item => item.Kind == "limit" && item.Detail == reason)) errors.Add(new("limit", ".", reason) { NamespaceIncomplete = true });
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
