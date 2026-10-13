using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Best-effort local hint watcher. It carries root identifiers only; SQL reconciliation establishes truth.</summary>
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

    private async Task<IDisposable> BuildWatchersAsync(CancellationToken cancellationToken)
    {
        var watchers = new List<FileSystemWatcher>();
        var roots = await store.ReadEnabledRootsAsync(cancellationToken).ConfigureAwait(false);
        _signals.ConfigureRoots(roots.Select(root => root.Id));
        foreach (var root in roots)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Revalidate(root);
                var watchPaths = GetWatchPaths(root, out var repository);
                foreach (var watchPath in watchPaths)
                {
                    var watcher = new FileSystemWatcher(watchPath)
                    {
                        IncludeSubdirectories = root.Recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime |
                            (root.DiscoveryMode == SourceDiscoveryMode.GitTracked ? NotifyFilters.DirectoryName : 0)
                    };
                    watchers.Add(watcher);
                    watcher.Created += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Created, change, repository);
                    watcher.Changed += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Changed, change, repository);
                    watcher.Deleted += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Deleted, change, repository);
                    watcher.Renamed += (_, change) => SignalIfRelevant(root, SourceWatchSignalKind.Renamed, change, repository);
                    watcher.Error += (_, _) => Signal(root.Id, SourceWatchSignalKind.Overflow);
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                new CompositeDisposable(watchers).Dispose();
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Source-root watcher was not opened for {SourceRootId}; periodic reconciliation remains authoritative.", root.Id.Value);
                Signal(root.Id, SourceWatchSignalKind.Overflow);
            }
        }
        return new CompositeDisposable(watchers);
    }

    private void Signal(SourceRootId rootId, SourceWatchSignalKind kind) =>
        _signals.Signal(new SourceWatchSignal(rootId, kind, timeProvider.GetUtcNow()));

    private void SignalIfRelevant(SourceRootConfiguration root, SourceWatchSignalKind kind, FileSystemEventArgs change,
        GitTrackedSourceDiscovery.Repository? repository)
    {
        if (ShouldSignal(root, change, repository)) Signal(root.Id, kind);
    }

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

    private static void Revalidate(SourceRootConfiguration root)
    {
        if (root.FollowLinks) throw new UnauthorizedAccessException("Source-root watchers do not follow links.");
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(root.CanonicalPath);
        var identity = PhysicalFileIdentity.GetDirectory(root.CanonicalPath);
        if (root.RequiresPhysicalIdentityValidation && !string.Equals(identity.IdentityFingerprint, root.PhysicalIdentityFingerprint, StringComparison.Ordinal)) throw new IOException("Source-root identity changed.");
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

    private sealed class CompositeDisposable(IReadOnlyList<FileSystemWatcher> watchers) : IDisposable
    {
        public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); }
    }
}
