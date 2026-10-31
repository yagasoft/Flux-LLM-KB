using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Revalidates one physical owner for an invocation confined to an exact document request.</summary>
public static class SourceCommandAdmission
{
    public static ValueTask<SourceWorkAdmission> PrepareAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory, SourceRevisionId revisionId, string expectedHash,
        string retainedRoot, CancellationToken cancellationToken) =>
        PrepareWithObservationsAsync(factory, revisionId, expectedHash, new WindowsSourceObservationReader(), cancellationToken);

    internal static async ValueTask<SourceWorkAdmission> PrepareWithObservationsAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory, SourceRevisionId revisionId, string expectedHash,
        ISourceObservationReader observations, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = await db.SourceRevisions.AsNoTracking().Where(x => x.Id == revisionId.Value &&
                x.ContentSha256 == expectedHash && x.OriginKind == 0 && x.SuppressedAtUtc == null)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source is null) throw new SourceWorkDeferredException();
        var rootRow = await db.SourceRootConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.SourceRootId &&
            x.State == (int)SourceRootState.Enabled, cancellationToken).ConfigureAwait(false);
        if (rootRow is null) throw new SourceWorkDeferredException();
        var admission = new SourceWorkAdmission();
        try
        {
            var root = SqlSourceRemovalReconciler.Restore(rootRow);
            if (root.PhysicalIdentityFingerprint is null || !Path.IsPathFullyQualified(source.CanonicalPath))
                throw new SourceWorkDeferredException();
            var fullPath = Path.GetFullPath(source.CanonicalPath);
            var prefix = Path.TrimEndingDirectorySeparator(root.CanonicalPath) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new SourceWorkDeferredException();
            var relative = SqlSourceChangeJournalStore.NormaliseRelative(Path.GetRelativePath(root.CanonicalPath, fullPath));
            if (relative == ".") throw new SourceWorkDeferredException();
            using var authority = observations.OpenRoot(root);
            authority.ValidateCurrent();
            using var absence = observations.OpenAbsence(root, relative);
            if (absence is not null)
            {
                absence.ValidateCurrent(); authority.ValidateCurrent();
                // Persist only advisory work for this exact path. The ordinary guarded
                // reconciler owns suppression; this command never drains sibling work.
                await new SqlSourceRootWatchStore(factory, TimeProvider.System).RecordSignalAsync(
                    new(root.Id, SourceWatchSignalKind.Deleted, DateTimeOffset.UtcNow, [new(relative)]), cancellationToken).ConfigureAwait(false);
                absence.ValidateCurrent(); authority.ValidateCurrent();
                throw new SourceWorkDeferredException();
            }
            using var presence = observations.Open(root, relative);
            presence.ValidateCurrent();
            // This gate must stay local to the exact branch/request/dispatch selectors
            // in the standalone command, never a generic host claim loop.
            admission.Admit(rootRow.Id, rootRow.ConfigurationRevision);
            if (!await SqlSourceWorkEligibility.Inputs(db, admission).AnyAsync(x => x.Id == revisionId.Value, cancellationToken).ConfigureAwait(false))
                throw new SourceWorkDeferredException();
            presence.ValidateCurrent(); authority.ValidateCurrent();
            return admission;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new SourceWorkDeferredException(); }
    }
}
