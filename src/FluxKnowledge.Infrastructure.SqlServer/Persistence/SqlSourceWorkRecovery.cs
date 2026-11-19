using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Retains process-local recovery progress across hosted service scopes.</summary>
public sealed class SqlSourceWorkRecoveryProgress
{
    internal System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> After { get; } = new();
    internal System.Collections.Concurrent.ConcurrentDictionary<SourceWorkAdmittedRoot, byte> AnnouncedAdmissions { get; } = new();
    private const int MaximumRediscoveryHints = 64;
    private readonly object _rediscoveryGate = new();
    private readonly HashSet<Guid> _rediscoveryRoots = [];
    private bool _rediscoveryOverflow;

    internal SemaphoreSlim RediscoveryOwner { get; } = new(1);
    internal RediscoverySweep? ActiveRediscovery { get; set; }
    internal DateTimeOffset NextRediscoveryFallback { get; set; }
    internal DateTimeOffset RediscoveryRetryAt { get; set; }

    // Signals only schedule a check. A committed capture and every existing
    // source and job ownership proof are still required by the recovery store.
    internal void NotifyRediscovery(Guid rootId)
    {
        lock (_rediscoveryGate)
        {
            if (_rediscoveryOverflow) return;
            _rediscoveryRoots.Add(rootId);
            if (_rediscoveryRoots.Count <= MaximumRediscoveryHints) return;
            _rediscoveryRoots.Clear();
            _rediscoveryOverflow = true;
        }
    }

    internal RediscoverySweep? StartRediscovery(DateTimeOffset now)
    {
        lock (_rediscoveryGate)
        {
            if (_rediscoveryOverflow || now >= NextRediscoveryFallback)
            {
                _rediscoveryOverflow = false;
                _rediscoveryRoots.Clear();
                return new(null);
            }
            if (_rediscoveryRoots.Count == 0) return null;
            var root = _rediscoveryRoots.First();
            _rediscoveryRoots.Remove(root);
            return new(root);
        }
    }

    internal sealed class RediscoverySweep(Guid? rootId)
    {
        internal Guid? RootId { get; } = rootId;
        internal Guid? After { get; set; }
    }
}

public sealed partial class SqlSourceWorkRecovery(IDbContextFactory<FluxKnowledgeDbContext> factory, SourceWorkAdmission admission,
    ISourceRemovalReconciler removals, ISourceObservationReader observations, TimeProvider clock,
    ISourceWorkRetirement? retirement = null, Guid? targetRootId = null,
    SqlSourceWorkRecoveryProgress? recoveryProgress = null, IGpuSchedulerWakeSignal? schedulerWake = null,
    IOutboxWakeSignal? outboxWake = null, IStatusEventPublisher? statusEvents = null) : ISourceWorkRecovery
{
    private readonly SqlSourceWorkRecoveryProgress _progress = recoveryProgress ?? new();

    public async ValueTask AdvanceAsync(CancellationToken cancellationToken)
    {
        await using var lookup = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var roots = await lookup.SourceRootConfigurations.AsNoTracking().Where(x => x.State == (int)SourceRootState.Enabled &&
                (targetRootId == null || x.Id == targetRootId))
            .OrderBy(x => x.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var outlook = await lookup.OutlookCaptureProfiles.AsNoTracking().Where(x => targetRootId == null || x.SourceRootId == targetRootId)
            .Select(x => x.SourceRootId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in roots)
        {
            if (admission.IsPrepared(root.Id, root.ConfigurationRevision)) continue;
            if (outlook.Contains(root.Id)) { admission.Admit(root.Id, root.ConfigurationRevision); continue; }
            var fullSweep = root.LastScanCompletedAtUtc is not { } completed ||
                completed.Add(SourcePolicyDefaults.MetadataReconciliationCadence) <= clock.GetUtcNow();
            try
            {
                var restored = SqlSourceRemovalReconciler.Restore(root);
                if (restored.PhysicalIdentityFingerprint is null) throw new IOException("SourceStartupIdentityUnavailable");
                using (var authority = observations.OpenRoot(restored)) authority.ValidateCurrent();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { continue; } // Keep admission closed until root identity/access can be proved.
            await PrepareScopesAsync(root, fullSweep, cancellationToken).ConfigureAwait(false);
            admission.MarkPrepared(root.Id, root.ConfigurationRevision);
        }
        await removals.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in roots)
        {
            if (!admission.IsPrepared(root.Id, root.ConfigurationRevision) || admission.IsAdmitted(root.Id, root.ConfigurationRevision)) continue;
            // Exact unresolved paths stay held in SQL while other paths can resume.
            if (!await lookup.SourcePendingChanges.AsNoTracking().AnyAsync(x => x.SourceRootId == root.Id && x.RelativePath == "." &&
                    (x.ReasonFlags & SourcePendingReasons.RemovalPending) != 0, cancellationToken).ConfigureAwait(false))
                admission.Admit(root.Id, root.ConfigurationRevision);
        }
        await AnnounceAdmissionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in roots.Where(x => x.CrawlMode == (int)SourceDiscoveryMode.GitTracked &&
            admission.IsAdmitted(x.Id, x.ConfigurationRevision)))
        {
            Guid? after = _progress.After.TryGetValue(root.Id, out var cursor) ? cursor : null;
            var next = await SqlGitTextSelection.RestorePublishedAsync(factory, root.Id, root.ConfigurationRevision,
                clock.GetUtcNow(), cancellationToken, after).ConfigureAwait(false);
            if (next is { } value) _progress.After[root.Id] = value;
            else _progress.After.TryRemove(root.Id, out _);
        }
        if (targetRootId is null)
            foreach (var id in _progress.After.Keys.Where(id => !roots.Any(root => root.Id == id)))
                _progress.After.TryRemove(id, out _);
        if (retirement is not null) await retirement.RetireAsync(cancellationToken).ConfigureAwait(false);
        // A standalone admission check owns only the exact command it serves.
        if (targetRootId is null) await ResumeRediscoveredExtractionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AnnounceAdmissionsAsync(CancellationToken ct)
    {
        var admitted = admission.ReadAdmittedRoots().ToHashSet();
        foreach (var prior in _progress.AnnouncedAdmissions.Keys.Where(prior => !admitted.Contains(prior)))
            _progress.AnnouncedAdmissions.TryRemove(prior, out _);
        var pending = admitted.Where(root => !_progress.AnnouncedAdmissions.ContainsKey(root)).ToArray();
        if (pending.Length == 0) return;
        // Startup admission can finish after the scheduler's no-candidate decision.
        // Publish a durable wake after admission opens; a local signal alone is not authority.
        await using var execution = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlGpuSchedulerStore.AcquireMutationLockAsync(db, transaction.GetDbTransaction(), ct).ConfigureAwait(false);
            await SqlGpuSchedulerStore.RecordWakeAsync(db, GpuSchedulerWakeReason.Reconciliation, clock.GetUtcNow(), ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        // A failed or uncertain commit leaves these exact revisions unacknowledged.
        foreach (var root in pending) _progress.AnnouncedAdmissions.TryAdd(root, 0);
        schedulerWake?.Notify(GpuSchedulerWakeReason.Reconciliation);
    }

    private Task PrepareScopesAsync(SourceRootConfigurationEntity expected, bool fullSweep, CancellationToken ct) =>
        new SqlSourceChangeJournalStore(factory, clock).ExecuteAsync(async db =>
        {
            var root = await db.SourceRootConfigurations.FromSqlInterpolated(
                $"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={expected.Id}").SingleAsync(ct).ConfigureAwait(false);
            if (root.State != (int)SourceRootState.Enabled || root.ConfigurationRevision != expected.ConfigurationRevision || root.CanonicalPath != expected.CanonicalPath)
                throw new SourceSnapshotChangedException("SourceStartupAuthorityChanged");
            var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id).ToListAsync(ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
            var generation = Math.Max(state?.Generation ?? 0, pending.Select(x => x.Generation).DefaultIfEmpty(0).Max()) + 1;
            if (state is not null) state.Generation = generation;
            foreach (var item in pending)
            {
                item.ReasonFlags |= SourcePendingReasons.RemovalPending;
                item.Generation = generation;
            }
            if (!fullSweep) return;
            var key = SqlSourceChangeJournalStore.PathKey(".");
            var wholeRoot = pending.SingleOrDefault(x => x.PathFingerprint == key);
            if (wholeRoot is null)
            {
                wholeRoot = new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = ".", PathFingerprint = key,
                    Generation = generation };
                if (state is not null) state.Generation = wholeRoot.Generation;
                db.SourcePendingChanges.Add(wholeRoot);
            }
            wholeRoot.IsSubtree = true;
            wholeRoot.Generation = generation;
            wholeRoot.ReasonFlags |= SourcePendingReasons.RemovalPending;
            wholeRoot.NotBeforeUtc = clock.GetUtcNow();
        }, ct);
}
