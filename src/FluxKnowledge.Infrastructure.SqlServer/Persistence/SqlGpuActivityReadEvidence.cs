using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public enum SqlGpuActivityReadPurpose { Overview, Jobs, GpuTasks, Files }

/// <summary>
/// Committed, narrow GPU metadata for a status read. The caller must reject the
/// whole sample if its enclosing ActivityReadFence changes. Separate statements
/// avoid taking cross-table reader locks opposite to scheduler write order.
/// </summary>
public sealed record SqlGpuActivityReadEvidence(string OwnedTasksJson, int UncertainCapacitySlots,
    string ReleasedOutcomesJson = "[]", string RemovedInputIdsJson = "[]",
    string RemovedJobBlockersJson = "[]", string RemovedGpuBlockersJson = "[]")
{
    private static readonly string RemovedSourceGpuTasksSql = $$"""
        SELECT task.Id, task.BatchId, task.AdmissionGeneration, task.ModelRuntimeKey, task.SettingsFingerprint
        {{SqlRemovedSourceWork.RemovedJobBindings}}
        INNER JOIN GpuMiniTasks task ON task.ParentJobId = job.Id AND task.SourceRevision = job.SourceRevision
        WHERE task.BatchId IS NOT NULL AND task.State IN (2,3,4)
          AND (job.PublicState <> 4 OR task.State = 3)
        """;

    public static async ValueTask<SqlGpuActivityReadEvidence> ReadAsync(FluxKnowledgeDbContext context,
        CancellationToken cancellationToken, SqlGpuActivityReadPurpose purpose = SqlGpuActivityReadPurpose.Overview)
    {
        var needsJobBlockers = purpose is SqlGpuActivityReadPurpose.Overview or SqlGpuActivityReadPurpose.Jobs;
        var needsTaskBlockers = purpose is SqlGpuActivityReadPurpose.Overview or SqlGpuActivityReadPurpose.GpuTasks;
        var tasks = await context.GpuMiniTasks.AsNoTracking()
            .Where(t => t.ExecutionState == 1 || needsTaskBlockers && t.ExecutionState == 3)
            // Prefer all admitted work, then a bounded recent receipt-proof window.
            // Older unproved outcomes remain visible; this never rewrites history.
            .OrderBy(t => t.ExecutionState).ThenByDescending(t => t.CreatedSequence).Take(4096)
            .Select(t => new { t.Id, t.BatchId, t.AdmissionGeneration, t.ExecutionState, t.ModelRuntimeKey, t.SettingsFingerprint })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var removedInputIds = await SqlRemovedSourceWork.ReadRemovedInputIdsAsync(context, cancellationToken).ConfigureAwait(false);
        var removedTasks = removedInputIds == "[]" || !(needsJobBlockers || needsTaskBlockers) ? [] :
            await context.Database.SqlQueryRaw<RemovedGpuTask>(RemovedSourceGpuTasksSql,
                    SqlRemovedSourceWork.JsonParameter("@removedInputIds", removedInputIds))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var slots = await context.GpuCapacitySlots.AsNoTracking().Where(s => s.State == 1 || s.State == 2 || s.ActiveBatchId != null)
            .Select(s => new { s.SlotKey, s.State, s.ActiveBatchId, s.OwnerKey })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var activeBatches = tasks.Where(t => t.ExecutionState == 1 && t.BatchId.HasValue).Select(t => t.BatchId!.Value).ToHashSet();
        var batchIds = tasks.Where(t => t.BatchId.HasValue).Select(t => t.BatchId!.Value)
            .Concat(slots.Where(s => s.ActiveBatchId.HasValue).Select(s => s.ActiveBatchId!.Value))
            .Concat(removedTasks.Select(task => task.BatchId))
            .Distinct().ToArray();
        var batches = await context.GpuBatches.AsNoTracking()
            .Where(b => batchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.CapacitySlotKey, b.OwnerKey, b.AdmissionGeneration, b.State, b.ModelRuntimeKey, b.SettingsFingerprint })
            .ToDictionaryAsync(b => b.Id, cancellationToken).ConfigureAwait(false);
        var dispatches = await context.GpuExecutorDispatches.AsNoTracking()
            .Where(d => batchIds.Contains(d.BatchId))
            .Select(d => new { d.DispatchId, d.BatchId, d.CapacitySlotKey, d.OwnerKey, d.AdmissionGeneration, d.State, d.ExecutorKey })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var byBatch = dispatches.ToLookup(d => d.BatchId);
        // Scheduler fence columns use BIN2 and reject trailing whitespace, so
        // ordinal comparisons preserve their exact SQL ownership semantics.
        var bySlot = slots.ToDictionary(s => s.SlotKey, StringComparer.Ordinal);
        var owned = new List<TaskProof>();
        foreach (var task in tasks.Where(t => t.ExecutionState == 1))
        {
            if (task.BatchId is not { } batchId || !batches.TryGetValue(batchId, out var batch) ||
                batch.State is not (0 or 1) || batch.AdmissionGeneration != task.AdmissionGeneration ||
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
            !batches.TryGetValue(batchId, out var batch) || batch.State is not (0 or 1) ||
            !string.Equals(batch.OwnerKey, slot.OwnerKey, StringComparison.Ordinal) || !activeBatches.Contains(batchId));
        var uncertainIds = tasks.Where(t => t.ExecutionState == 3).Select(t => t.Id).ToArray();
        var outcomes = !needsTaskBlockers || uncertainIds.Length == 0 ? [] : await context.GpuExecutorResultReceipts.AsNoTracking()
            .Where(r => uncertainIds.Contains(r.MiniTaskId) && r.Disposition == 1 && r.EvidenceClass == 2)
            .Select(r => new { r.MiniTaskId, r.DispatchId, r.BatchId, r.ExecutorKey, r.AdmissionGeneration })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var byTask = outcomes.ToLookup(r => r.MiniTaskId);
        var released = tasks.Where(t => t.ExecutionState == 3 && t.BatchId is not null &&
            batches.TryGetValue(t.BatchId.Value, out var batch) && batch.State == 3 && batch.AdmissionGeneration == t.AdmissionGeneration &&
            batch.ModelRuntimeKey == t.ModelRuntimeKey && batch.SettingsFingerprint == t.SettingsFingerprint &&
            byBatch[batch.Id].Any(d => d.State == 3 && d.AdmissionGeneration == batch.AdmissionGeneration &&
                d.CapacitySlotKey == batch.CapacitySlotKey && d.OwnerKey == batch.OwnerKey &&
                byTask[t.Id].Any(r => r.DispatchId == d.DispatchId && r.BatchId == batch.Id &&
                    r.ExecutorKey == d.ExecutorKey && r.AdmissionGeneration == t.AdmissionGeneration)))
            .Select(t => t.Id).ToArray();
        var occupiedBatches = slots.Where(slot => slot.ActiveBatchId.HasValue).Select(slot => slot.ActiveBatchId!.Value).ToHashSet();
        var removedBatchIds = removedTasks.Select(task => task.BatchId).ToHashSet();
        var reconcileIds = batches.Values.Where(batch => removedBatchIds.Contains(batch.Id) &&
            batch.State == (int)GpuBatchState.CapacityUncertain && !occupiedBatches.Contains(batch.Id)).Select(batch => batch.Id).ToArray();
        var reconciliations = reconcileIds.Length == 0 ? [] : await context.GpuSchedulerOperationReceipts.AsNoTracking()
            .Where(receipt => receipt.OperationKind == "capacity-reconciliation" && receipt.Accepted && receipt.Committed &&
                receipt.BatchId != null && reconcileIds.Contains(receipt.BatchId.Value))
            .Select(receipt => new { receipt.BatchId, receipt.AdmissionGeneration, receipt.CapacitySlotKey, receipt.OwnerKey })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = reconciliations.Select(receipt =>
            (receipt.BatchId!.Value, receipt.AdmissionGeneration, receipt.CapacitySlotKey, receipt.OwnerKey)).ToHashSet();
        var removedReleases = new List<RemovedGpuRelease>();
        foreach (var task in removedTasks)
        {
            if (occupiedBatches.Contains(task.BatchId) || !batches.TryGetValue(task.BatchId, out var batch) ||
                batch.AdmissionGeneration != task.AdmissionGeneration || batch.ModelRuntimeKey != task.ModelRuntimeKey ||
                batch.SettingsFingerprint != task.SettingsFingerprint)
                continue;
            var reconciledBatch = batch.State == (int)GpuBatchState.CapacityUncertain &&
                reconciled.Contains((batch.Id, batch.AdmissionGeneration, batch.CapacitySlotKey, batch.OwnerKey));
            foreach (var dispatch in byBatch[batch.Id])
            {
                if (dispatch.AdmissionGeneration != batch.AdmissionGeneration || dispatch.CapacitySlotKey != batch.CapacitySlotKey ||
                    dispatch.OwnerKey != batch.OwnerKey)
                    continue;
                if (dispatch.State is (int)GpuExecutorDispatchState.Terminal or (int)GpuExecutorDispatchState.DeliveryUncertain &&
                    (batch.State == (int)GpuBatchState.Completed && dispatch.State == (int)GpuExecutorDispatchState.Terminal ||
                     batch.State == (int)GpuBatchState.Released || reconciledBatch))
                    removedReleases.Add(new(task.Id, batch.Id, task.AdmissionGeneration, dispatch.DispatchId, dispatch.ExecutorKey));
            }
        }
        var (jobBlockers, gpuBlockers) = await SqlRemovedSourceWork.ReadBlockersAsync(context, removedInputIds,
            JsonSerializer.Serialize(removedReleases), needsJobBlockers, needsTaskBlockers, cancellationToken).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(owned), uncertain, JsonSerializer.Serialize(released), removedInputIds, jobBlockers, gpuBlockers);
    }

    private sealed record TaskProof(Guid Id, Guid BatchId, long AdmissionGeneration, bool HasActive, bool HasPending);
    private sealed record RemovedGpuRelease(Guid TaskId, Guid BatchId, long AdmissionGeneration, Guid DispatchId, string ExecutorKey);
    private sealed class RemovedGpuTask
    {
        public Guid Id { get; set; }
        public Guid BatchId { get; set; }
        public long AdmissionGeneration { get; set; }
        public string ModelRuntimeKey { get; set; } = "";
        public string SettingsFingerprint { get; set; } = "";
    }
}
