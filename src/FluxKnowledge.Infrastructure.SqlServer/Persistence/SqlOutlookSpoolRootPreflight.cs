using FluxKnowledge.Application.Operations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Fails startup before workers can consume a stale persisted Outlook spool root.</summary>
public sealed class SqlOutlookSpoolRootPreflight(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    PersistedOutlookSpoolRootPolicy policy)
{
    public async ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var persistedRoots = await context.OutlookCaptureProfiles.AsNoTracking()
            .OrderBy(profile => profile.Id)
            .Select(profile => profile.SpoolRoot)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var persistedRoot in persistedRoots)
        {
            try
            {
                _ = policy.RequireCanonicalBeforeIo(persistedRoot);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException(
                    "A persisted Outlook capture profile does not use the canonical safe spool root.",
                    exception);
            }
        }
    }
}
