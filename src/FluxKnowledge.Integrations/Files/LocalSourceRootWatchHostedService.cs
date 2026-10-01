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
    IDeploymentValidationHold? deploymentValidationHold = null) : BackgroundService
{
    private static readonly TimeSpan RebuildCadence = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PersistenceCadence = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PersistenceFailureDelay = TimeSpan.FromSeconds(30);
    private readonly LocalSourceWatchSignalBuffer _signals = new(coordinator, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                foreach (var watchPath in GetWatchPaths(root))
                {
                    var watcher = new FileSystemWatcher(watchPath)
                    {
                        IncludeSubdirectories = root.Recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                    };
                    watchers.Add(watcher);
                    watcher.Created += (_, _) => Signal(root.Id, SourceWatchSignalKind.Created);
                    watcher.Changed += (_, _) => Signal(root.Id, SourceWatchSignalKind.Changed);
                    watcher.Deleted += (_, _) => Signal(root.Id, SourceWatchSignalKind.Deleted);
                    watcher.Renamed += (_, _) => Signal(root.Id, SourceWatchSignalKind.Renamed);
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

    private static void Revalidate(SourceRootConfiguration root)
    {
        if (root.FollowLinks) throw new UnauthorizedAccessException("Source-root watchers do not follow links.");
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(root.CanonicalPath);
        var identity = PhysicalFileIdentity.GetDirectory(root.CanonicalPath);
        if (root.RequiresPhysicalIdentityValidation && !string.Equals(identity.IdentityFingerprint, root.PhysicalIdentityFingerprint, StringComparison.Ordinal)) throw new IOException("Source-root identity changed.");
    }

    public static IReadOnlyList<string> GetWatchPaths(SourceRootConfiguration root)
    {
        if (root.DiscoveryMode != SourceDiscoveryMode.GitTracked) return [root.CanonicalPath];
        var repository = GitTrackedSourceDiscovery.ResolveRepository(root.CanonicalPath);
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
