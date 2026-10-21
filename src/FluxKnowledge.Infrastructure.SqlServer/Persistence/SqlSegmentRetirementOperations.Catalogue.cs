using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentRetirementOperations
{
    private Guid? _candidateAfter;

    private async Task MaintainCatalogueAsync(IndexProjectionMaintenanceBudget budget, CancellationToken token)
    {
        await using (var selection = await contexts.CreateDbContextAsync(token))
        {
            // Explicit rebuilds retain sealed progress for an ownership-checked resume.
            // They end only through activation or explicit abandonment, never owner age/exit alone.
            var query = selection.IndexPublicationCandidates.AsNoTracking().Where(value => value.State < 2 && value.BuildPurpose == 0);
            var page = await (_candidateAfter is { } after ? query.Where(value => value.Id.CompareTo(after) > 0) : query)
                .OrderBy(value => value.Id).Take(budget.MaximumCandidateObservations).ToArrayAsync(token);
            if (page.Length == 0 && _candidateAfter is not null)
                page = await query.OrderBy(value => value.Id).Take(budget.MaximumCandidateObservations).ToArrayAsync(token);
            _candidateAfter = page.LastOrDefault()?.Id;
            foreach (var candidate in page)
            {
                if (ObserveCandidate(candidate) != GpuInteractiveOwnerObservation.Exited) continue;
                await using var context = await NewFileOperationContextAsync(token);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                await SqlSegmentedIndexStore.AcquireSharedTransactionAsync(context, token);
                await SqlPublishedPassageSelection.AcquireFenceAsync(context, token);
                var current = await context.IndexPublicationCandidates.SingleOrDefaultAsync(value => value.Id == candidate.Id, token);
                // A completed activation wins. Re-probe the stored process incarnation,
                // never infer native disposal from timeout, lost lease or SQL inactivity.
                if (current is { State: 0 or 1, BuildPurpose: 0 } && ObserveCandidate(current) == GpuInteractiveOwnerObservation.Exited)
                {
                    current.State = 3;
                    current.CompletedAtUtc = clock.GetUtcNow();
                    await context.SaveChangesAsync(token);
                }
                await transaction.CommitAsync(token);
            }
        }

        await using var cleanup = await NewFileOperationContextAsync(token);
        await using var cleanupTransaction = await cleanup.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await SqlSegmentedIndexStore.AcquireSharedTransactionAsync(cleanup, token);
        await SqlPublishedPassageSelection.AcquireFenceAsync(cleanup, token);
        var state = await cleanup.IndexState.SingleAsync(token);
        if (state.RequiredIndexFormat != 2 || state.CorpusRebuildOperationId is not null) return;
        // Preview metadata is untrusted and has no source/vector FKs. The compact
        // terminal candidate retains provenance and digests; only its worklist goes.
        await cleanup.Database.ExecuteSqlInterpolatedAsync($"""
            ;WITH [batch] AS (
                SELECT TOP ({budget.MaximumMemberDeletes}) [member].*
                FROM [IndexCandidateMembers] AS [member]
                WHERE EXISTS (SELECT 1 FROM [IndexPublicationCandidates] AS [candidate]
                    WHERE [candidate].[Id] = [member].[CandidateId] AND [candidate].[State] >= 2)
                ORDER BY [member].[CandidateId], [member].[Phase], [member].[VectorId])
            DELETE FROM [batch];
            """, token);
        var watermark = await ChangeWatermarkAsync(cleanup, state, token);
        if (watermark is { } version)
        {
            var epoch = state.CorpusEpoch;
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"""
                ;WITH [batch] AS (
                    SELECT TOP ({budget.MaximumChangeDeletes}) * FROM [IndexProjectionChanges]
                    WHERE [CorpusEpoch] = {epoch} AND [CorpusVersion] <= {version}
                    ORDER BY [CorpusVersion], [ScopeKind], [ScopeId])
                DELETE FROM [batch];
                """, token);
            var firstRemaining = await cleanup.IndexProjectionChanges.Where(value => value.CorpusEpoch == epoch && value.CorpusVersion <= version)
                .Select(value => (long?)value.CorpusVersion).MinAsync(token);
            var floor = firstRemaining is { } first ? first - 1 : version;
            state.ProjectionChangeFloorVersion = Math.Max(state.ProjectionChangeFloorVersion, floor);
            await cleanup.SaveChangesAsync(token);
        }
        await cleanupTransaction.CommitAsync(token);
    }

    private GpuInteractiveOwnerObservation ObserveCandidate(IndexPublicationCandidateEntity candidate) =>
        probe.Observe(new(candidate.OwnerProcessId, candidate.OwnerStartedAtUtc, candidate.OwnerMachineFingerprint));

    private static async Task<long?> ChangeWatermarkAsync(FluxKnowledgeDbContext context, IndexStateEntity state, CancellationToken token)
    {
        if (state.CorpusRebuildOperationId is not null) return null;
        var active = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == state.ActiveIndexGenerationId, token);
        if (active is not { FormatVersion: 2, CorpusVersion: { } activeVersion, RetiredAtUtc: null } ||
            active.CorpusEpoch != state.CorpusEpoch || activeVersion > state.CorpusVersion) return null;
        // A pending candidate needs every change after its BASE, even when its
        // expected version is much newer. An absent/foreign base is conservative zero.
        var pinned = await (from candidate in context.IndexPublicationCandidates
                            where candidate.State < 2 && candidate.CorpusEpoch == state.CorpusEpoch
                            join generation in context.IndexGenerations on candidate.BaseGenerationId equals (Guid?)generation.Id into bases
                            from generation in bases.DefaultIfEmpty()
                            select generation != null && generation.FormatVersion == 2 && generation.CorpusEpoch == state.CorpusEpoch &&
                                generation.CorpusVersion <= state.CorpusVersion ? generation.CorpusVersion : (long?)0).MinAsync(token);
        var watermark = Math.Min(activeVersion, pinned ?? activeVersion);
        if (watermark < state.ProjectionChangeFloorVersion) throw new InvalidOperationException("segment-retirement-change-log-gap");
        return watermark;
    }

    private static async Task<bool> CatalogueWorkRemainingAsync(FluxKnowledgeDbContext context, IndexStateEntity state, CancellationToken token)
    {
        if (await context.IndexCandidateMembers.AnyAsync(member => context.IndexPublicationCandidates.Any(candidate =>
            candidate.Id == member.CandidateId && candidate.State >= 2), token)) return true;
        var watermark = await ChangeWatermarkAsync(context, state, token);
        return watermark is { } version && await context.IndexProjectionChanges.AnyAsync(value =>
            value.CorpusEpoch == state.CorpusEpoch && value.CorpusVersion <= version, token);
    }
}
