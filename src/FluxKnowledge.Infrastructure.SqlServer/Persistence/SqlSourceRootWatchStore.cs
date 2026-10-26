using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Persists advisory watcher hints; only released source controls drive enumeration.</summary>
public sealed class SqlSourceRootWatchStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider timeProvider,
    SourceJournalOptions? journalOptions = null) : ISourceRootWatchStore
{
    private SourceJournalOptions JournalOptions { get; } = journalOptions ?? new();
    private readonly WindowsSourceJournalSealQualification _sealQualifications = new(journalOptions ?? new());
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    public async ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var roots = await context.SourceRootConfigurations.AsNoTracking().Where(root => root.State == (int)SourceRootState.Enabled).ToListAsync(cancellationToken).ConfigureAwait(false);
        return roots.Select(Restore).ToArray();
    }

    public async ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken)
    {
        await ExecuteAsync(async context =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {signal.RootId.Value};", cancellationToken).ConfigureAwait(false);
            var root = await context.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == signal.RootId.Value, cancellationToken).ConfigureAwait(false);
            if (root is null || root.State != (int)SourceRootState.Enabled) return;
            var entity = await context.SourceRootWatchStates.FromSqlInterpolated($"SELECT * FROM [SourceRootWatchStates] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRootId] = {signal.RootId.Value}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var overflowWasRecorded = entity is not null && entity.LeaseOwner is null && entity.DueAtUtc <= entity.LastSignalAtUtc;
            // A normal debounce due time is always after its last signal.  The schema intentionally
            // avoids a separate overflow flag, so an immediate due time is the durable overflow marker.
            // The claimed batch carries an immutable hint snapshot. Keep the union durably until
            // acknowledgement, including while leased: a crash must not discard its old paths.
            // Replaying an old path in degraded mode is conservative; losing a later edit is not.
            var hints = new SourceWatchHintAccumulator(signal.RootId);
            if (entity is not null) hints.Add(ReadSignal(entity));
            hints.Add(signal);
            var merged = hints.Snapshot();
            var state = entity is null
                ? SourceWatchState.Empty(signal.RootId)
                : new SourceWatchState(signal.RootId, entity.FirstSignalAtUtc, entity.LastSignalAtUtc, entity.SignalCount, entity.DebounceGeneration, entity.DueAtUtc, entity.DueAtUtc <= entity.LastSignalAtUtc);
            var next = state.Observe(merged, QuietPeriod, MaximumDelay);
            if (entity is null) { entity = new SourceRootWatchStateEntity { SourceRootId = signal.RootId.Value }; context.SourceRootWatchStates.Add(entity); }
            var debounceGeneration = entity.LeaseOwner is null ? next.DebounceGeneration : checked(entity.DebounceGeneration + 1);
            entity.FirstSignalAtUtc = next.FirstSignalAtUtc!.Value; entity.LastSignalAtUtc = next.LastSignalAtUtc!.Value; entity.SignalCount = next.SignalCount; entity.DebounceGeneration = debounceGeneration; entity.DueAtUtc = next.DueAtUtc!.Value;
            entity.HintsJson = JsonSerializer.Serialize(new StoredHints(1, merged.Paths!.ToArray(), merged.PathsIncomplete,
                merged.RepositoryControlChanged, merged.Kind == SourceWatchSignalKind.Overflow));
            if (merged.Kind == SourceWatchSignalKind.Overflow)
            {
                entity.DueAtUtc = signal.ObservedAtUtc;
                if (!overflowWasRecorded)
                {
                    OperatorEventAppender.Add(context, new OperatorEventDraft("watch.overflow_detected", "watch", "warning", "source-watcher", signal.ObservedAtUtc, SourceRootId: signal.RootId.Value, CorrelationId: $"watch:{signal.RootId.Value:N}:{debounceGeneration}", Details: new { kind = "overflow" }));
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset nowUtc, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ClaimedSourceWatchBatch? result = null;
        await ExecuteAsync(async context =>
        {
            var state = await context.SourceRootWatchStates.FromSqlRaw("SELECT TOP (1) * FROM [SourceRootWatchStates] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [DueAtUtc] <= {0} AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= {0}) ORDER BY [DueAtUtc], [SourceRootId]", nowUtc).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (state is null) return;
            state.LeaseOwner = leaseOwner; state.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration); state.LeaseGeneration++;
            result = new ClaimedSourceWatchBatch(new SourceRootId(state.SourceRootId), state.FirstSignalAtUtc, state.LastSignalAtUtc, state.SignalCount, state.DebounceGeneration, leaseOwner, state.LeaseGeneration, ReadSignal(state));
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken cancellationToken)
    {
        await ExecuteAsync(async context =>
        {
            var root = await context.SourceRootConfigurations.FromSqlInterpolated($"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {batch.SourceRootId.Value}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var state = await context.SourceRootWatchStates.FromSqlInterpolated($"SELECT * FROM [SourceRootWatchStates] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRootId] = {batch.SourceRootId.Value}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (state is null || state.LeaseGeneration != batch.LeaseGeneration || !string.Equals(state.LeaseOwner, batch.LeaseOwner, StringComparison.Ordinal)) throw new InvalidOperationException("The watch batch lease is no longer owned by this coordinator.");
            if (root is null || root.State != (int)SourceRootState.Enabled)
            {
                context.SourceRootWatchStates.Remove(state);
                return;
            }
            var now = timeProvider.GetUtcNow();
            if (await ReleaseJournalHintsAsync(context, root, batch, now, cancellationToken).ConfigureAwait(false))
            {
                AcknowledgeBatch(context, state, batch);
                return;
            }
            // A running control is fenced to an earlier reconciliation snapshot.  A later watcher
            // generation must remain as a follow-up durable request rather than being folded into it.
            var existing = await context.SourceScanRequests.Where(request => request.SourceRootId == batch.SourceRootId.Value &&
                request.RequestKind != SqlSourceVerificationStore.RequestKind &&
                (request.State == (int)SourceScanRequestState.Held ||
                 (request.State == (int)SourceScanRequestState.Released && request.RequestKind != 2)))
                .OrderBy(request => request.RequestedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var requestId = existing?.Id ?? Guid.NewGuid();
            if (existing is null)
            {
                context.SourceScanRequests.Add(new SourceScanRequestEntity { Id = requestId, SourceRootId = batch.SourceRootId.Value, RequestKind = 2, RequestedBy = "watcher", RequestedAtUtc = now, IsReleased = true, ReleasedAtUtc = now, State = (int)SourceScanRequestState.Released });
                context.SourceScanJobs.Add(new SourceScanJobEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, State = (int)SourceScanJobState.Pending, DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
                context.SourceScanOutbox.Add(new SourceScanOutboxEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, Operation = "source.scan", IdempotencyKey = $"source-scan:{requestId:N}", DueAtUtc = now, CreatedAtUtc = now });
            }
            else if (existing.State == (int)SourceScanRequestState.Held)
            {
                existing.IsReleased = true; existing.ReleasedAtUtc = now; existing.State = (int)SourceScanRequestState.Released;
                var job = await context.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == existing.Id, cancellationToken).ConfigureAwait(false);
                var outbox = await context.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == existing.Id, cancellationToken).ConfigureAwait(false);
                job.DueAtUtc = now; job.UpdatedAtUtc = now; outbox.DueAtUtc = now;
            }
            OperatorEventAppender.Add(context, new OperatorEventDraft("watch.batch_detected", "watch", "information", "source-watcher", now, SourceRootId: batch.SourceRootId.Value, SourceScanRequestId: requestId, CorrelationId: $"watch:{batch.SourceRootId.Value:N}:{batch.DebounceGeneration}", Details: new { kind = "batch" }));
            AcknowledgeBatch(context, state, batch);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void AcknowledgeBatch(FluxKnowledgeDbContext context, SourceRootWatchStateEntity state, ClaimedSourceWatchBatch batch)
    {
        if (state.DebounceGeneration == batch.DebounceGeneration) context.SourceRootWatchStates.Remove(state);
        else
        {
            // Later hints, including their conservative union with unacknowledged paths,
            // remain durable and claimable after this immutable batch is acknowledged.
            state.LeaseOwner = null;
            state.LeaseExpiresAtUtc = null;
        }
    }

    private async Task<bool> ReleaseJournalHintsAsync(FluxKnowledgeDbContext context, SourceRootConfigurationEntity root,
        ClaimedSourceWatchBatch batch, DateTimeOffset now, CancellationToken ct)
    {
        if (!JournalOptions.Enabled) return false;
        var scan = await context.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
        if (scan is null) return false;
        var signal = batch.Signal ?? new(batch.SourceRootId, SourceWatchSignalKind.Overflow, batch.LastSignalAtUtc);
        var membership = signal.RepositoryControlChanged && root.CrawlMode == (int)SourceDiscoveryMode.GitTracked;
        var point = await context.SourceVolumeJournalCheckpoints.SingleOrDefaultAsync(x => x.VolumeIdentity == scan.VolumeIdentity, ct).ConfigureAwait(false);
        var healthy = point is not null && point.UnavailableSinceUtc is null && JournalOptions.ApprovedVolumes.Contains(scan.VolumeIdentity, StringComparer.OrdinalIgnoreCase) &&
            (scan.Mode is "Journal" or "JournalPartial" || scan.Mode == "Baselining" && scan.RecoveryInProgress) &&
            scan.PolicyFingerprint == SqlRepositoryWorkRecovery.Policy(root);
        var hinted = signal.Paths ?? [];
        var profile = _sealQualifications.GetProfile(SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson), root.CanonicalPath);
        var keys = hinted.Select(x => SqlSourceChangeJournalStore.PathKey(x.RelativePath)).ToArray();
        var sealedPaths = healthy && profile is not null
            ? await context.SourceInventoryEntries.Where(x => x.SourceRootId == root.Id && x.BaselineEpoch == scan.BaselineEpoch &&
                !x.IsDirectory && x.IsSealed && x.SealProfileId == profile && keys.Contains(x.PathFingerprint))
                .Select(x => x.PathFingerprint).ToArrayAsync(ct).ConfigureAwait(false) : [];
        IReadOnlyList<SourceWatchPathHint> paths = hinted.Where(x => !healthy || x.IsSubtree ||
            !sealedPaths.Contains(SqlSourceChangeJournalStore.PathKey(x.RelativePath), StringComparer.Ordinal)).ToArray();
        var uncertain = signal.PathsIncomplete || hinted.Count == 0 && !membership;
        if (healthy && paths.Count == 0 && !uncertain && !membership)
        {
            OperatorEventAppender.Add(context, new OperatorEventDraft("watch.batch_detected", "watch", "information", "source-watcher", now,
                SourceRootId: root.Id, CorrelationId: $"watch:{root.Id:N}:{batch.DebounceGeneration}", Details: new { kind = "journal-advisory" }));
            return true;
        }
        var pending = await context.SourcePendingChanges.Where(x => x.SourceRootId == root.Id).ToListAsync(ct).ConfigureAwait(false);
        var incoming = paths.Select(x => SqlSourceChangeJournalStore.PathKey(x.RelativePath)).ToHashSet(StringComparer.Ordinal);
        if (membership || uncertain) incoming.Add(SqlSourceChangeJournalStore.PathKey("."));
        if (pending.Count + incoming.Count(key => !pending.Any(x => x.PathFingerprint == key)) > 10000)
        {
            SqlSourceIncrementalState.InvalidateCoverage(scan, "pending-path-overflow", point?.NextUsn ?? scan.CoveredUsn);
            return true;
        }
        var generation = ++scan.Generation;
        foreach (var hint in paths) Merge(hint.RelativePath,
            healthy && hint.IsSubtree ? SourcePendingReasons.ScopedNamespace : 1, hint.IsSubtree, now);
        if (membership) Merge(".", SourcePendingReasons.GitMembership, false, now);
        if (uncertain)
        {
            scan.NamespaceComplete = false; scan.NamespaceGeneration++;
            scan.UnresolvedThroughUsn ??= scan.CoveredUsn;
            if (scan.Mode == "Journal") scan.Mode = "JournalPartial";
            var due = (scan.LastReconciliationAtUtc ?? batch.FirstSignalAtUtc).AddSeconds(root.ReconciliationCadenceSeconds);
            Merge(".", SourcePendingReasons.NamespaceMetadata, true, due > now ? due : now);
        }
        OperatorEventAppender.Add(context, new OperatorEventDraft("watch.batch_detected", "watch", "information", "source-watcher", now,
            SourceRootId: root.Id, CorrelationId: $"watch:{root.Id:N}:{batch.DebounceGeneration}", Details: new { kind = membership ? "membership-pending" : "degraded-pending", paths = paths.Count, uncertain }));
        return true;

        void Merge(string relativePath, long reasons, bool subtree, DateTimeOffset due)
        {
            var path = SqlSourceChangeJournalStore.NormaliseRelative(relativePath);
            var key = SqlSourceChangeJournalStore.PathKey(path);
            var item = pending.SingleOrDefault(x => x.PathFingerprint == key);
            if (item is not null && !item.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SourceWatchPathCollision");
            if (item is null)
            {
                item = new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = path, PathFingerprint = key,
                    LastContentUsn = 0, LastUnjournaledGeneration = 0 };
                pending.Add(item); context.SourcePendingChanges.Add(item);
            }
            item.ReasonFlags |= reasons;
            item.IsSubtree |= subtree;
            item.Generation = generation;
            if (new JournalChange(0, "", "", "", (uint)reasons, false).ContentChanged) item.LastUnjournaledGeneration = generation;
            item.FirstObservedAtUtc ??= batch.FirstSignalAtUtc;
            if (item.NotBeforeUtc is null || item.NotBeforeUtc > due) item.NotBeforeUtc = due;
        }
    }

    private sealed record StoredHints(int Version, SourceWatchPathHint[] Paths, bool Incomplete, bool RepositoryControlChanged, bool Overflow);

    private static SourceWatchSignal ReadSignal(SourceRootWatchStateEntity state)
    {
        StoredHints? payload = null;
        try { if (state.HintsJson is { } json) payload = JsonSerializer.Deserialize<StoredHints>(json); }
        catch (JsonException) { }
        if (payload is not { Version: 1, Paths.Length: <= 512 })
            return new(new(state.SourceRootId), SourceWatchSignalKind.Overflow, state.LastSignalAtUtc,
                PathsIncomplete: true, FirstObservedAtUtc: state.FirstSignalAtUtc);
        // Revalidate bounded relative paths on every trust-boundary crossing.
        var hints = new SourceWatchHintAccumulator(new(state.SourceRootId));
        hints.Add(new(new(state.SourceRootId), payload.Overflow ? SourceWatchSignalKind.Overflow : SourceWatchSignalKind.Changed,
            state.LastSignalAtUtc, payload.Paths, payload.Incomplete, payload.RepositoryControlChanged, state.FirstSignalAtUtc));
        return hints.Snapshot();
    }

    private async Task ExecuteAsync(Func<FluxKnowledgeDbContext, Task> action, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await action(context).ConfigureAwait(false); await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static SourceRootConfiguration Restore(SourceRootConfigurationEntity root) => SourceRootConfiguration.Restore(new SourceRootId(root.Id), root.CanonicalPath, root.DisplayName, root.Recursive, root.FollowLinks, root.MaximumFileBytes, JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson), JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson), JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson), TimeSpan.FromSeconds(root.ReconciliationCadenceSeconds), (SourceRootState)root.State, root.ConfigurationRevision, physicalIdentityFingerprint: SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson), requiresPhysicalIdentityValidation: true, discoveryMode: (SourceDiscoveryMode)root.CrawlMode, repositoryIdentityFingerprint: SqlSourceScanStore.ParseGitAdmissionIdentity(root.HealthEvidenceJson));
}
