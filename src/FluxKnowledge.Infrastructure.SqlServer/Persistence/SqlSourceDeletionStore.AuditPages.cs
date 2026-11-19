using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    private const int AuditPurgeBatchSize = 16_000;

    private static async Task<bool> DeleteOwnedAuditPageAsync(FluxKnowledgeDbContext context, Guid rootId,
        CancellationToken cancellationToken)
    {
        // Preserve the original five-way OR. Each capped branch uses the live
        // anchors, avoiding large GUID arrays and repeated correlated JSON scans.
        static IQueryable<long> Page(IQueryable<long> query) => query.OrderBy(id => id).Take(AuditPurgeBatchSize);
        var direct = Page(context.AuditEvents.Where(value => value.SourceRootId == rootId).Select(value => value.Id));
        var scans = Page(from audit in context.AuditEvents
            join scan in context.SourceScanRequests on audit.SourceScanRequestId equals (Guid?)scan.Id
            where scan.SourceRootId == rootId select audit.Id);
        var revisions = Page(from audit in context.AuditEvents
            join revision in context.SourceRevisions on audit.SourceRevisionId equals (Guid?)revision.Id
            where revision.SourceRootId == rootId select audit.Id);
        var activities = Page(from audit in context.AuditEvents
            join activity in context.SourceActivities on audit.SourceActivityId equals (Guid?)activity.Id
            join revision in context.SourceRevisions on activity.SourceRevisionId equals revision.Id
            where revision.SourceRootId == rootId select audit.Id);
        var records = Page(from audit in context.AuditEvents
            join record in context.PipelineRecords on audit.PipelineRecordId equals (Guid?)record.Id
            join revision in context.SourceRevisions on record.SourceRevisionId equals (Guid?)revision.Id
            where revision.SourceRootId == rootId select audit.Id);
        var ids = await direct.Union(scans).Union(revisions).Union(activities).Union(records)
            .OrderBy(id => id).Take(AuditPurgeBatchSize).TagWith("source-delete-audit-page-select")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return false;
        var deleted = await context.AuditEvents.Where(value => ids.Contains(value.Id))
            .TagWith("source-delete-audit-page-delete").ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted != ids.Length) throw new InvalidOperationException("source-delete-audit-page-changed");
        return true;
    }
}
