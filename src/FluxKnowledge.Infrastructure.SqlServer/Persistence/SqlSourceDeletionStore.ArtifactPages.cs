using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    private const int ArtifactPurgeBatchSize = 4_096;
    private const long ArtifactPurgePayloadBytes = 64L * 1024 * 1024;

    private static async Task<bool> DeleteOwnedArtifactPageAsync(FluxKnowledgeDbContext context,
        IQueryable<Guid> recordIds, CancellationToken cancellationToken)
    {
        var owned = context.Artifacts.Where(value => recordIds.Contains(value.PipelineRecordId));
        // SQL measures the payload without transferring retained text to the worker.
        // Widen each length before addition: combined payloads can exceed Int32.
        var candidates = await owned.OrderBy(value => value.Id).Take(ArtifactPurgeBatchSize)
            .Select(value => new
            {
                value.Id,
                PayloadBytes = (long)(EF.Functions.DataLength(value.SearchText) ?? 0) +
                    (long)(EF.Functions.DataLength(value.DocumentMetadataJson) ?? 0)
            }).TagWith("source-delete-artifact-page-select").ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Length == 0) return false;

        var ids = new List<Guid>(candidates.Length);
        var payloadBytes = 0L;
        foreach (var candidate in candidates)
        {
            // One oversized first item is allowed so a valid artifact cannot
            // permanently stall removal. This bounds selected payload, not log I/O.
            if (ids.Count > 0 && candidate.PayloadBytes > ArtifactPurgePayloadBytes - payloadBytes) break;
            ids.Add(candidate.Id);
            payloadBytes = checked(payloadBytes + candidate.PayloadBytes);
        }
        var frozenIds = ids.ToArray();
        var deleted = await context.Artifacts.Where(value => frozenIds.Contains(value.Id))
            .TagWith("source-delete-artifact-page-delete").ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted != frozenIds.Length) throw new InvalidOperationException("source-delete-artifact-page-changed");
        // The caller retains every source/record/job anchor while payload remains.
        // Final emptiness and deletion of those principals share this transaction.
        return await owned.AnyAsync(cancellationToken).ConfigureAwait(false);
    }
}
