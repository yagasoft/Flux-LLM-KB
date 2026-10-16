using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Committed, narrow GPU metadata for a status read. The caller must reject the
/// whole sample if its enclosing ActivityReadFence changes. Separate statements
/// avoid taking cross-table reader locks opposite to scheduler write order.
/// </summary>
public sealed record SqlGpuActivityReadEvidence(string OwnedTasksJson, int UncertainCapacitySlots)
{
    public static async ValueTask<SqlGpuActivityReadEvidence> ReadAsync(FluxKnowledgeDbContext context,
        CancellationToken cancellationToken)
    {
        var tasks = await context.GpuMiniTasks.AsNoTracking().Where(t => t.ExecutionState == 1)
            .Select(t => new { t.Id, t.BatchId, t.AdmissionGeneration })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var slots = await context.GpuCapacitySlots.AsNoTracking().Where(s => s.State == 1 || s.State == 2)
            .Select(s => new { s.SlotKey, s.State, s.ActiveBatchId, s.OwnerKey })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var activeBatches = tasks.Where(t => t.BatchId.HasValue).Select(t => t.BatchId!.Value).ToHashSet();
        var batchIds = activeBatches.Concat(slots.Where(s => s.ActiveBatchId.HasValue).Select(s => s.ActiveBatchId!.Value))
            .Distinct().ToArray();
        var batches = await context.GpuBatches.AsNoTracking()
            .Where(b => batchIds.Contains(b.Id) && (b.State == 0 || b.State == 1))
            .Select(b => new { b.Id, b.CapacitySlotKey, b.OwnerKey, b.AdmissionGeneration })
            .ToDictionaryAsync(b => b.Id, cancellationToken).ConfigureAwait(false);
        var dispatches = await context.GpuExecutorDispatches.AsNoTracking()
            .Where(d => batchIds.Contains(d.BatchId) && (d.State == 0 || d.State == 1 || d.State == 2))
            .Select(d => new { d.BatchId, d.CapacitySlotKey, d.OwnerKey, d.AdmissionGeneration, d.State })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var byBatch = dispatches.ToLookup(d => d.BatchId);
        // Scheduler fence columns use BIN2 and reject trailing whitespace, so
        // ordinal comparisons preserve their exact SQL ownership semantics.
        var bySlot = slots.ToDictionary(s => s.SlotKey, StringComparer.Ordinal);
        var owned = new List<TaskProof>();
        foreach (var task in tasks)
        {
            if (task.BatchId is not { } batchId || !batches.TryGetValue(batchId, out var batch) ||
                batch.AdmissionGeneration != task.AdmissionGeneration ||
                !bySlot.TryGetValue(batch.CapacitySlotKey, out var slot) || slot.State != 1 ||
                slot.ActiveBatchId != batch.Id || !string.Equals(batch.OwnerKey, slot.OwnerKey, StringComparison.Ordinal))
                continue;
            var delivery = byBatch[batch.Id].Where(d => d.AdmissionGeneration == batch.AdmissionGeneration &&
                string.Equals(d.CapacitySlotKey, slot.SlotKey, StringComparison.Ordinal) &&
                string.Equals(d.OwnerKey, batch.OwnerKey, StringComparison.Ordinal)).ToArray();
            var active = delivery.Any(d => d.State is 1 or 2);
            var pending = delivery.Any(d => d.State == 0);
            if (active || pending) owned.Add(new(task.Id, batch.Id, task.AdmissionGeneration, active, pending));
        }
        var uncertain = slots.Count(slot => slot.State == 2 || slot.ActiveBatchId is not { } batchId ||
            !batches.TryGetValue(batchId, out var batch) ||
            !string.Equals(batch.OwnerKey, slot.OwnerKey, StringComparison.Ordinal) || !activeBatches.Contains(batchId));
        return new(JsonSerializer.Serialize(owned), uncertain);
    }

    private sealed record TaskProof(Guid Id, Guid BatchId, long AdmissionGeneration, bool HasActive, bool HasPending);
}
