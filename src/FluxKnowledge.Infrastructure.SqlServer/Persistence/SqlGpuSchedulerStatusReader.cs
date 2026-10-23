using FluxKnowledge.Application.Gpu;
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
            uncertainAge);
    }

}
