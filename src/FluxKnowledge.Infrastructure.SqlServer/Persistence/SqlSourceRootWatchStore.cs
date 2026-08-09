using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Persists advisory watcher hints; only released source controls drive enumeration.</summary>
public sealed class SqlSourceRootWatchStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider timeProvider) : ISourceRootWatchStore
{
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
            // A claim fences the current generation. Signals arriving while it is leased form a
            // fresh generation which ReleaseScanAsync will preserve after publishing the claim.
            var state = entity is null || entity.LeaseOwner is not null
                ? SourceWatchState.Empty(signal.RootId)
                : new SourceWatchState(signal.RootId, entity.FirstSignalAtUtc, entity.LastSignalAtUtc, entity.SignalCount, entity.DebounceGeneration, entity.DueAtUtc, entity.DueAtUtc <= entity.LastSignalAtUtc);
            var next = state.Observe(signal, QuietPeriod, MaximumDelay);
            if (entity is null) { entity = new SourceRootWatchStateEntity { SourceRootId = signal.RootId.Value }; context.SourceRootWatchStates.Add(entity); }
            var debounceGeneration = entity.LeaseOwner is null ? next.DebounceGeneration : checked(entity.DebounceGeneration + 1);
            entity.FirstSignalAtUtc = next.FirstSignalAtUtc!.Value; entity.LastSignalAtUtc = next.LastSignalAtUtc!.Value; entity.SignalCount = next.SignalCount; entity.DebounceGeneration = debounceGeneration; entity.DueAtUtc = next.DueAtUtc!.Value;
            if (signal.Kind == SourceWatchSignalKind.Overflow)
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
            result = new ClaimedSourceWatchBatch(new SourceRootId(state.SourceRootId), state.FirstSignalAtUtc, state.LastSignalAtUtc, state.SignalCount, state.DebounceGeneration, leaseOwner, state.LeaseGeneration);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken cancellationToken)
    {
        await ExecuteAsync(async context =>
        {
            var state = await context.SourceRootWatchStates.FromSqlInterpolated($"SELECT * FROM [SourceRootWatchStates] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceRootId] = {batch.SourceRootId.Value}").SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (state is null || state.LeaseGeneration != batch.LeaseGeneration || !string.Equals(state.LeaseOwner, batch.LeaseOwner, StringComparison.Ordinal)) throw new InvalidOperationException("The watch batch lease is no longer owned by this coordinator.");
            var now = timeProvider.GetUtcNow();
            // A running control is fenced to an earlier reconciliation snapshot.  A later watcher
            // generation must remain as a follow-up durable request rather than being folded into it.
            var existing = await context.SourceScanRequests.Where(request => request.SourceRootId == batch.SourceRootId.Value &&
                (request.State == (int)SourceScanRequestState.Held ||
                 (request.State == (int)SourceScanRequestState.Released && request.RequestKind != 2)))
                .OrderBy(request => request.RequestedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var requestId = existing?.Id ?? Guid.NewGuid();
            if (existing is null)
            {
                context.SourceScanRequests.Add(new SourceScanRequestEntity { Id = requestId, SourceRootId = batch.SourceRootId.Value, RequestKind = 2, RequestedBy = "watcher", RequestedAtUtc = now, IsReleased = true, ReleasedAtUtc = now, State = (int)SourceScanRequestState.Released });
                context.SourceScanJobs.Add(new SourceScanJobEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, State = (int)SourceScanJobState.Pending, DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
                context.SourceScanOutbox.Add(new SourceScanOutboxEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, Operation = "source.scan", IdempotencyKey = $"source-watch:{batch.SourceRootId.Value:N}:{batch.DebounceGeneration}", DueAtUtc = now, CreatedAtUtc = now });
            }
            else if (existing.State == (int)SourceScanRequestState.Held)
            {
                existing.IsReleased = true; existing.ReleasedAtUtc = now; existing.State = (int)SourceScanRequestState.Released;
                var job = await context.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == existing.Id, cancellationToken).ConfigureAwait(false);
                var outbox = await context.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == existing.Id, cancellationToken).ConfigureAwait(false);
                job.DueAtUtc = now; job.UpdatedAtUtc = now; outbox.DueAtUtc = now;
            }
            OperatorEventAppender.Add(context, new OperatorEventDraft("watch.batch_detected", "watch", "information", "source-watcher", now, SourceRootId: batch.SourceRootId.Value, SourceScanRequestId: requestId, CorrelationId: $"watch:{batch.SourceRootId.Value:N}:{batch.DebounceGeneration}", Details: new { kind = "batch" }));
            if (state.DebounceGeneration == batch.DebounceGeneration)
            {
                context.SourceRootWatchStates.Remove(state);
            }
            else
            {
                // A later signal replaced the leased payload. Keep its due time (including
                // overflow's immediate due) and make it claimable by the next pump iteration.
                state.LeaseOwner = null;
                state.LeaseExpiresAtUtc = null;
            }
        }, cancellationToken).ConfigureAwait(false);
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

    private static SourceRootConfiguration Restore(SourceRootConfigurationEntity root) => SourceRootConfiguration.Restore(new SourceRootId(root.Id), root.CanonicalPath, root.DisplayName, root.Recursive, root.FollowLinks, root.MaximumFileBytes, JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson), JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson), JsonSerializer.Deserialize<string[]>(root.AllowedClassificationsJson), TimeSpan.FromSeconds(root.ReconciliationCadenceSeconds), (SourceRootState)root.State, root.ConfigurationRevision, physicalIdentityFingerprint: SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson), requiresPhysicalIdentityValidation: true);
}
