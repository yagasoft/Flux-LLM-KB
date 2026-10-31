using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceWorkRecovery(IDbContextFactory<FluxKnowledgeDbContext> factory, SourceWorkAdmission admission,
    ISourceRemovalReconciler removals, ISourceChangeJournalReader reader, ISourceObservationReader observations,
    SqlSourceIncrementalCoordinator incremental, SourceJournalOptions options, TimeProvider clock,
    ISourceWorkRetirement? retirement = null, Guid? targetRootId = null) : ISourceWorkRecovery
{
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
            var fullSweep = true;
            try
            {
                var restored = SqlSourceRemovalReconciler.Restore(root);
                if (restored.PhysicalIdentityFingerprint is null) throw new IOException("SourceStartupIdentityUnavailable");
                using (var authority = observations.OpenRoot(restored)) authority.ValidateCurrent();
                var state = await lookup.SourceRootScanStates.AsNoTracking().SingleOrDefaultAsync(x => x.SourceRootId == root.Id, cancellationToken).ConfigureAwait(false);
                if (options.Enabled && state is not null && options.ApprovedVolumes.Contains(state.VolumeIdentity, StringComparer.OrdinalIgnoreCase))
                {
                    var barrier = await reader.QueryAsync(state.VolumeIdentity, cancellationToken).ConfigureAwait(false);
                    await incremental.CatchUpStartupAsync(barrier, cancellationToken).ConfigureAwait(false);
                    var current = await lookup.SourceRootScanStates.AsNoTracking().SingleAsync(x => x.SourceRootId == root.Id, cancellationToken).ConfigureAwait(false);
                    fullSweep = current.RecoveryRequired || !current.NamespaceComplete || current.Mode is not ("Journal" or "JournalPartial") ||
                        current.CoveredUsn < barrier.NextUsn || current.PolicyFingerprint != SqlRepositoryWorkRecovery.Policy(root);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { fullSweep = true; }
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
        if (retirement is not null) await retirement.RetireAsync(cancellationToken).ConfigureAwait(false);
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
