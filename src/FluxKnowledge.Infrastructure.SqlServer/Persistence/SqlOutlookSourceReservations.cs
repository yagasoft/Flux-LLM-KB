using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlOutlookSourceReservations
{
    internal const string DeleteRefusal = "outlook-source-delete-unsupported";

    internal static Task<bool> ProtectsDeletionAsync(FluxKnowledgeDbContext context, Guid rootId, CancellationToken ct) =>
        context.OutlookCaptureProfiles.AnyAsync(profile =>
            profile.SourceRootId == rootId || profile.WatchedSourceRootId == rootId, ct);

    // Commit callers share the publication fence with Outlook binding and rollback.
    internal static async Task RequireAvailableAsync(FluxKnowledgeDbContext context, string path,
        SourceRootConfigurationEntity? existingRoot, bool enable, CancellationToken ct)
    {
        var reservations = await context.OutlookCaptureProfiles.AsNoTracking()
            .Where(profile => profile.WatchedSourceRootId != null)
            .Select(profile => new { profile.WatchedSourceRootId, profile.WatchedRootPath, profile.WatchedStagingPath,
                profile.WatchedPublicationSuspendedAtUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var reservation in reservations)
        {
            if (reservation.WatchedRootPath is not { } watch || reservation.WatchedStagingPath is not { } stage)
                throw new NativeOperationException("outlook-export-path-reserved");
            if (Overlaps(stage, path))
                throw new NativeOperationException("outlook-export-path-reserved");
            if (existingRoot is not null && existingRoot.Id == reservation.WatchedSourceRootId)
            {
                if (!string.Equals(watch, path, StringComparison.OrdinalIgnoreCase) ||
                    !existingRoot.Recursive || existingRoot.FollowLinks || existingRoot.CrawlMode != (int)SourceDiscoveryMode.Filesystem ||
                    JsonSerializer.Deserialize<string[]>(existingRoot.IncludePatternsJson) is not { Length: 0 } ||
                    JsonSerializer.Deserialize<string[]>(existingRoot.ExcludePatternsJson) is not { Length: 0 })
                    throw new NativeOperationException("outlook-export-path-reserved");
                if (enable && reservation.WatchedPublicationSuspendedAtUtc is not null)
                    throw new NativeOperationException("outlook-watched-source-suspended");
            }
            else if (Overlaps(watch, path))
                throw new NativeOperationException("outlook-export-path-reserved");
        }
    }

    internal static bool Overlaps(string first, string second) =>
        Contains(first, second) || Contains(second, first);

    private static bool Contains(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
