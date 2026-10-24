using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Gpu;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

// The caller owns the context/transaction. Separate GPU-table reads preserve writer lock order.
internal static class SqlGpuSchedulerStatusReader
{
    internal static async ValueTask<GpuSchedulerStatusSnapshot> ReadAsync(
        FluxKnowledgeDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tasks = context.GpuMiniTasks.AsNoTracking();
        var readyState = (int)GpuMiniTaskExecutionState.Ready;
        var activeState = (int)GpuMiniTaskExecutionState.Active;
        var uncertainState = (int)GpuMiniTaskExecutionState.OutcomeUncertain;
        var completedState = (int)GpuMiniTaskExecutionState.Completed;
        var activeBatchStates = new[] { (int)GpuBatchState.Active, (int)GpuBatchState.AtSafeBoundary };
        var activeBatch = await context.GpuBatches.AsNoTracking()
            .Where(batch => activeBatchStates.Contains(batch.State))
            .OrderBy(batch => batch.CreatedAtUtc)
            .Select(batch => new { batch.PriorityLane })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var taskCounts = await tasks.GroupBy(task => new { task.PriorityLane, task.ExecutionState,
                Deferred = task.DeferredUntilUtc != null })
            .Select(group => new { group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var laneCounts = taskCounts
            .Where(group => group.Key.ExecutionState != completedState && group.Key.ExecutionState != (int)GpuMiniTaskExecutionState.Cancelled)
            .GroupBy(group => (GpuPriorityLane)group.Key.PriorityLane)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        var slotCounts = await context.GpuCapacitySlots.AsNoTracking().GroupBy(slot => slot.State)
            .Select(group => new { State = group.Key, Count = group.Count(), OldestHeartbeat = group.Min(slot => slot.LastHeartbeatAtUtc) })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var oldestUncertainHeartbeat = slotCounts.FirstOrDefault(group => group.State == (int)GpuCapacitySlotState.Uncertain)?.OldestHeartbeat;
        TimeSpan? uncertainAge = oldestUncertainHeartbeat is not { } heartbeat
            ? null
            : now > heartbeat
                ? now - heartbeat
                : TimeSpan.Zero;

        return new GpuSchedulerStatusSnapshot(
            taskCounts.Where(group => group.Key.ExecutionState == readyState).Sum(group => group.Count),
            taskCounts.Where(group => group.Key.ExecutionState == activeState).Sum(group => group.Count),
            taskCounts.Where(group => group.Key.ExecutionState == readyState && group.Key.Deferred).Sum(group => group.Count),
            taskCounts.Where(group => group.Key.ExecutionState == uncertainState).Sum(group => group.Count),
            laneCounts,
            activeBatch is not null,
            activeBatch is null ? null : (GpuPriorityLane)activeBatch.PriorityLane,
            slotCounts.Where(group => group.State == (int)GpuCapacitySlotState.Available).Sum(group => group.Count),
            slotCounts.Where(group => group.State == (int)GpuCapacitySlotState.Reserved).Sum(group => group.Count),
            slotCounts.Where(group => group.State == (int)GpuCapacitySlotState.Uncertain).Sum(group => group.Count),
            await context.GpuSchedulerStates.AsNoTracking()
                .Where(state => state.Id == 1)
                .Select(state => state.NextDeferredAtUtc)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false),
            uncertainAge) { OcrOwner = await ReadOcrOwnerAsync(context, now, cancellationToken).ConfigureAwait(false) };
    }

    private static async Task<GpuOcrOwnerStatus?> ReadOcrOwnerAsync(FluxKnowledgeDbContext context,
        DateTimeOffset now, CancellationToken ct)
    {
        var row = await (from slot in context.GpuCapacitySlots.AsNoTracking()
            join batch in context.GpuBatches.AsNoTracking() on slot.ActiveBatchId equals batch.Id
            join dispatch in context.GpuExecutorDispatches.AsNoTracking() on batch.Id equals dispatch.BatchId
            join task in context.GpuMiniTasks.AsNoTracking() on batch.Id equals task.BatchId
            join request in context.DocumentOcrRequests.AsNoTracking() on task.Id equals request.MiniTaskId
            where dispatch.ExecutorKey == PaddleOcrVlmRuntimeContract.ExecutorKey &&
                slot.State != (int)GpuCapacitySlotState.Available
            orderby batch.CreatedAtUtc
            select new { batch.Id, slot.State, slot.LastHeartbeatAtUtc, batch.CreatedAtUtc,
                request.ExecutionInstanceId, request.ExecutionPhase, request.ExecutionDeadlineAtUtc,
                request.RecoveryReason, request.NativeCleanupConfirmedAtUtc }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return null;
        var blocked = row.ExecutionInstanceId is null || row.ExecutionPhase == (int)DocumentOcrExecutionPhase.Blocked;
        var recovering = !blocked && (row.State == (int)GpuCapacitySlotState.Uncertain ||
            row.RecoveryReason is not null || row.NativeCleanupConfirmedAtUtc is not null || row.ExecutionDeadlineAtUtc <= now);
        var reason = row.ExecutionInstanceId is null ? "document-ocr-legacy-owner-unproven" : row.RecoveryReason switch
        {
            "document-ocr-job-missing-maintenance-required" or "document-ocr-job-unverifiable-maintenance-required" or
            "document-ocr-native-cleanup-unproven" or "document-ocr-owner-unverifiable" or "document-ocr-foreign-owner" or
            "document-ocr-result-persistence-failed" or "document-ocr-stored-result-invalid" => row.RecoveryReason,
            _ => blocked ? "document-ocr-cleanup-unproven" : recovering ? "document-ocr-recovery-pending" : "document-ocr-running"
        };
        var age = now - (row.LastHeartbeatAtUtc ?? row.CreatedAtUtc);
        return new(row.Id, row.ExecutionInstanceId, "PaddleOCR-VL", blocked ? "Blocked" : recovering ? "Recovering" : "Running",
            (int)Math.Clamp(Math.Ceiling(age.TotalMinutes), 0, int.MaxValue), row.ExecutionDeadlineAtUtc, reason,
            blocked ? "Operator verification of native cleanup is required before GPU work can resume." :
            recovering ? "Automatic cleanup and result settlement are pending. If cleanup cannot be proved, operator verification is required." :
            "OCR is running within its fixed deadline. Capacity is released after native cleanup is confirmed.");
    }

}
