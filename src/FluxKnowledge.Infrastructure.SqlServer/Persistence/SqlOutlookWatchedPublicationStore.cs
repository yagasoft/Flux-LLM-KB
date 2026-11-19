using System.Text.Json;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record OutlookWatchedExportCandidate(
    Guid ProfileId,
    Guid ExportId,
    Guid SourceRootId,
    string WatchRoot,
    string StageRoot,
    string WatchIdentity,
    string StageIdentity,
    OutlookWatchedBundleReceipt? PreparedBundle);

public sealed partial class SqlOutlookWatchedPublicationStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task BindAsync(
        Guid profileId,
        Guid sourceRootId,
        string watchRoot,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        watchRoot = RequirePrivateRoot(watchRoot);
        stageRoot = RequirePrivateRoot(stageRoot);
        _ = new OutlookWatchedBundleWriter(watchRoot, stageRoot);
        using var watchLease = PhysicalFileIdentity.OpenDirectoryLease(watchRoot);
        using var stageLease = PhysicalFileIdentity.OpenDirectoryLease(stageRoot);
        if (!SamePath(watchRoot, watchLease.Identity.CanonicalPath) ||
            !SamePath(stageRoot, stageLease.Identity.CanonicalPath))
            throw new InvalidDataException("The Outlook export locations must be canonical private directories.");

        await InTransactionAsync(async context =>
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var profile = await ReadProfileForUpdateAsync(context, profileId, cancellationToken).ConfigureAwait(false);
            var watchedRoot = await ReadRootForUpdateAsync(context, sourceRootId, cancellationToken).ConfigureAwait(false);
            EnsureDedicatedSource(profile, watchedRoot, watchRoot);
            if (profile.WatchedSourceRootId is not null)
            {
                if (!MatchesBinding(profile, new(profileId, Guid.Empty, sourceRootId, watchRoot, stageRoot,
                    watchLease.Identity.IdentityFingerprint, stageLease.Identity.IdentityFingerprint, null)))
                    throw new InvalidOperationException("The existing Outlook watched source binding is immutable.");
                return;
            }
            if (Directory.EnumerateFileSystemEntries(watchRoot).Any() ||
                Directory.EnumerateFileSystemEntries(stageRoot).Any() ||
                await context.SourceRevisions.AnyAsync(row => row.SourceRootId == sourceRootId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The Outlook export requires a dedicated empty source and staging directory.");

            if (SqlOutlookSourceReservations.Overlaps(profile.SpoolRoot, watchRoot) || SqlOutlookSourceReservations.Overlaps(profile.SpoolRoot, stageRoot))
                throw new InvalidOperationException("The Outlook spool must be separate from the watched export directories.");
            await SqlOutlookSourceReservations.RequireAvailableAsync(context, watchRoot, watchedRoot, enable: false,
                cancellationToken).ConfigureAwait(false);
            await SqlOutlookSourceReservations.RequireAvailableAsync(context, stageRoot, existingRoot: null, enable: false,
                cancellationToken).ConfigureAwait(false);
            await EnsureNoOtherWatchedScopeAsync(context, sourceRootId, watchRoot, stageRoot, cancellationToken).ConfigureAwait(false);
            profile.WatchedSourceRootId = sourceRootId;
            profile.WatchedRootPath = watchRoot;
            profile.WatchedStagingPath = stageRoot;
            profile.WatchedRootIdentity = watchLease.Identity.IdentityFingerprint;
            profile.WatchedStagingIdentity = stageLease.Identity.IdentityFingerprint;
            profile.ConfigurationRevision++;
            profile.UpdatedAtUtc = _clock.GetUtcNow();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutlookWatchedExportCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 100);
        var now = _clock.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await (
            from export in context.OutlookCaptureExports.AsNoTracking()
            join profile in context.OutlookCaptureProfiles.AsNoTracking() on export.ProfileId equals profile.Id
            join root in context.SourceRootConfigurations.AsNoTracking() on profile.WatchedSourceRootId equals root.Id
            where export.State == (int)OutlookExportState.Ingested && export.WatchedPublishedAtUtc == null &&
                (export.WatchedRetryAfterUtc == null || export.WatchedRetryAfterUtc <= now) &&
                profile.WatchedRootPath != null && profile.WatchedStagingPath != null &&
                profile.WatchedRootIdentity != null && profile.WatchedStagingIdentity != null &&
                profile.WatchedPublicationSuspendedAtUtc == null &&
                profile.State != (int)OutlookCaptureState.Stale && root.State == (int)SourceRootState.Enabled &&
                root.CanonicalPath == profile.WatchedRootPath && root.Recursive && !root.FollowLinks &&
                root.CrawlMode == (int)SourceDiscoveryMode.Filesystem
            orderby export.WatchedRetryAfterUtc, export.Id
            select new
            {
                ProfileId = profile.Id, ExportId = export.Id, SourceRootId = root.Id,
                profile.WatchedRootPath, profile.WatchedStagingPath, profile.WatchedRootIdentity, profile.WatchedStagingIdentity,
                export.WatchedPreparedBundleJson
            }).Take(maximumCount).ToListAsync(cancellationToken).ConfigureAwait(false);
        return pending.Select(row => new OutlookWatchedExportCandidate(row.ProfileId, row.ExportId, row.SourceRootId,
            row.WatchedRootPath!, row.WatchedStagingPath!, row.WatchedRootIdentity!, row.WatchedStagingIdentity!,
            DeserializeReceipt(row.WatchedPreparedBundleJson))).ToArray();
    }

    public async Task<OutlookWatchedBundleReceipt> PrepareWithdrawalAsync(
        OutlookWatchedExportCandidate candidate, OutlookWatchedBundleReceipt preparation, CancellationToken cancellationToken)
    {
        if (preparation.ExportId != candidate.ExportId || preparation.StageId == Guid.Empty)
            throw new InvalidDataException("The prepared Outlook bundle does not match its capture receipt.");
        OutlookWatchedBundleReceipt? selected = null;
        await InTransactionAsync(async context =>
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var profile = await ReadProfileForUpdateAsync(context, candidate.ProfileId, cancellationToken).ConfigureAwait(false);
            var root = await ReadRootForUpdateAsync(context, candidate.SourceRootId, cancellationToken).ConfigureAwait(false);
            EnsureDedicatedSource(profile, root, candidate.WatchRoot);
            if (!MatchesBinding(profile, candidate) || profile.WatchedPublicationSuspendedAtUtc is not null ||
                root.State != (int)SourceRootState.Enabled)
                throw new InvalidOperationException("The Outlook export destination is no longer active.");
            await EnsureNoOtherWatchedScopeAsync(context, root.Id, candidate.WatchRoot, candidate.StageRoot, cancellationToken).ConfigureAwait(false);

            var export = await ReadExportAsync(context, candidate, cancellationToken).ConfigureAwait(false);
            if (export.WatchedWithdrawalAtUtc is not null)
            {
                selected = DeserializeReceipt(export.WatchedPreparedBundleJson)
                    ?? throw new InvalidDataException("The Outlook withdrawal has no staged bundle receipt.");
                return;
            }
            if (export.SourceRevisionId is not { } ownerId)
                throw new InvalidDataException("The Outlook export does not have an owned retained revision.");

            var revisions = await context.SourceRevisions.FromSqlInterpolated($"""
                WITH [OutlookOwnership] AS
                (
                    SELECT [Id] FROM [SourceRevisions] WHERE [Id] = {ownerId}
                    UNION ALL
                    SELECT [child].[Id] FROM [SourceRevisions] AS [child]
                    JOIN [OutlookOwnership] AS [parent] ON [child].[ParentSourceRevisionId] = [parent].[Id]
                )
                SELECT [revision].* FROM [SourceRevisions] AS [revision]
                JOIN [OutlookOwnership] AS [owned] ON [revision].[Id] = [owned].[Id]
                """).ToListAsync(cancellationToken).ConfigureAwait(false);

            var parent = revisions.SingleOrDefault(revision => revision.Id == ownerId);
            if (parent is null || parent.ParentSourceRevisionId is not null ||
                !string.Equals(parent.StableSourceIdentity, $"outlook:{export.Id:N}", StringComparison.Ordinal) ||
                revisions.Any(revision => revision.SourceRootId != profile.SourceRootId))
                throw new InvalidDataException("The Outlook export revision ownership did not match the committed receipt.");

            var now = _clock.GetUtcNow();
            var withdrawn = revisions.Where(revision => revision.SuppressedAtUtc is null).ToArray();
            foreach (var revision in withdrawn)
                revision.SuppressedAtUtc = now;
            export.WatchedWithdrawalAtUtc = now;
            export.WatchedPreparedBundleJson = JsonSerializer.Serialize(preparation);
            selected = preparation;
            export.WatchedWithdrawalRevisionIdsJson = JsonSerializer.Serialize(withdrawn.Select(revision => revision.Id).Order().ToArray());
            if (withdrawn.Length != 0)
                await SqlPublishedPassageSelection.AdvanceVersionAsync(
                    context, now, cancellationToken, [new(3, profile.SourceRootId)]).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return selected ?? throw new InvalidOperationException("The Outlook withdrawal did not select a durable bundle.");
    }

    private static OutlookWatchedBundleReceipt? DeserializeReceipt(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<OutlookWatchedBundleReceipt>(json);

    public Task MarkPublishedAsync(OutlookWatchedExportCandidate candidate, CancellationToken cancellationToken) =>
        InTransactionAsync(async context =>
        {
            var profile = await ReadProfileForUpdateAsync(context, candidate.ProfileId, cancellationToken).ConfigureAwait(false);
            if (!MatchesBinding(profile, candidate) || profile.WatchedPublicationSuspendedAtUtc is not null)
                throw new InvalidOperationException("The Outlook export destination changed before acknowledgement.");
            var export = await ReadExportAsync(context, candidate, cancellationToken).ConfigureAwait(false);
            if (export.WatchedWithdrawalAtUtc is null)
                throw new InvalidOperationException("A durable Outlook withdrawal receipt is required before acknowledging publication.");
            export.WatchedPublishedAtUtc ??= _clock.GetUtcNow();
            export.WatchedRetryAfterUtc = null;
        }, cancellationToken);

    public async Task DeferAsync(OutlookWatchedExportCandidate candidate, CancellationToken cancellationToken)
    {
        var later = _clock.GetUtcNow().AddMinutes(5);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.OutlookCaptureExports
            .Where(export => export.Id == candidate.ExportId && export.ProfileId == candidate.ProfileId &&
                export.WatchedPublishedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(export => export.WatchedRetryAfterUtc, later), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task InTransactionAsync(Func<FluxKnowledgeDbContext, Task> action, CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await action(context).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task<OutlookCaptureProfileEntity> ReadProfileForUpdateAsync(
        FluxKnowledgeDbContext context, Guid profileId, CancellationToken cancellationToken) =>
        await context.OutlookCaptureProfiles
            .FromSqlInterpolated($"SELECT * FROM [OutlookCaptureProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {profileId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Outlook capture profile is unavailable.");

    private static async Task<SourceRootConfigurationEntity> ReadRootForUpdateAsync(
        FluxKnowledgeDbContext context, Guid sourceRootId, CancellationToken cancellationToken) =>
        await context.SourceRootConfigurations
            .FromSqlInterpolated($"SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceRootId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Outlook watched source is unavailable.");

    private static async Task<OutlookCaptureExportEntity> ReadExportAsync(
        FluxKnowledgeDbContext context, OutlookWatchedExportCandidate candidate, CancellationToken cancellationToken) =>
        await context.OutlookCaptureExports.SingleOrDefaultAsync(export =>
            export.Id == candidate.ExportId && export.ProfileId == candidate.ProfileId &&
            export.State == (int)OutlookExportState.Ingested, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The committed Outlook export is unavailable.");

    private static void EnsureDedicatedSource(
        OutlookCaptureProfileEntity profile, SourceRootConfigurationEntity root, string expectedPath)
    {
        if (profile.SourceRootId == root.Id || profile.State == (int)OutlookCaptureState.Stale ||
            root.State == (int)SourceRootState.Deleting || root.CrawlMode != (int)SourceDiscoveryMode.Filesystem ||
            !root.Recursive || root.FollowLinks || !SamePath(expectedPath, root.CanonicalPath) ||
            (JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson)?.Length ?? 1) != 0 ||
            (JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson)?.Length ?? 1) != 0)
            throw new InvalidOperationException("Use a dedicated recursive Outlook source with no path filters or link following.");
    }

    private static async Task EnsureNoOtherWatchedScopeAsync(
        FluxKnowledgeDbContext context,
        Guid sourceRootId,
        string watchRoot,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        var peers = await context.SourceRootConfigurations.AsNoTracking()
            .Where(root => root.Id != sourceRootId && root.State != (int)SourceRootState.Deleting)
            .Select(root => root.CanonicalPath).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (peers.Any(peer => SqlOutlookSourceReservations.Overlaps(peer, watchRoot) ||
            SqlOutlookSourceReservations.Overlaps(peer, stageRoot)))
            throw new InvalidOperationException("The Outlook export directories overlap another configured source.");
    }

    private static string RequirePrivateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an absolute local Outlook export path.", nameof(path));
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(canonical);
        return canonical;
    }

    private static bool MatchesBinding(OutlookCaptureProfileEntity profile, OutlookWatchedExportCandidate candidate) =>
        profile.Id == candidate.ProfileId && profile.WatchedSourceRootId == candidate.SourceRootId &&
        SamePath(profile.WatchedRootPath, candidate.WatchRoot) && SamePath(profile.WatchedStagingPath, candidate.StageRoot) &&
        string.Equals(profile.WatchedRootIdentity, candidate.WatchIdentity, StringComparison.Ordinal) &&
        string.Equals(profile.WatchedStagingIdentity, candidate.StageIdentity, StringComparison.Ordinal);

    private static bool SamePath(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
