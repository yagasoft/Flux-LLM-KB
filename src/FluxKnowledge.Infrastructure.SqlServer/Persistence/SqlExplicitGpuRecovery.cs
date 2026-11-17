using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlExplicitGpuRecovery
{
    internal static Task<bool> IsMarkedAsync(FluxKnowledgeDbContext context, Guid recordId, Guid? embedJobId, CancellationToken ct) =>
        context.AuditEvents.FromSqlInterpolated($"""
            SELECT * FROM AuditEvents WHERE PipelineRecordId={recordId} AND EventType={"embedding recovery queued"}
              AND JSON_VALUE(CASE WHEN ISJSON(DetailsJson)=1 THEN DetailsJson ELSE {"{}"} END,'$.requireSettledGpuPublicationProof')={"true"}
              AND ({embedJobId} IS NULL OR JSON_VALUE(CASE WHEN ISJSON(DetailsJson)=1 THEN DetailsJson ELSE {"{}"} END,'$.jobId')={(embedJobId == null ? null : embedJobId.Value.ToString("D"))})
            """).AnyAsync(ct);

    internal static async Task<bool> HasCurrentInputAsync(FluxKnowledgeDbContext context, Guid sourceId, CancellationToken ct)
    {
        var source = await context.SourceRevisions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == sourceId, ct);
        if (source is null || source.SuppressedAtUtc != null || !await SqlPhysicalSourceHistory.IsValidAsync(context, source, ct)) return false;
        var root = await context.SourceRootConfigurations.AsNoTracking().SingleAsync(value => value.Id == source.SourceRootId, ct);
        if (root.State != (int)SourceRootState.Enabled || root.FollowLinks) return false;
        if (root.CrawlMode == (int)SourceDiscoveryMode.GitTracked)
            return await SqlGitTextSelection.ReadAsync(context, sourceId, ct) is not null;
        if (!Path.IsPathFullyQualified(source.CanonicalPath)) return false;
        var relative = Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        relative = SqlSourceChangeJournalStore.NormaliseRelative(relative);
        var key = SqlSourceChangeJournalStore.PathKey(relative);
        var entry = await context.SourceInventoryEntries.AsNoTracking().SingleOrDefaultAsync(value => value.SourceRootId == source.SourceRootId && value.PathFingerprint == key, ct);
        if (entry is null || !string.Equals(SqlSourceChangeJournalStore.NormaliseRelative(entry.RelativePath), relative, StringComparison.OrdinalIgnoreCase)) return false;
        return await (from inventory in context.SourceInventoryEntries
            join scan in context.SourceRootScanStates on inventory.SourceRootId equals scan.SourceRootId
            join artifact in context.SourceArtifacts on inventory.SourceArtifactId equals artifact.Id
            where inventory.Id == entry.Id && inventory.SourceRootId == source.SourceRootId && inventory.SourceRevisionId == source.Id &&
                inventory.BaselineEpoch == scan.BaselineEpoch && !inventory.IsDirectory && !inventory.IsAncestor &&
                artifact.SourceRevisionId == source.Id && artifact.ContentSha256 == source.ContentSha256 && artifact.ByteLength == source.ByteLength &&
                artifact.ReferenceCount > 0 && artifact.ChecksumVerifiedAtUtc != default
            select inventory.Id).AnyAsync(ct);
    }
}
