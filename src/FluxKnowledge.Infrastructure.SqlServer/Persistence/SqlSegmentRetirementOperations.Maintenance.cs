using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentRetirementOperations : IIndexProjectionMaintenance
{
    private readonly SemaphoreSlim _maintenanceRun = new(1, 1);
    private Guid? _obsoleteManifestAfter, _pendingManifestAfter;
    private string? _orphanAfter, _pendingSegmentAfter;
    public ValueTask<IndexProjectionMaintenanceResult> RunOnceAsync(CancellationToken cancellationToken) =>
        RunOnceAsync(new IndexProjectionMaintenanceBudget(), cancellationToken);

    public async ValueTask<IndexProjectionMaintenanceResult> RunOnceAsync(IndexProjectionMaintenanceBudget budget, CancellationToken cancellationToken)
    {
        budget.Validate();
        if (!await _maintenanceRun.WaitAsync(0, cancellationToken)) return new(0, 0, 0, "segment-retirement-maintenance-active", WorkRemaining: true);
        try { return await RunMaintenanceCoreAsync(budget, cancellationToken); }
        finally { _maintenanceRun.Release(); }
    }

    private async ValueTask<IndexProjectionMaintenanceResult> RunMaintenanceCoreAsync(IndexProjectionMaintenanceBudget budget, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(budget.MaximumSeconds));
        var token = deadline.Token;
        var segments = 0; long bytes = 0, attemptedBytes = 0; var manifests = 0;
        var workRemaining = true; // Unknown observation never acknowledges a drained queue.
        string? reason = null;
        try
        {
            await using var context = await contexts.CreateDbContextAsync(token);
            var state = await context.IndexState.AsNoTracking().SingleAsync(token);
            if (state.RequiredIndexFormat != 2) return new(0, 0, 0);
            await MaintainCatalogueAsync(budget, token);
            var obsolete = await NextManifestsAsync(DueManifests(context, state.ActiveIndexGenerationId, budget, clock.GetUtcNow()),
                _obsoleteManifestAfter, token);
            _obsoleteManifestAfter = obsolete.LastOrDefault()?.Id;
            foreach (var row in obsolete)
            {
                try { await MarkManifestAsync(row.Id, Guid.NewGuid(), token); }
                catch (InvalidOperationException exception) when (IsRetirementBlocker(exception)) { reason = exception.Message; }
            }

            // Include sealed orphans left by an acknowledged failed candidate. Unknown
            // producers and unfinished candidates remain pins, never age-based proof.
            var orphans = await NextSegmentsAsync(DueOrphans(context, budget, clock.GetUtcNow()), _orphanAfter, budget.MaximumOrphanMarks, token);
            _orphanAfter = orphans.LastOrDefault()?.Id;
            foreach (var row in orphans)
            {
                try { await MarkUnreferencedSegmentAsync(row.Id, Guid.NewGuid(), token); }
                catch (InvalidOperationException exception) when (IsRetirementBlocker(exception)) { reason = exception.Message; }
            }
            var pending = await NextSegmentsAsync(context.IndexSegments.AsNoTracking().Where(value => value.State == 2 && value.RetirementOperationId != null),
                _pendingSegmentAfter, budget.MaximumSegments, token);
            _pendingSegmentAfter = pending.LastOrDefault()?.Id;
            foreach (var target in pending)
            {
                var readCost = files.MaximumSegmentVerificationBytes(SqlSegmentedIndexStore.Describe(target));
                if (readCost <= 0 || readCost > budget.MaximumBytes) { reason ??= "segment-retirement-byte-budget"; continue; }
                if (readCost > budget.MaximumBytes - attemptedBytes) continue;
                attemptedBytes += readCost;
                try
                {
                    if (await TryReclaimSegmentAsync(target.Id, target.RetirementOperationId!.Value, token)) { segments++; bytes += target.FileLength; }
                    else reason ??= "segment-retirement-owner-or-reference-active";
                }
                catch (InvalidOperationException exception) when (IsRetirementBlocker(exception)) { reason = exception.Message; }
            }
            var pendingManifests = await NextManifestsAsync(context.IndexGenerations.AsNoTracking().Where(value => value.FormatVersion == 2 &&
                value.RetirementOperationId != null && value.ReclaimedAtUtc == null), _pendingManifestAfter, token);
            _pendingManifestAfter = pendingManifests.LastOrDefault()?.Id;
            foreach (var target in pendingManifests)
            {
                var readCost = files.MaximumManifestVerificationBytes(SqlSegmentedIndexStore.Describe(target));
                if (readCost <= 0 || readCost > budget.MaximumBytes) { reason ??= "segment-retirement-byte-budget"; continue; }
                if (readCost > budget.MaximumBytes - attemptedBytes) continue;
                attemptedBytes += readCost;
                try
                {
                    if (await TryReclaimManifestAsync(target.Id, target.RetirementOperationId!.Value, token)) manifests++;
                    else reason ??= "segment-retirement-manifest-pending";
                }
                catch (InvalidOperationException exception) when (IsRetirementBlocker(exception)) { reason = exception.Message; }
            }
            workRemaining = await ObserveWorkRemainingAsync(budget, token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { reason ??= "segment-retirement-time-budget"; }
        return new(segments, bytes, manifests, reason, attemptedBytes, workRemaining);
    }

    private static IQueryable<IndexGenerationEntity> DueManifests(FluxKnowledgeDbContext context, Guid? active,
        IndexProjectionMaintenanceBudget budget, DateTimeOffset now)
    {
        var cutoff = now.AddHours(-budget.MaximumHistoryHours);
        var recent = context.IndexGenerations.Where(value => value.FormatVersion == 2 && value.Id != active && value.RetiredAtUtc == null)
            .OrderByDescending(value => value.CreatedAtUtc).ThenByDescending(value => value.Id)
            .Take(budget.RetainedInactiveManifests).Select(value => value.Id);
        return context.IndexGenerations.AsNoTracking().Where(value => value.FormatVersion == 2 && value.Id != active &&
            value.EmbeddingJobId == null && value.RetiredAtUtc == null && (value.CreatedAtUtc <= cutoff || !recent.Contains(value.Id)));
    }

    private static IQueryable<IndexSegmentEntity> DueOrphans(FluxKnowledgeDbContext context, IndexProjectionMaintenanceBudget budget, DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-budget.OrphanGraceMinutes);
        return context.IndexSegments.AsNoTracking().Where(value => value.State == 1 && value.CreatedAtUtc <= cutoff &&
            value.ProducingCandidateId != null && context.IndexPublicationCandidates.Any(candidate => candidate.Id == value.ProducingCandidateId && candidate.State >= 2) &&
            !context.IndexGenerationSegments.Any(member => member.SegmentId == value.Id) && !context.CurrentIndexMembers.Any(member => member.SegmentId == value.Id));
    }

    private async Task<bool> ObserveWorkRemainingAsync(IndexProjectionMaintenanceBudget budget, CancellationToken token)
    {
        // Observe all queues, not merely the rotated page. Short shared recovery
        // admission then publication fence prevents an in-flight state transfer
        // between eligible and retirement-requested queues from looking drained.
        await using var context = await NewFileOperationContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await SqlSegmentedIndexStore.AcquireSharedTransactionAsync(context, token);
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, token);
        var state = await context.IndexState.AsNoTracking().SingleAsync(token);
        var now = clock.GetUtcNow();
        var remaining = state.RequiredIndexFormat == 2 &&
            (await DueManifests(context, state.ActiveIndexGenerationId, budget, now).AnyAsync(token) ||
             await DueOrphans(context, budget, now).AnyAsync(token) ||
             await context.IndexSegments.AnyAsync(value => value.State == 2, token) ||
             await context.IndexGenerations.AnyAsync(value => value.FormatVersion == 2 && value.RetirementOperationId != null && value.ReclaimedAtUtc == null, token) ||
             await CatalogueWorkRemainingAsync(context, state, token));
        await transaction.CommitAsync(token);
        return remaining;
    }

    // Cursors choose candidates only; they are never eligibility or disposal proof.
    // Wrap after the final ID, so a permanently pinned prefix cannot starve the tail.
    private static async Task<IndexGenerationEntity[]> NextManifestsAsync(IQueryable<IndexGenerationEntity> query, Guid? after, CancellationToken token)
    {
        var page = await (after is { } id ? query.Where(value => value.Id.CompareTo(id) > 0) : query)
            .OrderBy(value => value.Id).Take(2).ToArrayAsync(token);
        return page.Length == 0 && after is not null ? await query.OrderBy(value => value.Id).Take(2).ToArrayAsync(token) : page;
    }
    private static async Task<IndexSegmentEntity[]> NextSegmentsAsync(IQueryable<IndexSegmentEntity> query, string? after, int count, CancellationToken token)
    {
        var page = await (after is not null ? query.Where(value => string.Compare(value.Id, after) > 0) : query)
            .OrderBy(value => value.Id).Take(count).ToArrayAsync(token);
        return page.Length == 0 && after is not null ? await query.OrderBy(value => value.Id).Take(count).ToArrayAsync(token) : page;
    }

    private static bool IsRetirementBlocker(InvalidOperationException exception) => exception.Message.StartsWith("segment-retirement-", StringComparison.Ordinal);
}
