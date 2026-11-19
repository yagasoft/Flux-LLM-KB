using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlOutlookWatchedPublicationStore
{
    public Task SuspendAndRestoreLegacyAsync(Guid profileId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(profileId, Guid.Empty);
        return InTransactionAsync(async context =>
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var profile = await ReadProfileForUpdateAsync(context, profileId, cancellationToken).ConfigureAwait(false);
            if (profile.WatchedSourceRootId is not { } watchedRootId || watchedRootId == profile.SourceRootId ||
                string.IsNullOrWhiteSpace(profile.WatchedRootPath))
                throw new InvalidOperationException("The Outlook profile has no dedicated watched source to suspend.");

            var watchedRoot = await ReadRootForUpdateAsync(context, watchedRootId, cancellationToken).ConfigureAwait(false);
            if (!SamePath(watchedRoot.CanonicalPath, profile.WatchedRootPath) || watchedRoot.State == (int)SourceRootState.Deleting)
                throw new InvalidOperationException("The Outlook watched source binding is unavailable for rollback.");

            var now = _clock.GetUtcNow();
            if (profile.WatchedPublicationSuspendedAtUtc is null)
            {
                profile.WatchedPublicationSuspendedAtUtc = now;
                profile.ConfigurationRevision = checked(profile.ConfigurationRevision + 1);
                profile.UpdatedAtUtc = now;
            }
            if (watchedRoot.State != (int)SourceRootState.Paused)
            {
                watchedRoot.State = (int)SourceRootState.Paused;
                watchedRoot.ConfigurationRevision = checked(watchedRoot.ConfigurationRevision + 1);
                watchedRoot.UpdatedAtUtc = now;
            }

            var withdrawn = await context.SourceRevisions
                .Where(revision => revision.SourceRootId == watchedRoot.Id && revision.SuppressedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(revision => revision.SuppressedAtUtc, now),
                    cancellationToken).ConfigureAwait(false);
            var restored = await RestoreRecordedSuppressionsAsync(context, profile.Id, profile.SourceRootId,
                cancellationToken).ConfigureAwait(false);

            if (withdrawn != 0 || restored != 0)
                await SqlPublishedPassageSelection.AdvanceVersionAsync(context, now, cancellationToken,
                    [new(3, watchedRoot.Id), new(3, profile.SourceRootId)]).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static Task<int> RestoreRecordedSuppressionsAsync(
        FluxKnowledgeDbContext context, Guid profileId, Guid legacyRootId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            IF EXISTS (
                SELECT 1 FROM [OutlookCaptureExports] AS [export]
                WHERE [export].[ProfileId] = {profileId} AND [export].[State] = {(int)OutlookExportState.Ingested}
                  AND [export].[WatchedWithdrawalAtUtc] IS NOT NULL
                  AND ([export].[WatchedWithdrawalRevisionIdsJson] IS NULL OR
                       ISJSON([export].[WatchedWithdrawalRevisionIdsJson]) <> 1 OR
                       LEFT([export].[WatchedWithdrawalRevisionIdsJson], 1) <> N'[')
            )
                THROW 51000, 'An Outlook withdrawal receipt is malformed.', 1;

            -- Walk only the ancestry of recorded, still-suppressed revisions. The immutable
            -- source root and capture parent both have to match before any visibility changes.
            WITH [Recorded] AS (
                SELECT [revision].[Id] AS [RevisionId],
                       [revision].[Id] AS [AncestorId],
                       [revision].[ParentSourceRevisionId],
                       [owner].[Id] AS [OwnerId],
                       [export].[WatchedWithdrawalAtUtc] AS [WithdrawalAtUtc]
                FROM [OutlookCaptureExports] AS [export]
                JOIN [OutlookCaptureFolders] AS [folder] ON [folder].[Id] = [export].[FolderId]
                JOIN [SourceRevisions] AS [owner] ON [owner].[Id] = [export].[SourceRevisionId]
                CROSS APPLY OPENJSON(CASE WHEN ISJSON([export].[WatchedWithdrawalRevisionIdsJson]) = 1
                    THEN [export].[WatchedWithdrawalRevisionIdsJson] ELSE N'[]' END) AS [receipt]
                JOIN [SourceRevisions] AS [revision] ON [revision].[Id] = TRY_CONVERT(uniqueidentifier, [receipt].[value])
                WHERE [export].[ProfileId] = {profileId} AND [folder].[ProfileId] = {profileId}
                  AND [export].[State] = {(int)OutlookExportState.Ingested}
                  AND [export].[WatchedWithdrawalAtUtc] IS NOT NULL
                  AND [receipt].[type] = 1 AND DATALENGTH([receipt].[value]) = 72
                  AND [revision].[SuppressedAtUtc] = [export].[WatchedWithdrawalAtUtc]
                  AND [revision].[SourceRootId] = {legacyRootId} AND [owner].[SourceRootId] = {legacyRootId}
                  AND [owner].[ParentSourceRevisionId] IS NULL AND [owner].[OriginKind] = 0 AND [owner].[Revision] = 1
                  AND [owner].[Classification] = N'OutlookMessage'
                  AND [owner].[StableSourceIdentity] = N'outlook:' + LOWER(REPLACE(CONVERT(char(36), [export].[Id]), '-', ''))
            ),
            [Ancestors] AS (
                SELECT [RevisionId], [AncestorId], [ParentSourceRevisionId], [OwnerId], [WithdrawalAtUtc]
                FROM [Recorded]
                UNION ALL
                SELECT [proof].[RevisionId], [parent].[Id], [parent].[ParentSourceRevisionId],
                       [proof].[OwnerId], [proof].[WithdrawalAtUtc]
                FROM [Ancestors] AS [proof]
                JOIN [SourceRevisions] AS [parent] ON [parent].[Id] = [proof].[ParentSourceRevisionId]
                WHERE [proof].[AncestorId] <> [proof].[OwnerId] AND [parent].[SourceRootId] = {legacyRootId}
            )
            UPDATE [revision] SET [SuppressedAtUtc] = NULL
            FROM [SourceRevisions] AS [revision]
            WHERE [revision].[SourceRootId] = {legacyRootId}
              AND EXISTS (
                  SELECT 1 FROM [Ancestors] AS [proof]
                  WHERE [proof].[RevisionId] = [revision].[Id] AND [proof].[AncestorId] = [proof].[OwnerId]
                    AND [proof].[WithdrawalAtUtc] = [revision].[SuppressedAtUtc]
              );
            """, cancellationToken);
}
