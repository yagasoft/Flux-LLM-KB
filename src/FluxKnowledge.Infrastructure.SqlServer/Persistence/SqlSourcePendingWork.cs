using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal sealed class SourcePendingCapacityException(bool pageTooLarge = false)
    : IOException(pageTooLarge ? "SourcePendingCapacityPageTooLarge" : "SourcePendingCapacityExceeded")
{
    internal bool PageTooLarge { get; } = pageTooLarge;
}

/// <summary>Bounds dirty-path debt without throwing away its content or removal obligations.</summary>
internal static class SqlSourcePendingWork
{
    internal const int MaximumPaths = 10000;

    internal static void RequireMetadata(FluxKnowledgeDbContext db, SourceRootScanStateEntity state,
        IList<SourcePendingChangeEntity> pending, DateTimeOffset now)
    {
        var key = SqlSourceChangeJournalStore.PathKey(".");
        var root = pending.SingleOrDefault(x => x.PathFingerprint == key);
        if (root is null)
        {
            // RecoveryRequired already records this metadata obligation. A full queue
            // keeps every specific event, and the weekly scheduler supplies the sweep.
            if (pending.Count >= MaximumPaths) return;
            root = new() { Id = Guid.NewGuid(), SourceRootId = state.SourceRootId, RelativePath = ".", PathFingerprint = key };
            db.SourcePendingChanges.Add(root);
            pending.Add(root);
        }
        if (root.RelativePath != ".") throw new InvalidDataException("SourcePendingRootPathCollision");
        root.ReasonFlags = (root.ReasonFlags & ~SourcePendingReasons.FullRecapture) |
            SourcePendingReasons.NamespaceMetadata | SourcePendingReasons.RemovalPending;
        root.IsSubtree = true;
        root.Generation = ++state.Generation;
        root.LastContentUsn ??= 0;
        root.LastUnjournaledGeneration ??= 0;
        root.FirstObservedAtUtc ??= now;
        root.NotBeforeUtc = now;
        root.RetryGeneration = null;
        state.NamespaceComplete = false;
        state.NamespaceGeneration++;
        if (state.Mode == "Journal") state.Mode = "JournalPartial";
    }
}
