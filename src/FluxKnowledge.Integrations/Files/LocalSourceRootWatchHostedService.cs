using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Best-effort bounded relative hints; guarded reconciliation establishes truth.</summary>
public sealed class LocalSourceRootWatchHostedService(
    ISourceRootWatchStore store,
    SourceWatchCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<LocalSourceRootWatchHostedService> logger,
    IDeploymentValidationHold? deploymentValidationHold = null,
    IDatabaseStartupReadiness? databaseStartupReadiness = null) : BackgroundService
{
    private static readonly TimeSpan RebuildCadence = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PersistenceCadence = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PersistenceFailureDelay = TimeSpan.FromSeconds(30);
    private readonly LocalSourceWatchSignalBuffer _signals = new(coordinator, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (databaseStartupReadiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await (deploymentValidationHold ?? DeploymentValidationHold.None)
            .WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
        using var persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var persistence = PersistHintsAsync(persistenceCancellation.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IDisposable? watchers = null;
                try { watchers = await BuildWatchersAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Source-root watchers could not be restored; periodic reconciliation remains authoritative.");
                }
                try { await Task.Delay(RebuildCadence, timeProvider, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                finally { watchers?.Dispose(); }
            }
        }
        finally
        {
            await persistenceCancellation.CancelAsync().ConfigureAwait(false);
            await persistence.ConfigureAwait(false);
        }
    }

    private async Task PersistHintsAsync(CancellationToken stoppingToken)
    {
        try
        {
            var delay = PersistenceCadence;
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
                delay = await _signals.FlushAsync(stoppingToken).ConfigureAwait(false)
                    ? PersistenceCadence : PersistenceFailureDelay;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task<IDisposable> BuildWatchersAsync(CancellationToken cancellationToken)
    {
        var registrations = new List<IDisposable>();
        var roots = await store.ReadEnabledRootsAsync(cancellationToken).ConfigureAwait(false);
        _signals.ConfigureRoots(roots.Select(root => root.Id));
        foreach (var root in roots)
        {
            var registration = new List<IDisposable>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Pin validated ancestor/root names for this registration's lifetime. FileSystemWatcher
                // opens its own handle; only guarded reconciliation may authorise source content reads.
                registration.Add(Revalidate(root));
                var watchPaths = GetWatchPaths(root, out var repository);
                foreach (var watchPath in watchPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var watcher = new FileSystemWatcher(watchPath)
                    {
                        IncludeSubdirectories = root.Recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                            NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes | NotifyFilters.Security
                    };
                    registration.Add(watcher);
                    watcher.Created += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Created, change, repository);
                    watcher.Changed += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Changed, change, repository);
                    watcher.Deleted += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Deleted, change, repository);
                    watcher.Renamed += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Renamed, change, repository);
                    watcher.Error += (_, _) => Signal(root.Id, SourceWatchSignalKind.Overflow);
                    watcher.EnableRaisingEvents = true;
                }
                registrations.Add(new CompositeDisposable(registration));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                new CompositeDisposable(registration).Dispose();
                new CompositeDisposable(registrations).Dispose();
                throw;
            }
            catch (Exception exception)
            {
                new CompositeDisposable(registration).Dispose();
                logger.LogWarning(exception, "Source-root watcher was not opened for {SourceRootId}; periodic reconciliation remains authoritative.", root.Id.Value);
                Signal(root.Id, SourceWatchSignalKind.Overflow);
            }
        }
        return new CompositeDisposable(registrations);
    }

    private void Signal(SourceRootId rootId, SourceWatchSignalKind kind) =>
        _signals.Signal(new SourceWatchSignal(rootId, kind, timeProvider.GetUtcNow()));

    private void SignalIfRelevant(SourceRootConfiguration root, SourceWatchSignalKind kind, FileSystemEventArgs change,
        GitTrackedSourceDiscovery.Repository? repository)
    {
        if (CreateSignal(root, kind, change, timeProvider.GetUtcNow(), repository) is { } signal) _signals.Signal(signal);
    }

    internal static SourceWatchSignal? CreateSignal(SourceRootConfiguration root, SourceWatchSignalKind kind,
        FileSystemEventArgs change, DateTimeOffset observedAtUtc, GitTrackedSourceDiscovery.Repository? repository = null)
    {
        if (!ShouldSignal(root, change, repository)) return null;
        var paths = new List<SourceWatchPathHint>(2);
        var incomplete = false;
        var outside = false;
        var control = false;
        // FileSystemWatcher cannot reliably tell us whether a deleted/renamed path was a
        // directory. Keep a conservative subtree hint, without opening event paths.
        foreach (var eventPath in change is RenamedEventArgs renamed
            ? new[] { renamed.OldFullPath, change.FullPath } : new[] { change.FullPath })
        {
            try
            {
                if (!Path.IsPathFullyQualified(eventPath)) { incomplete = true; continue; }
                var path = Path.GetFullPath(eventPath);
                var relative = Path.GetRelativePath(root.CanonicalPath, path);
                var slashRelative = relative.Replace('\\', '/');
                var git = root.DiscoveryMode == SourceDiscoveryMode.GitTracked;
                var insideControl = git && (slashRelative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    slashRelative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) || repository is not null &&
                    (Within(path, repository.GitDirectory) || Within(path, repository.CommonDirectory)));
                if (insideControl) { control = true; continue; }
                if (git && repository is not null &&
                    (OverlapsControl(path, repository.GitDirectory) || OverlapsControl(path, repository.CommonDirectory))) control = true;
                if (slashRelative == ".." || slashRelative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                {
                    outside = true;
                    continue;
                }
                if (git && GitTrackedSourceDiscovery.IsExcludedTree(slashRelative)) continue;
                paths.Add(new(relative, IsSubtree: true));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                incomplete = true;
            }
        }
        // A boundary rename needs only its inside obligation. An unexplained outside-only
        // callback, or an invalid name, cannot establish a complete path hint.
        incomplete |= outside && paths.Count == 0 && !control;
        return new(root.Id, kind, observedAtUtc, paths, incomplete, control);
    }

    private static bool Within(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldSignal(SourceRootConfiguration root, FileSystemEventArgs change,
        GitTrackedSourceDiscovery.Repository? repository = null) =>
        root.DiscoveryMode != SourceDiscoveryMode.GitTracked || MayAffectRepository(root.CanonicalPath, change.FullPath, repository) ||
        change is RenamedEventArgs renamed && MayAffectRepository(root.CanonicalPath, renamed.OldFullPath, repository);

    private static bool MayAffectRepository(string root, string path, GitTrackedSourceDiscovery.Repository? repository)
    {
        // Event paths are local hints only. Never suppress an uncertain/control hint,
        // or inspect file existence: deleted directories can contain eligible files.
        if (!Path.IsPathFullyQualified(path)) return true;
        string relative;
        try { path = Path.GetFullPath(path); relative = Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) ||
            relative.Equals(".git", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (repository is not null && (OverlapsControl(path, repository.GitDirectory) || OverlapsControl(path, repository.CommonDirectory)))
            return true;
        return !GitTrackedSourceDiscovery.IsExcludedTree(relative);
    }

    private static bool OverlapsControl(string path, string control)
    {
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        control = control.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(control, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(control + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            control.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IDisposable Revalidate(SourceRootConfiguration root)
    {
        if (root.FollowLinks) throw new UnauthorizedAccessException("Source-root watchers do not follow links.");
        if (root.RequiresPhysicalIdentityValidation && string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint))
            throw new IOException("The admitted source-root identity is missing; re-admit the folder before watching it.");
        if (!string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint) &&
            (root.PhysicalIdentityFingerprint.Length != 64 || !root.PhysicalIdentityFingerprint.All(char.IsAsciiHexDigit)))
            throw new IOException("The admitted source-root identity is malformed; re-admit the folder before watching it.");
        if (root.DiscoveryMode == SourceDiscoveryMode.GitTracked)
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(root.CanonicalPath);
        else if (root.DiscoveryMode != SourceDiscoveryMode.Filesystem)
            throw new UnauthorizedAccessException("The source-root discovery mode is not supported.");
        var lease = WindowsSourcePathAccess.OpenDirectoryPath(root.CanonicalPath, enumerate: true);
        try
        {
            if (!string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint) &&
                !string.Equals(lease.Identity.IdentityFingerprint, root.PhysicalIdentityFingerprint, StringComparison.Ordinal))
                throw new IOException("The admitted source-root identity changed; re-admit the folder before watching it.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static IReadOnlyList<string> GetWatchPaths(SourceRootConfiguration root) => GetWatchPaths(root, out _);

    private static IReadOnlyList<string> GetWatchPaths(SourceRootConfiguration root, out GitTrackedSourceDiscovery.Repository? repository)
    {
        repository = null;
        if (root.DiscoveryMode != SourceDiscoveryMode.GitTracked) return [root.CanonicalPath];
        repository = GitTrackedSourceDiscovery.ResolveRepository(root.CanonicalPath);
        if (root.RequiresPhysicalIdentityValidation && repository.Identity != root.RepositoryIdentityFingerprint)
            throw new IOException("Git watcher repository identity changed.");
        // Ordinary .git is already covered; linked worktrees also need their validated common control directory.
        return repository.CommonDirectory.StartsWith(root.CanonicalPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? [root.CanonicalPath] : [root.CanonicalPath, repository.CommonDirectory];
    }

    private sealed class CompositeDisposable(IReadOnlyList<IDisposable> registrations) : IDisposable
    {
        public void Dispose()
        {
            // Watchers close before their path leases, including a partially opened Git registration.
            for (var index = registrations.Count - 1; index >= 0; index--) registrations[index].Dispose();
        }
    }
}
