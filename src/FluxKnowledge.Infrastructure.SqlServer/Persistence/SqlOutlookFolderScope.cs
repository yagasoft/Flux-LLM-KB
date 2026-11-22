using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Fenced membership for a complete traversal of the selected Outlook folder tree.</summary>
public sealed class SqlOutlookFolderScope(IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider timeProvider)
{
    public async Task<OutlookCaptureFolderEntity[]?> ReconcileAsync(
        OutlookCatchUpClaim claim,
        long? configurationRevision,
        IReadOnlyList<Guid> selectedRootIds,
        IReadOnlyList<OutlookFolderIdentity> discovered,
        CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await executionContext.Database.CreateExecutionStrategy().ExecuteAsync<OutlookCaptureFolderEntity[]?>(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var profileId = claim.ProfileId.Value;
            var profile = await CurrentProfiles(context, claim, configurationRevision, timeProvider.GetUtcNow(),
                    context.OutlookCaptureProfiles.FromSqlInterpolated(
                        $"SELECT * FROM [OutlookCaptureProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {profileId}"))
                .SingleOrDefaultAsync(p => p.IncludeSubfolders, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                return null;
            }

            var rows = await context.OutlookCaptureFolders.Where(row => row.ProfileId == profile.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var roots = rows.Where(row => !row.IsDiscoveredSubfolder &&
                row.State != (int)OutlookCaptureState.Disabled).ToArray();
            if (!roots.Select(row => row.Id).Order().SequenceEqual(selectedRootIds.Order()))
            {
                return null;
            }

            var byIdentity = rows.ToDictionary(row => (row.StoreId, row.FolderEntryId));
            var active = roots.ToList();
            var activeIds = roots.Select(row => row.Id).ToHashSet();
            foreach (var identity in discovered)
            {
                var key = (identity.StoreId, identity.FolderEntryId);
                if (!byIdentity.TryGetValue(key, out var row))
                {
                    row = new OutlookCaptureFolderEntity
                    {
                        Id = Guid.NewGuid(),
                        ProfileId = profile.Id,
                        StoreId = identity.StoreId,
                        FolderEntryId = identity.FolderEntryId
                    };
                    context.OutlookCaptureFolders.Add(row);
                    byIdentity.Add(key, row);
                }
                if (!activeIds.Add(row.Id)) continue;
                row.IsDiscoveredSubfolder = true;
                row.DisplayName = identity.DisplayName;
                row.Basis = profile.IncrementalBasis;
                row.State = (int)OutlookCaptureState.Ready;
                active.Add(row);
            }
            foreach (var removed in rows.Where(row => row.IsDiscoveredSubfolder && !activeIds.Contains(row.Id)))
            {
                removed.State = (int)OutlookCaptureState.Disabled;
                removed.CursorUtc = null;
                removed.CursorFingerprint = null;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return active.ToArray();
        }).ConfigureAwait(false);
    }

    public static IQueryable<OutlookCaptureProfileEntity> CurrentProfiles(
        FluxKnowledgeDbContext context,
        OutlookCatchUpClaim claim,
        long? configurationRevision,
        DateTimeOffset now,
        IQueryable<OutlookCaptureProfileEntity>? profiles = null)
    {
        var owner = $"{claim.LeaseOwner.WindowsUserSid}|{claim.LeaseOwner.SessionId}|{claim.LeaseOwner.HostInstanceId}";
        return (profiles ?? context.OutlookCaptureProfiles.AsNoTracking())
            .Where(p => p.Id == claim.ProfileId.Value && p.IsEnabled &&
                p.ConfigurationRevision == configurationRevision &&
                context.OutlookCatchUps.Any(c => c.Id == claim.CatchUpId && c.ProfileId == p.Id &&
                    c.ConfigurationRevision == p.ConfigurationRevision && c.FencingToken == claim.FencingToken &&
                    c.State == 1 && c.LeaseOwner == owner && c.LeaseExpiresAtUtc > now));
    }

}
