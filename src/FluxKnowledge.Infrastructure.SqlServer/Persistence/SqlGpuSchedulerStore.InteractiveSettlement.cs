using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlGpuSchedulerStore : IGpuInteractiveSettlementStore
{
    public async ValueTask<GpuInteractiveRequestSnapshot> ReadInteractiveRequestAsync(Guid requestId, Guid executorInstanceId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty || executorInstanceId == Guid.Empty) throw new ArgumentException("interactive-owner-invalid");
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await executionContext.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            // Same ordering as handoff/cancellation: an observation cannot race an unresolved admission transaction.
            await AcquireAdmissionLockAsync(db, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await AcquireMutationLockAsync(db, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            var task = await db.GpuMiniTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == requestId, cancellationToken).ConfigureAwait(false);
            GpuInteractiveRequestSnapshot Snapshot(GpuInteractiveRequestState state, GpuExecutorBatchHandle? handle = null) =>
                new(requestId, executorInstanceId, task?.ModelRuntimeKey, task?.SettingsFingerprint, handle, state);
            if (task is null) return Snapshot(GpuInteractiveRequestState.NotFound);
            var owner = _interactiveOwner.Value;
            if (task.ParentJobId is not null || task.InteractiveExecutorInstanceId != executorInstanceId ||
                task.RequiredExecutorKey != $"retrieval-gpu:{executorInstanceId:N}" ||
                task.InteractiveOwnerProcessId != owner.ProcessId || task.InteractiveOwnerStartedAtUtc != owner.StartedAtUtc ||
                task.InteractiveOwnerMachineFingerprint != owner.MachineFingerprint)
                return Snapshot(GpuInteractiveRequestState.Uncertain);
            if (task.BatchId is null)
                return Snapshot(task.ExecutionState == (int)GpuMiniTaskExecutionState.Cancelled && task.InteractiveCancellationRequested
                    ? GpuInteractiveRequestState.CancelledBeforeExecution
                    : task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready ? GpuInteractiveRequestState.Queued : GpuInteractiveRequestState.Uncertain);
            var batch = await db.GpuBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == task.BatchId, cancellationToken).ConfigureAwait(false);
            var dispatch = await db.GpuExecutorDispatches.AsNoTracking().SingleOrDefaultAsync(d => d.BatchId == task.BatchId, cancellationToken).ConfigureAwait(false);
            if (batch is null || dispatch is null || batch.ItemCount != 1 || batch.AdmissionGeneration != task.AdmissionGeneration ||
                batch.ModelRuntimeKey != task.ModelRuntimeKey || batch.SettingsFingerprint != task.SettingsFingerprint ||
                dispatch.ExecutorKey != task.RequiredExecutorKey || dispatch.AdmissionGeneration != batch.AdmissionGeneration ||
                dispatch.CapacitySlotKey != batch.CapacitySlotKey || dispatch.OwnerKey != batch.OwnerKey ||
                await db.GpuMiniTasks.AnyAsync(t => t.BatchId == batch.Id && t.Id != requestId, cancellationToken).ConfigureAwait(false))
                return Snapshot(GpuInteractiveRequestState.Uncertain);
            var handle = new GpuExecutorBatchHandle(batch.Id, batch.CapacitySlotKey, dispatch.ExecutorKey, batch.AdmissionGeneration, dispatch.DispatchId);
            var slot = await db.GpuCapacitySlots.AsNoTracking().SingleOrDefaultAsync(s => s.SlotKey == batch.CapacitySlotKey, cancellationToken).ConfigureAwait(false);
            if (slot is null) return Snapshot(GpuInteractiveRequestState.Uncertain, handle);
            // The old owner must have released. A later legitimate batch may already own this physical slot.
            var oldOwnerReleased = slot.ActiveBatchId is null && slot.OwnerKey is null && slot.State == (int)GpuCapacitySlotState.Available;
            if (slot.ActiveBatchId is { } nextBatch && nextBatch != batch.Id &&
                (slot.State == (int)GpuCapacitySlotState.Reserved || slot.State == (int)GpuCapacitySlotState.Uncertain))
                oldOwnerReleased = await db.GpuBatches.AnyAsync(b => b.Id == nextBatch && b.CapacitySlotKey == slot.SlotKey &&
                    b.OwnerKey == slot.OwnerKey && b.AdmissionGeneration > 0 &&
                    (b.State == (int)GpuBatchState.Active || b.State == (int)GpuBatchState.CapacityUncertain), cancellationToken).ConfigureAwait(false);
            var confirmed = task.ExecutionState == (int)GpuMiniTaskExecutionState.Completed && batch.State == (int)GpuBatchState.Completed &&
                dispatch.State == (int)GpuExecutorDispatchState.Terminal && await db.GpuExecutorResultReceipts.AnyAsync(r =>
                    r.DispatchId == handle.DispatchId && r.BatchId == handle.BatchId && r.MiniTaskId == requestId && r.ExecutorKey == handle.ExecutorKey &&
                    r.AdmissionGeneration == handle.AdmissionGeneration && r.Disposition == (int)GpuMiniTaskBoundaryDisposition.Completed &&
                    r.EvidenceClass == (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed, cancellationToken).ConfigureAwait(false);
            var releasedWithUncertainOutcome = task.ExecutionState == (int)GpuMiniTaskExecutionState.OutcomeUncertain &&
                batch.State == (int)GpuBatchState.Released && dispatch.State == (int)GpuExecutorDispatchState.DeliveryUncertain &&
                await db.GpuExecutorResultReceipts.AnyAsync(r => r.DispatchId == handle.DispatchId && r.BatchId == handle.BatchId &&
                    r.MiniTaskId == requestId && r.ExecutorKey == handle.ExecutorKey && r.AdmissionGeneration == handle.AdmissionGeneration &&
                    r.Disposition == (int)GpuMiniTaskBoundaryDisposition.OutcomeUncertain && r.EvidenceClass == (int)GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed,
                    cancellationToken).ConfigureAwait(false);
            var reconciled = task.ExecutionState == (int)GpuMiniTaskExecutionState.OutcomeUncertain && batch.State == (int)GpuBatchState.CapacityUncertain &&
                dispatch.State == (int)GpuExecutorDispatchState.DeliveryUncertain &&
                await db.GpuExecutorEvidence.AnyAsync(e => e.DispatchId == handle.DispatchId && e.BatchId == handle.BatchId &&
                    e.CapacitySlotKey == handle.CapacitySlotKey && e.ExecutorKey == handle.ExecutorKey && e.AdmissionGeneration == handle.AdmissionGeneration &&
                    e.EvidenceClass == (int)GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed, cancellationToken).ConfigureAwait(false) &&
                await db.GpuExecutorEvidence.AnyAsync(e => e.DispatchId == handle.DispatchId && e.BatchId == handle.BatchId &&
                    e.CapacitySlotKey == handle.CapacitySlotKey && e.ExecutorKey == handle.ExecutorKey && e.AdmissionGeneration == handle.AdmissionGeneration &&
                    e.EvidenceClass == (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed, cancellationToken).ConfigureAwait(false);
            return Snapshot(oldOwnerReleased && (confirmed || releasedWithUncertainOutcome || reconciled)
                ? GpuInteractiveRequestState.Released : GpuInteractiveRequestState.Owned, handle);
        }, cancellationToken).ConfigureAwait(false);
    }
}
