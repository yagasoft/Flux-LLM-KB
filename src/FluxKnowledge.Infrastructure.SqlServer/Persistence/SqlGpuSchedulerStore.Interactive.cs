using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlGpuSchedulerStore
{
    public async ValueTask<IReadOnlyList<GpuInteractiveRecoveryWork>> ReadInteractiveRecoveryAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ready = await context.GpuMiniTasks.AsNoTracking().Where(t => t.InteractiveExecutorInstanceId != null &&
            t.ExecutionState == (int)GpuMiniTaskExecutionState.Ready).OrderBy(t => t.CreatedSequence).Take(2)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var active = await (from task in context.GpuMiniTasks.AsNoTracking()
            join batch in context.GpuBatches.AsNoTracking() on task.BatchId equals batch.Id
            join dispatch in context.GpuExecutorDispatches.AsNoTracking() on batch.Id equals dispatch.BatchId
            join slot in context.GpuCapacitySlots.AsNoTracking() on batch.CapacitySlotKey equals slot.SlotKey
            where task.ParentJobId == null && task.InteractiveExecutorInstanceId != null && batch.ItemCount == 1 &&
                task.AdmissionGeneration == batch.AdmissionGeneration && task.RequiredExecutorKey == dispatch.ExecutorKey &&
                task.ModelRuntimeKey == batch.ModelRuntimeKey && task.SettingsFingerprint == batch.SettingsFingerprint &&
                dispatch.AdmissionGeneration == batch.AdmissionGeneration && dispatch.CapacitySlotKey == batch.CapacitySlotKey &&
                dispatch.OwnerKey == batch.OwnerKey && dispatch.State != (int)GpuExecutorDispatchState.Terminal &&
                (batch.State == (int)GpuBatchState.Active || batch.State == (int)GpuBatchState.CapacityUncertain) &&
                ((slot.ActiveBatchId == batch.Id && slot.OwnerKey == batch.OwnerKey &&
                  (slot.State == (int)GpuCapacitySlotState.Reserved || slot.State == (int)GpuCapacitySlotState.Uncertain)) ||
                 (batch.State == (int)GpuBatchState.CapacityUncertain && task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)) &&
                !context.GpuMiniTasks.Any(other => other.BatchId == batch.Id && other.Id != task.Id)
            orderby task.CreatedSequence
            select new { Task = task, Batch = batch, Dispatch = dispatch, Slot = slot }).Take(2)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var result = ready.Select(t => new GpuInteractiveRecoveryWork(t.Id, t.InteractiveExecutorInstanceId!.Value, OwnerOf(t), null, null)).ToList();
        foreach (var item in active)
        {
            var handle = new GpuExecutorBatchHandle(item.Batch.Id, item.Batch.CapacitySlotKey,
                item.Dispatch.ExecutorKey, item.Batch.AdmissionGeneration, item.Dispatch.DispatchId);
            handle.Validate();
            result.Add(new(item.Task.Id, item.Task.InteractiveExecutorInstanceId!.Value, OwnerOf(item.Task), handle,
                item.Slot.State == (int)GpuCapacitySlotState.Reserved && item.Slot.ActiveBatchId == item.Batch.Id &&
                item.Slot.LastHeartbeatAtUtc.HasValue
                    ? new(item.Batch.Id, item.Slot.SlotKey, item.Batch.OwnerKey, item.Batch.AdmissionGeneration,
                        item.Slot.LastHeartbeatAtUtc.Value, item.Slot.RowVersion.ToArray()) : null));
        }
        return result;
    }

    // Trusted recovery boundary: caller must first prove this exact process incarnation exited.
    public async ValueTask<bool> CancelLostInteractiveReadyAsync(Guid requestId, GpuInteractiveOwnerIdentity owner, CancellationToken cancellationToken)
    {
        owner.Validate();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.GpuMiniTasks.Where(t => t.Id == requestId && t.InteractiveExecutorInstanceId != null &&
            t.ExecutionState == (int)GpuMiniTaskExecutionState.Ready && t.InteractiveOwnerProcessId == owner.ProcessId &&
            t.InteractiveOwnerStartedAtUtc == owner.StartedAtUtc && t.InteractiveOwnerMachineFingerprint == owner.MachineFingerprint)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExecutionState, (int)GpuMiniTaskExecutionState.Cancelled)
                .SetProperty(t => t.InteractiveCancellationRequested, true), cancellationToken).ConfigureAwait(false) == 1;
    }

    private static GpuInteractiveOwnerIdentity OwnerOf(GpuMiniTaskEntity task)
    {
        var owner = new GpuInteractiveOwnerIdentity(task.InteractiveOwnerProcessId!.Value,
            task.InteractiveOwnerStartedAtUtc!.Value, task.InteractiveOwnerMachineFingerprint!);
        owner.Validate();
        return owner;
    }

    public async ValueTask<GpuInteractiveExecutionWork?> ReadInteractiveExecutionAsync(
        GpuExecutorBatchHandle handle, Guid executorInstanceId, GpuExecutorDispatchState requiredDispatchState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (executorInstanceId == Guid.Empty) throw new ArgumentException("interactive-owner-invalid", nameof(executorInstanceId));
        if (requiredDispatchState is not GpuExecutorDispatchState.PendingDelivery and not GpuExecutorDispatchState.Acknowledged)
            throw new ArgumentOutOfRangeException(nameof(requiredDispatchState));
        if (handle.ExecutorKey != $"retrieval-gpu:{executorInstanceId:N}") return null;
        var owner = _interactiveOwner.Value;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await (
            from task in context.GpuMiniTasks.AsNoTracking()
            join batch in context.GpuBatches.AsNoTracking() on task.BatchId equals batch.Id
            join dispatch in context.GpuExecutorDispatches.AsNoTracking() on batch.Id equals dispatch.BatchId
            join slot in context.GpuCapacitySlots.AsNoTracking() on dispatch.CapacitySlotKey equals slot.SlotKey
            where task.ParentJobId == null && task.SourceRevision == 0 && task.InteractiveExecutorInstanceId == executorInstanceId &&
                task.InteractiveOwnerProcessId == owner.ProcessId && task.InteractiveOwnerStartedAtUtc == owner.StartedAtUtc &&
                task.InteractiveOwnerMachineFingerprint == owner.MachineFingerprint &&
                task.RequiredExecutorKey == handle.ExecutorKey && task.BatchId == handle.BatchId &&
                task.AdmissionGeneration == handle.AdmissionGeneration && task.ExecutionState == (int)GpuMiniTaskExecutionState.Active &&
                task.ExecutionDeadlineUtc != null && task.PriorityLane == (int)GpuPriorityLane.InteractiveRetrieval &&
                batch.ItemCount == 1 && batch.State == (int)GpuBatchState.Active && batch.AdmissionGeneration == handle.AdmissionGeneration &&
                batch.CapacitySlotKey == handle.CapacitySlotKey && batch.ModelRuntimeKey == task.ModelRuntimeKey && batch.SettingsFingerprint == task.SettingsFingerprint &&
                dispatch.DispatchId == handle.DispatchId && dispatch.ExecutorKey == handle.ExecutorKey && dispatch.CapacitySlotKey == handle.CapacitySlotKey &&
                dispatch.AdmissionGeneration == handle.AdmissionGeneration && dispatch.State == (int)requiredDispatchState &&
                slot.State == (int)GpuCapacitySlotState.Reserved && slot.ActiveBatchId == batch.Id &&
                slot.OwnerKey == batch.OwnerKey && dispatch.OwnerKey == batch.OwnerKey &&
                !context.GpuMiniTasks.Any(other => other.BatchId == batch.Id && other.Id != task.Id)
            select new { task.Id, task.ModelRuntimeKey, task.SettingsFingerprint, task.ExecutionDeadlineUtc, task.InteractiveCancellationRequested })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) return null;
        return new(candidate.Id, executorInstanceId, candidate.ModelRuntimeKey, candidate.SettingsFingerprint,
            candidate.ExecutionDeadlineUtc!.Value, candidate.InteractiveCancellationRequested || candidate.ExecutionDeadlineUtc <= _timeProvider.GetUtcNow());
    }

    public async ValueTask<GpuMiniTaskHandoffResult> HandoffInteractiveAsync(
        GpuInteractiveHandoffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.ExecutorInstanceId == Guid.Empty || request.EstimatedBytes <= 0 ||
            request.QueueDeadlineUtc.Offset != TimeSpan.Zero || request.ExecutionDeadlineUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("interactive-request-invalid", nameof(request));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.ExecutorKey, nameof(request.ExecutorKey), 256);
        if (!string.Equals(request.ExecutorKey, $"retrieval-gpu:{request.ExecutorInstanceId:N}", StringComparison.Ordinal))
            throw new ArgumentException("interactive-executor-instance-mismatch", nameof(request));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.ModelRuntimeKey, nameof(request.ModelRuntimeKey), 256);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.SettingsFingerprint, nameof(request.SettingsFingerprint), 256);
        var owner = _interactiveOwner.Value;

        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await executionContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await AcquireAdmissionLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await AcquireMutationLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            var existing = await context.GpuMiniTasks.SingleOrDefaultAsync(t => t.Id == request.RequestId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.ParentJobId.HasValue || existing.InteractiveExecutorInstanceId != request.ExecutorInstanceId ||
                    existing.InteractiveOwnerProcessId != owner.ProcessId || existing.InteractiveOwnerStartedAtUtc != owner.StartedAtUtc ||
                    existing.InteractiveOwnerMachineFingerprint != owner.MachineFingerprint ||
                    !string.Equals(existing.RequiredExecutorKey, request.ExecutorKey, StringComparison.Ordinal) ||
                    !string.Equals(existing.ModelRuntimeKey, request.ModelRuntimeKey, StringComparison.Ordinal) ||
                    !string.Equals(existing.SettingsFingerprint, request.SettingsFingerprint, StringComparison.Ordinal) ||
                    existing.EstimatedBytes != request.EstimatedBytes || existing.QueueDeadlineUtc != request.QueueDeadlineUtc ||
                    existing.ExecutionDeadlineUtc != request.ExecutionDeadlineUtc)
                    throw new InvalidOperationException("interactive-request-identity-conflict");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new GpuMiniTaskHandoffResult(existing.Id, true, true);
            }
            var now = _timeProvider.GetUtcNow();
            if (await context.GpuMiniTasks.AnyAsync(t => t.InteractiveExecutorInstanceId == request.ExecutorInstanceId &&
                (t.InteractiveOwnerProcessId != owner.ProcessId || t.InteractiveOwnerStartedAtUtc != owner.StartedAtUtc ||
                 t.InteractiveOwnerMachineFingerprint != owner.MachineFingerprint), cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("interactive-executor-process-conflict");
            if (request.QueueDeadlineUtc <= now || request.QueueDeadlineUtc > now.AddSeconds(2) ||
                request.ExecutionDeadlineUtc <= request.QueueDeadlineUtc || request.ExecutionDeadlineUtc > now.Add(_interactiveExecutionTimeout))
                throw new ArgumentException("interactive-request-deadline-invalid", nameof(request));
            await CancelExpiredInteractiveAsync(context, now, cancellationToken).ConfigureAwait(false);
            if (request.DeclineWhenGpuBusy &&
                (!await context.GpuCapacitySlots.AnyAsync(cancellationToken).ConfigureAwait(false) ||
                 await context.GpuCapacitySlots.AnyAsync(slot =>
                     slot.State != (int)GpuCapacitySlotState.Available || slot.ActiveBatchId != null,
                     cancellationToken).ConfigureAwait(false) ||
                 await context.GpuMiniTasks.AnyAsync(task =>
                     task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready ||
                     task.ExecutionState == (int)GpuMiniTaskExecutionState.Active,
                     cancellationToken).ConfigureAwait(false)))
                throw new GpuInteractiveBusyWithoutHandoffException();
            if (await context.GpuMiniTasks.CountAsync(t => t.InteractiveExecutorInstanceId != null &&
                (t.ExecutionState == (int)GpuMiniTaskExecutionState.Ready || t.ExecutionState == (int)GpuMiniTaskExecutionState.Active ||
                 context.GpuCapacitySlots.Any(slot => slot.ActiveBatchId != null && slot.ActiveBatchId == t.BatchId)),
                cancellationToken).ConfigureAwait(false) >= 2)
                throw new InvalidOperationException("interactive-queue-full");

            context.GpuMiniTasks.Add(new GpuMiniTaskEntity
            {
                Id = request.RequestId, SourceRevision = 0, PriorityLane = (int)GpuPriorityLane.InteractiveRetrieval,
                InteractiveExecutorInstanceId = request.ExecutorInstanceId, RequiredExecutorKey = request.ExecutorKey,
                InteractiveOwnerProcessId = owner.ProcessId, InteractiveOwnerStartedAtUtc = owner.StartedAtUtc,
                InteractiveOwnerMachineFingerprint = owner.MachineFingerprint,
                ModelRuntimeKey = request.ModelRuntimeKey, SettingsFingerprint = request.SettingsFingerprint,
                EstimatedBytes = request.EstimatedBytes, QueueDeadlineUtc = request.QueueDeadlineUtc,
                ExecutionDeadlineUtc = request.ExecutionDeadlineUtc, IdempotencyKey = $"interactive:{request.RequestId:D}",
                CreatedAtUtc = now, ExecutionState = (int)GpuMiniTaskExecutionState.Ready
            });
            await RecordWakeAsync(context, GpuSchedulerWakeReason.WorkReady, now, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuMiniTaskHandoffResult(request.RequestId, false, true);
        }).ConfigureAwait(false);
    }

    public async ValueTask<bool> CancelInteractiveAsync(Guid requestId, Guid executorInstanceId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty || executorInstanceId == Guid.Empty) throw new ArgumentException("interactive-owner-invalid");
        var owner = _interactiveOwner.Value;
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await executionContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await AcquireAdmissionLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            await AcquireMutationLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            var task = await context.GpuMiniTasks.SingleOrDefaultAsync(t => t.Id == requestId &&
                t.InteractiveExecutorInstanceId == executorInstanceId && t.InteractiveOwnerProcessId == owner.ProcessId &&
                t.InteractiveOwnerStartedAtUtc == owner.StartedAtUtc && t.InteractiveOwnerMachineFingerprint == owner.MachineFingerprint,
                cancellationToken).ConfigureAwait(false);
            if (task is null) return false;
            task.InteractiveCancellationRequested = true;
            if (task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready) task.ExecutionState = (int)GpuMiniTaskExecutionState.Cancelled;
            // Active capacity belongs to native execution, not the caller's deadline/cancellation.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static Task<int> CancelExpiredInteractiveAsync(FluxKnowledgeDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.GpuMiniTasks.Where(t => t.InteractiveExecutorInstanceId != null &&
            t.ExecutionState == (int)GpuMiniTaskExecutionState.Ready &&
            (t.QueueDeadlineUtc <= now || t.InteractiveCancellationRequested))
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ExecutionState, (int)GpuMiniTaskExecutionState.Cancelled)
                .SetProperty(t => t.InteractiveCancellationRequested, true), cancellationToken);

    private sealed class InteractiveQueueExpiredException(Guid requestId, Guid executorInstanceId) : Exception
    {
        public Guid RequestId { get; } = requestId;
        public Guid ExecutorInstanceId { get; } = executorInstanceId;
    }

    private void RequireUnexpiredInteractive(IReadOnlyList<GpuMiniTaskEntity> selected)
    {
        var task = selected[0];
        if (task.InteractiveExecutorInstanceId.HasValue && task.QueueDeadlineUtc <= _timeProvider.GetUtcNow())
            throw new InteractiveQueueExpiredException(task.Id, task.InteractiveExecutorInstanceId.Value);
    }

    private async Task<GpuSchedulerAdmissionRoundResult> CommitExpiredInteractiveAsync(InteractiveQueueExpiredException expired,
        Guid operationId, Guid batchId, GpuSchedulerWakeReason wakeReason, GpuSchedulerOptions options, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await AcquireAdmissionLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        await AcquireMutationLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(r => r.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null) return AdmissionResultFromReceipt(receipt, wakeReason, options);
        await context.GpuMiniTasks.Where(t => t.Id == expired.RequestId && t.InteractiveExecutorInstanceId == expired.ExecutorInstanceId &&
            t.ExecutionState == (int)GpuMiniTaskExecutionState.Ready)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ExecutionState, (int)GpuMiniTaskExecutionState.Cancelled)
                .SetProperty(t => t.InteractiveCancellationRequested, true), cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        await RecomputeNextDeferredAsync(context, now, cancellationToken).ConfigureAwait(false);
        await RecordWakeAsync(context, GpuSchedulerWakeReason.WorkReady, now, cancellationToken).ConfigureAwait(false);
        var result = new GpuSchedulerAdmissionRoundResult(true, GpuAdmissionDisposition.Busy, null);
        RecordAdmissionReceipt(context, operationId, batchId, wakeReason, options, result);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
