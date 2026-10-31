using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Bounds dirty-path debt without throwing away its content or removal obligations.</summary>
internal static class SqlSourcePendingWork
{
    internal const int MaximumPaths = 10000;

    internal static bool HasFullRecapture(IEnumerable<SourcePendingChangeEntity> pending) =>
        pending.Any(x => x.RelativePath == "." && x.IsSubtree && (x.ReasonFlags & SourcePendingReasons.FullRecapture) != 0);

    internal static async Task CoalesceExistingAsync(FluxKnowledgeDbContext db, SourceRootScanStateEntity state,
        DateTimeOffset now, CancellationToken ct)
    {
        if (await db.SourcePendingChanges.CountAsync(x => x.SourceRootId == state.SourceRootId, ct).ConfigureAwait(false) < MaximumPaths) return;
        var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == state.SourceRootId).ToListAsync(ct).ConfigureAwait(false);
        if (!HasFullRecapture(pending)) Coalesce(db, state, pending, now);
    }

    internal static void Coalesce(FluxKnowledgeDbContext db, SourceRootScanStateEntity state,
        IList<SourcePendingChangeEntity> pending, DateTimeOffset now, long reasons = 0,
        long firstUsn = 0, long lastUsn = 0, long contentUsn = 0, bool unjournaled = false,
        DateTimeOffset? firstObserved = null)
    {
        var key = SqlSourceChangeJournalStore.PathKey(".");
        var root = pending.SingleOrDefault(x => x.PathFingerprint == key);
        var wasCoalesced = root is not null && (root.ReasonFlags & SourcePendingReasons.FullRecapture) != 0;
        var all = pending.ToArray();
        if (root is null)
        {
            root = new() { Id = Guid.NewGuid(), SourceRootId = state.SourceRootId, RelativePath = ".", PathFingerprint = key };
            db.SourcePendingChanges.Add(root);
        }
        if (root.RelativePath != ".") throw new InvalidDataException("SourcePendingRootPathCollision");
        var generation = ++state.Generation;
        root.ReasonFlags = all.Aggregate(reasons, (value, item) => value | item.ReasonFlags) |
            SourcePendingReasons.FullRecapture | SourcePendingReasons.NamespaceMetadata | SourcePendingReasons.RemovalPending;
        root.IsSubtree = true;
        root.Generation = generation;
        root.FirstUsn = all.Select(x => x.FirstUsn).Append(firstUsn).Where(x => x > 0).DefaultIfEmpty(0).Min();
        root.LastUsn = Math.Max(lastUsn, all.Select(x => x.LastUsn).DefaultIfEmpty(0).Max());
        root.LastContentUsn = Math.Max(contentUsn, all.Select(x => x.LastContentUsn ?? x.LastUsn).DefaultIfEmpty(0).Max());
        // An initial collapse has unknown per-path provenance. Later journal CLOSEs
        // must not move this boundary and invalidate the scan's own fresh captures.
        root.LastUnjournaledGeneration = !wasCoalesced || unjournaled ? generation :
            all.Select(x => x.LastUnjournaledGeneration ?? x.Generation).DefaultIfEmpty(0).Max();
        root.FirstObservedAtUtc = all.Select(x => x.FirstObservedAtUtc).Append(firstObserved ?? now)
            .Where(x => x is not null).Min();
        root.NotBeforeUtc = now;
        root.RetryGeneration = null;
        db.SourcePendingChanges.RemoveRange(all.Where(x => x != root));
        pending.Clear();
        pending.Add(root);
        if (!wasCoalesced || unjournaled || (reasons & (SourcePendingReasons.NamespaceMask | SourcePendingReasons.GitMembership | 0x3300)) != 0)
        {
            state.NamespaceComplete = false;
            state.NamespaceGeneration++;
        }
        if (state.Mode == "Journal") state.Mode = "JournalPartial";
    }
}
