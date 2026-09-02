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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (deploymentValidationHold ?? DeploymentValidationHold.None)
            .WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            IDisposable? watchers = null;
            try
            {
                watchers = await BuildWatchersAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Source-root watchers could not be restored; periodic reconciliation remains authoritative.");
            }
            try { await Task.Delay(RebuildCadence, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            watchers?.Dispose();
        }
    }

    private async Task<IDisposable> BuildWatchersAsync(CancellationToken cancellationToken)
    {
        var watchers = new List<FileSystemWatcher>();
        foreach (var root in await store.ReadEnabledRootsAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                Revalidate(root);
                var watcher = new FileSystemWatcher(root.CanonicalPath)
                {
                    IncludeSubdirectories = root.Recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, _) => Signal(root.Id, SourceWatchSignalKind.Created);
                watcher.Changed += (_, _) => Signal(root.Id, SourceWatchSignalKind.Changed);
                watcher.Deleted += (_, _) => Signal(root.Id, SourceWatchSignalKind.Deleted);
                watcher.Renamed += (_, _) => Signal(root.Id, SourceWatchSignalKind.Renamed);
                watcher.Error += (_, _) => Signal(root.Id, SourceWatchSignalKind.Overflow);
                watchers.Add(watcher);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Source-root watcher was not opened for {SourceRootId}; periodic reconciliation remains authoritative.", root.Id.Value);
                await RecordSafeAsync(new SourceWatchSignal(root.Id, SourceWatchSignalKind.Overflow, timeProvider.GetUtcNow())).ConfigureAwait(false);
            }
        }
        return new CompositeDisposable(watchers);
    }

    private void Signal(SourceRootId rootId, SourceWatchSignalKind kind) =>
        _ = RecordSafeAsync(new SourceWatchSignal(rootId, kind, timeProvider.GetUtcNow()));

    private async Task RecordSafeAsync(SourceWatchSignal signal)
    {
        try { await coordinator.RecordAsync(signal, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { logger.LogWarning(exception, "Source-root watcher hint could not be persisted for {SourceRootId}.", signal.RootId.Value); }
    }

    private static void Revalidate(SourceRootConfiguration root)
    {
        if (root.FollowLinks) throw new UnauthorizedAccessException("Source-root watchers do not follow links.");
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(root.CanonicalPath);
        var identity = PhysicalFileIdentity.GetDirectory(root.CanonicalPath);
        if (root.RequiresPhysicalIdentityValidation && !string.Equals(identity.IdentityFingerprint, root.PhysicalIdentityFingerprint, StringComparison.Ordinal)) throw new IOException("Source-root identity changed.");
    }

    private sealed class CompositeDisposable(IReadOnlyList<FileSystemWatcher> watchers) : IDisposable
    {
        public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); }
    }
}
