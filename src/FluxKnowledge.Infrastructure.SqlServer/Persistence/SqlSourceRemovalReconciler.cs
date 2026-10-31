using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Diagnostics;
using System.Text.Json;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceRemovalReconciler(IDbContextFactory<FluxKnowledgeDbContext> factory,
    TimeProvider clock, ISourceObservationReader observations, Guid? targetRootId = null) : ISourceRemovalReconciler
{
    private const int PageSize = 128;
    private readonly SemaphoreSlim _owner = new(1, 1);
    private readonly Dictionary<Guid, Progress> _progress = [];
    private readonly Dictionary<Guid, DateTimeOffset> _retryAfter = [];
    private readonly Dictionary<Guid, Guid> _scopeAfter = [];
    private int _rootTurn;
    private sealed class Progress(SourcePendingChangeEntity scope)
    {
        internal SourcePendingChangeEntity Scope { get; } = scope;
        internal Guid? After { get; set; }
        internal HashSet<string> Uncertain { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _owner.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return 0;
        try
        {
            var count = 0;
            var budget = Stopwatch.StartNew();
            await using var lookup = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var roots = await lookup.SourcePendingChanges.AsNoTracking().Where(x =>
                    (targetRootId == null || x.SourceRootId == targetRootId) &&
                    (x.ReasonFlags & SourcePendingReasons.RemovalPending) != 0 &&
                    (x.ReasonFlags != SourcePendingReasons.RemovalPending || x.NotBeforeUtc == null || x.NotBeforeUtc <= now))
                .Select(x => x.SourceRootId).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
            Array.Sort(roots);
            for (var turn = 0; turn < roots.Length && (turn == 0 || budget.Elapsed < TimeSpan.FromSeconds(1)); turn++)
            {
                var id = roots[(_rootTurn + turn) % roots.Length];
                var root = await lookup.SourceRootConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
                if (root is null || root.State != (int)SourceRootState.Enabled) continue;
                var scopesQuery = lookup.SourcePendingChanges.AsNoTracking().Where(x => x.SourceRootId == id &&
                        (x.ReasonFlags & SourcePendingReasons.RemovalPending) != 0 &&
                        (x.ReasonFlags != SourcePendingReasons.RemovalPending || x.NotBeforeUtc == null || x.NotBeforeUtc <= now));
                var scopes = await (_scopeAfter.TryGetValue(id, out var last) ? scopesQuery.Where(x => x.Id.CompareTo(last) > 0) : scopesQuery)
                    .OrderBy(x => x.Id).Take(64).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (scopes.Length == 0)
                {
                    _scopeAfter.Remove(id);
                    scopes = await scopesQuery.OrderBy(x => x.Id).Take(64).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                foreach (var scope in scopes)
                {
                    if (budget.Elapsed > TimeSpan.FromSeconds(1)) break;
                    _scopeAfter[id] = scope.Id;
                    if (_retryAfter.TryGetValue(scope.Id, out var retry) && retry > now) continue;
                    _retryAfter.Remove(scope.Id);
                    // Finish the frozen pass even when intake advances its generation.
                    // Suppression has fresh physical proof; only acknowledgement requires
                    // the exact generation. Restarting here could starve later paths forever.
                    if (!_progress.TryGetValue(scope.Id, out var progress))
                        _progress[scope.Id] = progress = new(scope);
                    try { count += await ReconcilePageAsync(root, progress, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _progress.Remove(scope.Id); _retryAfter[scope.Id] = now.AddSeconds(5); } // No proof: retain the durable hold.
                }
            }
            if (roots.Length > 0) _rootTurn = (_rootTurn + 1) % roots.Length;
            foreach (var id in _scopeAfter.Keys.Where(id => !roots.Contains(id)).ToArray()) _scopeAfter.Remove(id);
            var live = await lookup.SourcePendingChanges.AsNoTracking().Where(x =>
                    (x.ReasonFlags & SourcePendingReasons.RemovalPending) != 0 &&
                    (targetRootId == null || x.SourceRootId == targetRootId))
                .Select(x => x.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var liveIds = live.ToHashSet();
            foreach (var id in _progress.Keys.Where(id => !liveIds.Contains(id)).ToArray()) _progress.Remove(id);
            foreach (var id in _retryAfter.Keys.Where(id => !liveIds.Contains(id)).ToArray()) _retryAfter.Remove(id);
            return count;
        }
        finally { _owner.Release(); }
    }

    private async Task<int> ReconcilePageAsync(SourceRootConfigurationEntity rootRow, Progress progress, CancellationToken ct)
    {
        var root = Restore(rootRow);
        var scope = progress.Scope;
        var relative = SqlSourceChangeJournalStore.NormaliseRelative(scope.RelativePath);
        if (root.PhysicalIdentityFingerprint is null) throw new IOException("RemovalRootIdentityUnavailable");
        using (var authority = observations.OpenRoot(root)) authority.ValidateCurrent();
        await using var lookup = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = lookup.SourceRevisions.AsNoTracking().Where(x => x.SourceRootId == root.Id.Value && x.OriginKind == 0 && x.SuppressedAtUtc == null);
        if (relative != ".")
        {
            var full = Path.GetFullPath(relative, root.CanonicalPath);
            var prefix = full + Path.DirectorySeparatorChar;
            query = scope.IsSubtree
                ? query.Where(x => EF.Functions.Collate(x.CanonicalPath, "Latin1_General_100_CI_AS") == full ||
                    EF.Functions.Collate(x.CanonicalPath, "Latin1_General_100_CI_AS").StartsWith(prefix))
                : query.Where(x => EF.Functions.Collate(x.CanonicalPath, "Latin1_General_100_CI_AS") == full);
        }
        if (progress.After is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
        var candidates = await query.OrderBy(x => x.Id).Take(PageSize).Select(x => x.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var count = 0;
        foreach (var group in candidates.Chunk(16))
        {
            await using var execution = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            count += await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
                await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
                await RequireRootAsync(db, rootRow, ct).ConfigureAwait(false);
                using var authority = observations.OpenRoot(root);
                var leases = new List<ISourceAbsenceLease>();
                var affected = new List<IndexAffectedScope>();
                try
                {
                    var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
                    var journalId = state is null ? null : await db.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == state.VolumeIdentity)
                        .Select(x => x.JournalId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                    foreach (var revision in await db.SourceRevisions.Where(x => group.Contains(x.Id) && x.SourceRootId == root.Id.Value &&
                                 x.OriginKind == 0 && x.SuppressedAtUtc == null).ToArrayAsync(ct).ConfigureAwait(false))
                    {
                        var path = Path.GetRelativePath(root.CanonicalPath, revision.CanonicalPath);
                        if (!SourcePathScope.Contains(relative, scope.IsSubtree, path)) throw new SourceSnapshotChangedException("RemovalScopeChanged");
                        ISourceAbsenceLease? absence;
                        try { absence = observations.OpenAbsence(root, path); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { progress.Uncertain.Add(path); continue; }
                        if (absence is null) continue;
                        leases.Add(absence); absence.ValidateCurrent();
                        var now = clock.GetUtcNow();
                        revision.SuppressedAtUtc = now;
                        revision.RetainUntilUtc = now.AddDays(30);
                        revision.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Withdraw();
                        var pathKey = SqlSourceChangeJournalStore.PathKey(path);
                        var inventory = await db.SourceInventoryEntries.SingleOrDefaultAsync(x => x.SourceRootId == root.Id.Value &&
                            x.PathFingerprint == pathKey && x.SourceRevisionId == revision.Id && !x.IsDirectory, ct).ConfigureAwait(false);
                        revision.RetentionEvidenceJson = JsonSerializer.Serialize(new SourceGuardedRemovalWitness(2, "guarded-path-removal",
                            root.Id.Value, revision.Id, path, root.ConfigurationRevision, root.PhysicalIdentityFingerprint,
                            scope.Id, scope.Generation, now, state?.BaselineEpoch ?? 0, state?.RecoveryGeneration ?? 0,
                            journalId, state?.CoveredUsn ?? 0, inventory?.Id, inventory?.FileId, inventory?.SourceArtifactId));
                        affected.Add(new(3, root.Id.Value));
                        OperatorEventAppender.Add(db, OperatorEventDraft.SourceRemoved(root.Id.Value, revision.Id, $"source:{revision.Id:N}",
                            new { reason = "guarded-path-removal", pendingId = scope.Id, generation = scope.Generation }));
                    }
                    if (affected.Count > 0) await SqlPublishedPassageSelection.AdvanceVersionAsync(db, clock.GetUtcNow(), ct, affected).ConfigureAwait(false);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    authority.ValidateCurrent();
                    foreach (var lease in leases) lease.ValidateCurrent();
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return affected.Count;
                }
                finally { foreach (var lease in leases) lease.Dispose(); }
            }).ConfigureAwait(false);
        }
        if (candidates.Length > 0) progress.After = candidates[^1];
        if (candidates.Length < PageSize)
        {
            await AcknowledgeAsync(rootRow, progress, ct).ConfigureAwait(false);
            _progress.Remove(scope.Id);
        }
        return count;
    }

    private async Task AcknowledgeAsync(SourceRootConfigurationEntity root, Progress progress, CancellationToken ct)
    {
        await using var execution = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
            await RequireRootAsync(db, root, ct).ConfigureAwait(false);
            using var authority = observations.OpenRoot(Restore(root));
            var current = await db.SourcePendingChanges.SingleOrDefaultAsync(x => x.Id == progress.Scope.Id, ct).ConfigureAwait(false);
            if (current is null || current.Generation != progress.Scope.Generation) return;
            current.ReasonFlags &= ~SourcePendingReasons.RemovalPending;
            var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
            if (progress.Uncertain.Count > 0 && await db.SourcePendingChanges.CountAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false)
                    + progress.Uncertain.Count > SqlSourcePendingWork.MaximumPaths)
            {
                // Too many uncertain paths remain represented by the existing broader hold.
                current.ReasonFlags |= SourcePendingReasons.RemovalPending;
                current.NotBeforeUtc = clock.GetUtcNow().AddSeconds(5);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                authority.ValidateCurrent();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            foreach (var path in progress.Uncertain)
            {
                var key = SqlSourceChangeJournalStore.PathKey(path);
                var item = await db.SourcePendingChanges.SingleOrDefaultAsync(x => x.SourceRootId == root.Id && x.PathFingerprint == key, ct).ConfigureAwait(false);
                if (item is null)
                {
                    item = new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = path, PathFingerprint = key };
                    db.SourcePendingChanges.Add(item);
                }
                item.ReasonFlags |= SourcePendingReasons.RemovalPending;
                item.Generation = state is null ? Math.Max(item.Generation, current.Generation) + 1 :
                    state.Generation = Math.Max(state.Generation, Math.Max(item.Generation, current.Generation)) + 1;
                item.NotBeforeUtc = clock.GetUtcNow().AddSeconds(5);
            }
            if (current.ReasonFlags == 0) db.SourcePendingChanges.Remove(current);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            authority.ValidateCurrent();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task RequireRootAsync(FluxKnowledgeDbContext db, SourceRootConfigurationEntity expected, CancellationToken ct)
    {
        var current = await db.SourceRootConfigurations.FromSqlInterpolated(
            $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={expected.Id}").SingleAsync(ct).ConfigureAwait(false);
        if (current.State != (int)SourceRootState.Enabled || current.ConfigurationRevision != expected.ConfigurationRevision ||
            current.CanonicalPath != expected.CanonicalPath ||
            SqlSourceScanStore.ParseAdmissionIdentityFingerprint(current.HealthEvidenceJson) != SqlSourceScanStore.ParseAdmissionIdentityFingerprint(expected.HealthEvidenceJson))
            throw new SourceSnapshotChangedException("RemovalRootAuthorityChanged");
    }

    internal static SourceRootConfiguration Restore(SourceRootConfigurationEntity root) => SourceRootConfiguration.Restore(
        new SourceRootId(root.Id), root.CanonicalPath, root.DisplayName, root.Recursive, root.FollowLinks, root.MaximumFileBytes,
        JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson), JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson),
        JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson), TimeSpan.FromSeconds(root.ReconciliationCadenceSeconds),
        (SourceRootState)root.State, root.ConfigurationRevision, physicalIdentityFingerprint: SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson),
        requiresPhysicalIdentityValidation: true, discoveryMode: (SourceDiscoveryMode)root.CrawlMode,
        repositoryIdentityFingerprint: SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson));
}
