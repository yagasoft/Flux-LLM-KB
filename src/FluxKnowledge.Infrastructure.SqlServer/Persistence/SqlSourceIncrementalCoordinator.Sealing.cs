using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceIncrementalCoordinator
{
    internal Action? BeforeCaptureAttestation { get; init; }
    internal Action? BeforeCaptureAttestationCommit { get; init; }

    private async Task QueueUnsealedChecksAsync(CancellationToken ct)
    {
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roots = await lookup.SourceRootScanStates.Where(x => x.Mode != "Legacy").Select(x => x.SourceRootId).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var id in roots.Order())
            await _journal.ExecuteAsync(async db =>
            {
                var root = await db.SourceRootConfigurations.FromSqlInterpolated(
                    $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={id}").SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (root is null || root.State != (int)SourceRootState.Enabled) return;
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == id, ct).ConfigureAwait(false);
                await QueueUnsealedChecksAsync(db, root, state, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    private async Task QueueUnsealedChecksAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity root,
        SourceRootScanStateEntity state, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var profile = _qualifications.GetProfile(SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson), root.CanonicalPath);
        if (state.SealProfileId != profile)
        {
            // Revoke existing trust as well as future sealing. Watcher/reuse paths also
            // compare the current profile, so there is no trusted interval before this debt is queued.
            await db.SourceInventoryEntries.Where(x => x.SourceRootId == root.Id && !x.IsDirectory && x.BaselineEpoch == state.BaselineEpoch)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsSealed, false).SetProperty(x => x.NextForcedCheckAtUtc, now)
                    .SetProperty(x => x.SealFailureCode, "qualification-changed"), ct).ConfigureAwait(false);
            state.SealProfileId = profile;
        }
        var due = await db.SourceInventoryEntries.Where(x => x.SourceRootId == root.Id && !x.IsDirectory && !x.IsSealed &&
            x.BaselineEpoch == state.BaselineEpoch && (x.NextForcedCheckAtUtc == null || x.NextForcedCheckAtUtc <= now) &&
            db.SourceRevisions.Any(r => r.Id == x.SourceRevisionId && r.SuppressedAtUtc == null))
            .OrderBy(x => x.NextForcedCheckAtUtc).ThenBy(x => x.Id).Take(128).ToArrayAsync(ct).ConfigureAwait(false);
        if (due.Length == 0) return;
        var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id).ToListAsync(ct).ConfigureAwait(false);
        var generation = ++state.Generation;
        foreach (var entry in due)
        {
            var item = pending.SingleOrDefault(x => x.PathFingerprint == entry.PathFingerprint);
            if (item is null)
            {
                if (pending.Count >= 10000) { SqlSourceIncrementalState.InvalidateCoverage(state, "pending-path-overflow", state.CoveredUsn); break; }
                item = new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = entry.RelativePath,
                    PathFingerprint = entry.PathFingerprint, LastContentUsn = 0 };
                pending.Add(item); db.SourcePendingChanges.Add(item);
            }
            item.ReasonFlags |= 1; item.Generation = generation;
            item.LastContentUsn ??= item.LastUsn; item.LastUnjournaledGeneration = generation;
            item.FirstObservedAtUtc ??= now; item.NotBeforeUtc = now;
            entry.NextForcedCheckAtUtc = now.AddSeconds(root.ReconciliationCadenceSeconds);
        }
    }
}
