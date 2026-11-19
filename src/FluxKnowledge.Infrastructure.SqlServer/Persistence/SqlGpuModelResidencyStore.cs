using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Fences resident native capacity separately from ordinary job settlement.</summary>
internal sealed class SqlGpuModelResidencyStore(IDbContextFactory<FluxKnowledgeDbContext> factory, TimeProvider clock)
{
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(2);

    internal ValueTask<GpuResidentModelFence?> BeginBorrowAsync(GpuExecutorBatchHandle handle, Guid residencyId,
        Guid executionInstanceId, GpuResidentModelKey model, GpuInteractiveOwnerIdentity owner,
        GpuResidentHelperIdentity? helper, CancellationToken ct)
    {
        handle.Validate();
        model.Validate();
        var initial = new GpuResidentModelFence(residencyId, handle.CapacitySlotKey, 1, owner, helper);
        initial.Validate();
        if (executionInstanceId == Guid.Empty) throw new ArgumentException("gpu-resident-execution-invalid");

        return MutateAsync<GpuResidentModelFence?>(async db =>
        {
            var slot = await ReadSlotAsync(db, handle.CapacitySlotKey, ct).ConfigureAwait(false);
            var batch = await db.GpuBatches.SingleOrDefaultAsync(row => row.Id == handle.BatchId, ct).ConfigureAwait(false);
            var dispatch = await db.GpuExecutorDispatches.SingleOrDefaultAsync(row => row.DispatchId == handle.DispatchId, ct)
                .ConfigureAwait(false);
            if (!MatchesReservation(handle, slot, batch, dispatch) || batch!.State != (int)GpuBatchState.Active ||
                dispatch!.State != (int)GpuExecutorDispatchState.Acknowledged ||
                batch.ModelRuntimeKey != model.RuntimeKey || batch.SettingsFingerprint != model.SettingsFingerprint)
                return null;

            var current = await db.GpuModelResidencies.SingleOrDefaultAsync(row => row.SlotKey == handle.CapacitySlotKey &&
                row.ReleasedAtUtc == null, ct).ConfigureAwait(false);
            if (current is not null)
                return current.State == (int)GpuModelResidencyState.Borrowed &&
                    current.BorrowedDispatchId == handle.DispatchId && current.BorrowedExecutionInstanceId == executionInstanceId &&
                    MatchesFence(current, initial) && ReadModel(current) == model ? initial : null;

            db.GpuModelResidencies.Add(new()
            {
                ResidencyId = residencyId, SlotKey = handle.CapacitySlotKey, Version = initial.Version,
                RuntimeKey = model.RuntimeKey, SettingsFingerprint = model.SettingsFingerprint, ModelKey = model.ModelKey,
                OwnerProcessId = owner.ProcessId, OwnerStartedAtUtc = owner.StartedAtUtc, MachineFingerprint = owner.MachineFingerprint,
                ChildJobId = helper?.JobId, ChildProcessId = helper?.ProcessId, ChildStartedAtUtc = helper?.StartedAtUtc,
                State = (int)GpuModelResidencyState.Borrowed, BorrowedDispatchId = handle.DispatchId,
                BorrowedExecutionInstanceId = executionInstanceId
            });
            return initial;
        }, ct);
    }

    /// <summary>Called inside the executor's terminal job transaction while it holds the scheduler mutation fence.</summary>
    internal static async Task<GpuResidentModelFence?> ParkAfterTerminalAsync(FluxKnowledgeDbContext db,
        GpuResidentModelFence borrowed, GpuExecutorBatchHandle handle, Guid executionInstanceId, GpuResidentModelKey loadedModel,
        DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("gpu-resident-settlement-transaction-required");
        borrowed.Validate();
        handle.Validate();
        loadedModel.Validate();
        if (borrowed.SlotKey != handle.CapacitySlotKey || executionInstanceId == Guid.Empty)
            throw new ArgumentException("gpu-resident-settlement-binding-invalid");

        var current = await ReadResidencyAsync(db, borrowed, ct).ConfigureAwait(false);
        if (current is null || current.State != (int)GpuModelResidencyState.Borrowed ||
            current.BorrowedDispatchId != handle.DispatchId || current.BorrowedExecutionInstanceId != executionInstanceId ||
            current.RuntimeKey != loadedModel.RuntimeKey || current.SettingsFingerprint != loadedModel.SettingsFingerprint)
            return null;

        var slot = await ReadSlotAsync(db, handle.CapacitySlotKey, ct).ConfigureAwait(false);
        var batch = await db.GpuBatches.SingleOrDefaultAsync(row => row.Id == handle.BatchId, ct).ConfigureAwait(false);
        var dispatch = await db.GpuExecutorDispatches.SingleOrDefaultAsync(row => row.DispatchId == handle.DispatchId, ct)
            .ConfigureAwait(false);
        if (!MatchesReservation(handle, slot, batch, dispatch) || batch!.State != (int)GpuBatchState.Completed ||
            dispatch!.State != (int)GpuExecutorDispatchState.Terminal)
            return null;

        // The caller's terminal mutations and detachment proof land together. The
        // physical slot switches directly from an active reservation to idle residency.
        current.Version = checked(current.Version + 1);
        current.State = (int)GpuModelResidencyState.Idle;
        current.ModelKey = loadedModel.ModelKey;
        current.BorrowedDispatchId = null;
        current.BorrowedExecutionInstanceId = null;
        current.IdleUntilUtc = now + IdleLifetime;
        db.GpuModelDetachmentReceipts.Add(new()
        {
            DispatchId = handle.DispatchId, BatchId = handle.BatchId, ExecutorKey = handle.ExecutorKey,
            AdmissionGeneration = handle.AdmissionGeneration, ExecutionInstanceId = executionInstanceId,
            ResidencyId = current.ResidencyId, Version = current.Version, DetachedAtUtc = now
        });
        slot!.State = (int)GpuCapacitySlotState.ResidentIdle;
        slot.ActiveBatchId = null;
        slot.UpdatedAtUtc = now;
        await SqlGpuSchedulerStore.RecordWakeAsync(db, GpuSchedulerWakeReason.Reconciliation, now, ct).ConfigureAwait(false);
        return borrowed with { Version = current.Version };
    }

    /// <summary>Reserves exactly one borrowed model in the same transaction as the new dispatch.</summary>
    internal static async Task<GpuResidentModelFence?> ReserveWarmForAdmissionAsync(FluxKnowledgeDbContext db,
        GpuResidentModelFence idle, GpuResidentModelKey nextModel, GpuBatchEntity batch, GpuExecutorDispatchEntity dispatch,
        Guid executionInstanceId, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("gpu-resident-admission-transaction-required");
        idle.Validate();
        nextModel.Validate();
        if (executionInstanceId == Guid.Empty) throw new ArgumentException("gpu-resident-execution-invalid");

        var current = await ReadResidencyAsync(db, idle, ct).ConfigureAwait(false);
        var slot = await ReadSlotAsync(db, idle.SlotKey, ct).ConfigureAwait(false);
        if (current is null || current.State != (int)GpuModelResidencyState.Idle ||
            current.IdleUntilUtc is not { } until || until <= now || ReadModel(current) != nextModel ||
            slot is not { State: (int)GpuCapacitySlotState.ResidentIdle, ActiveBatchId: null } ||
            batch.State != (int)GpuBatchState.Active || dispatch.State != (int)GpuExecutorDispatchState.PendingDelivery ||
            batch.Id != dispatch.BatchId || batch.CapacitySlotKey != idle.SlotKey || dispatch.CapacitySlotKey != idle.SlotKey ||
            batch.AdmissionGeneration != dispatch.AdmissionGeneration || batch.OwnerKey != dispatch.OwnerKey ||
            slot.OwnerKey != batch.OwnerKey || batch.ModelRuntimeKey != nextModel.RuntimeKey ||
            batch.SettingsFingerprint != nextModel.SettingsFingerprint)
            return null;

        current.Version = checked(current.Version + 1);
        current.State = (int)GpuModelResidencyState.Borrowed;
        current.BorrowedDispatchId = dispatch.DispatchId;
        current.BorrowedExecutionInstanceId = executionInstanceId;
        current.IdleUntilUtc = null;
        slot.State = (int)GpuCapacitySlotState.Reserved;
        slot.ActiveBatchId = batch.Id;
        slot.LastHeartbeatAtUtc = now;
        slot.UpdatedAtUtc = now;
        return idle with { Version = current.Version };
    }

    internal ValueTask<GpuResidentModelFence?> RequestEvictionAsync(GpuResidentModelFence idle,
        GpuResidentEvictionReason reason, CancellationToken ct)
    {
        idle.Validate();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return MutateAsync<GpuResidentModelFence?>(async db =>
        {
            var current = await ReadResidencyAsync(db, idle, ct).ConfigureAwait(false);
            var slot = await ReadSlotAsync(db, idle.SlotKey, ct).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (current is null || slot is null ||
                reason == GpuResidentEvictionReason.Expired && (current.IdleUntilUtc is not { } until || until > now))
                return null;

            return RequestEviction(current, slot, now) ? ReadFence(current) : null;
        }, ct);
    }

    internal static bool RequestEviction(GpuModelResidencyEntity current, GpuCapacitySlotEntity slot, DateTimeOffset now)
    {
        if (current.State != (int)GpuModelResidencyState.Idle || current.SlotKey != slot.SlotKey ||
            slot is not { State: (int)GpuCapacitySlotState.ResidentIdle, ActiveBatchId: null }) return false;
        current.Version = checked(current.Version + 1);
        current.State = (int)GpuModelResidencyState.Evicting;
        slot.State = (int)GpuCapacitySlotState.ResidentEvicting;
        slot.UpdatedAtUtc = now;
        return true;
    }

    internal static GpuResidentModelFence ReadFence(GpuModelResidencyEntity row) => new(row.ResidencyId, row.SlotKey, row.Version,
        new(row.OwnerProcessId, row.OwnerStartedAtUtc, row.MachineFingerprint),
        row.ChildJobId is { } job && row.ChildProcessId is { } pid && row.ChildStartedAtUtc is { } started ? new(job, pid, started) : null);

    internal ValueTask<bool> ConfirmEvictionAsync(GpuResidentModelFence evicting, GpuResidentReleaseKind proof, CancellationToken ct)
    {
        evicting.Validate();
        if (!Enum.IsDefined(proof)) throw new ArgumentOutOfRangeException(nameof(proof));
        return MutateAsync(async db =>
        {
            var current = await ReadResidencyAsync(db, evicting, ct).ConfigureAwait(false);
            if (current is null || (current.ChildJobId.HasValue != (proof == GpuResidentReleaseKind.ContainedJobEmpty)))
                return false;
            if (current.State == (int)GpuModelResidencyState.Released)
                return current.ReleasedAtUtc is not null && current.ReleaseKind == (int)proof;

            var slot = await ReadSlotAsync(db, evicting.SlotKey, ct).ConfigureAwait(false);
            if (current.State != (int)GpuModelResidencyState.Evicting ||
                slot is not { State: (int)GpuCapacitySlotState.ResidentEvicting, ActiveBatchId: null })
                return false;

            var now = clock.GetUtcNow();
            current.State = (int)GpuModelResidencyState.Released;
            current.ReleasedAtUtc = now;
            current.ReleaseKind = (int)proof;
            current.IdleUntilUtc = null;
            slot.State = (int)GpuCapacitySlotState.Available;
            slot.OwnerKey = null;
            slot.LastHeartbeatAtUtc = now;
            slot.UpdatedAtUtc = now;
            await SqlGpuSchedulerStore.RecordWakeAsync(db, GpuSchedulerWakeReason.CapacityReleased, now, ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    internal static Task<bool> HasLiveResidencyAsync(FluxKnowledgeDbContext db, string slotKey, CancellationToken ct) =>
        db.GpuModelResidencies.AnyAsync(row => row.SlotKey == slotKey && row.ReleasedAtUtc == null, ct);

    internal async ValueTask<GpuResidentModelFence?> ReadBorrowAsync(GpuExecutorBatchHandle handle, CancellationToken ct)
    {
        handle.Validate();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await (from candidate in db.GpuModelResidencies.AsNoTracking()
                         join dispatch in db.GpuExecutorDispatches on candidate.BorrowedDispatchId equals dispatch.DispatchId
                         where candidate.SlotKey == handle.CapacitySlotKey && candidate.State == (int)GpuModelResidencyState.Borrowed &&
                             dispatch.DispatchId == handle.DispatchId && dispatch.BatchId == handle.BatchId &&
                             dispatch.CapacitySlotKey == handle.CapacitySlotKey && dispatch.ExecutorKey == handle.ExecutorKey &&
                             dispatch.AdmissionGeneration == handle.AdmissionGeneration
                         select candidate).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : ReadFence(row);
    }

    internal async ValueTask<IReadOnlyList<GpuModelResidencyEntity>> ReadLiveAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.GpuModelResidencies.AsNoTracking().Where(row => row.ReleasedAtUtc == null)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask<bool> IsCurrentHelperCleanupTargetAsync(GpuResidentModelFence fence, CancellationToken ct)
    {
        if (fence.Helper is null) return false;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ReadResidencyAsync(db, fence, ct).ConfigureAwait(false);
        return row?.State is (int)GpuModelResidencyState.Borrowed or (int)GpuModelResidencyState.Evicting;
    }

    internal ValueTask<bool> ReleaseBorrowedAsync(GpuResidentModelFence fence, GpuExecutorBatchHandle handle,
        Guid executionInstanceId, GpuResidentReleaseKind proof, CancellationToken ct) =>
        MutateAsync(db => ReleaseBorrowedAsync(db, fence, handle, executionInstanceId, proof, clock.GetUtcNow(), ct), ct);

    // Physical release is independent of job outcome. Keep its active slot
    // fenced until the adapter completes the ordinary terminal transaction.
    internal static async Task<bool> ReleaseBorrowedAsync(FluxKnowledgeDbContext db, GpuResidentModelFence fence,
        GpuExecutorBatchHandle handle, Guid executionInstanceId, GpuResidentReleaseKind proof, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("gpu-resident-release-transaction-required");
        if (!Enum.IsDefined(proof) || fence.Helper is not null != (proof == GpuResidentReleaseKind.ContainedJobEmpty)) return false;
        var row = await ReadResidencyAsync(db, fence, ct).ConfigureAwait(false);
        if (row is null || row.State != (int)GpuModelResidencyState.Borrowed || row.SlotKey != handle.CapacitySlotKey ||
            row.BorrowedDispatchId != handle.DispatchId || row.BorrowedExecutionInstanceId != executionInstanceId)
            return false;
        var dispatch = await db.GpuExecutorDispatches.SingleOrDefaultAsync(value => value.DispatchId == handle.DispatchId, ct)
            .ConfigureAwait(false);
        var slot = await ReadSlotAsync(db, handle.CapacitySlotKey, ct).ConfigureAwait(false);
        if (dispatch is null || dispatch.BatchId != handle.BatchId || dispatch.CapacitySlotKey != handle.CapacitySlotKey ||
            dispatch.AdmissionGeneration != handle.AdmissionGeneration || dispatch.ExecutorKey != handle.ExecutorKey ||
            slot is null || slot.ActiveBatchId != handle.BatchId || slot.OwnerKey != dispatch.OwnerKey ||
            slot.State is not ((int)GpuCapacitySlotState.Reserved or (int)GpuCapacitySlotState.Uncertain))
            return false;
        row.State = (int)GpuModelResidencyState.Released;
        row.ReleasedAtUtc = now;
        row.ReleaseKind = (int)proof;
        row.BorrowedDispatchId = null;
        row.BorrowedExecutionInstanceId = null;
        return true;
    }

    private async ValueTask<T> MutateAsync<T>(Func<FluxKnowledgeDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var initial = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await initial.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            // Maintenance can hold Admission while a resident is being evicted.
            await SqlGpuSchedulerStore.AcquireMutationLockAsync(db, tx.GetDbTransaction(), ct).ConfigureAwait(false);
            var result = await action(db).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private static Task<GpuCapacitySlotEntity?> ReadSlotAsync(FluxKnowledgeDbContext db, string slotKey, CancellationToken ct) =>
        db.GpuCapacitySlots.SingleOrDefaultAsync(row => row.SlotKey == slotKey, ct);

    private static async Task<GpuModelResidencyEntity?> ReadResidencyAsync(
        FluxKnowledgeDbContext db, GpuResidentModelFence fence, CancellationToken ct)
    {
        var row = await db.GpuModelResidencies.SingleOrDefaultAsync(value => value.ResidencyId == fence.ResidencyId, ct)
            .ConfigureAwait(false);
        return row is not null && MatchesFence(row, fence) ? row : null;
    }

    internal static GpuResidentModelKey ReadModel(GpuModelResidencyEntity row) => new(row.RuntimeKey, row.SettingsFingerprint, row.ModelKey);

    private static bool MatchesFence(GpuModelResidencyEntity row, GpuResidentModelFence fence) =>
        row.ResidencyId == fence.ResidencyId && string.Equals(row.SlotKey, fence.SlotKey, StringComparison.Ordinal) &&
        row.Version == fence.Version && row.OwnerProcessId == fence.Owner.ProcessId && row.OwnerStartedAtUtc == fence.Owner.StartedAtUtc &&
        string.Equals(row.MachineFingerprint, fence.Owner.MachineFingerprint, StringComparison.Ordinal) &&
        row.ChildJobId == fence.Helper?.JobId && row.ChildProcessId == fence.Helper?.ProcessId &&
        row.ChildStartedAtUtc == fence.Helper?.StartedAtUtc;

    private static bool MatchesReservation(GpuExecutorBatchHandle handle, GpuCapacitySlotEntity? slot,
        GpuBatchEntity? batch, GpuExecutorDispatchEntity? dispatch) =>
        slot is { State: (int)GpuCapacitySlotState.Reserved } && slot.ActiveBatchId == handle.BatchId &&
        batch is not null && batch.CapacitySlotKey == handle.CapacitySlotKey && batch.AdmissionGeneration == handle.AdmissionGeneration &&
        dispatch is not null && dispatch.BatchId == handle.BatchId && dispatch.CapacitySlotKey == handle.CapacitySlotKey &&
        dispatch.ExecutorKey == handle.ExecutorKey && dispatch.AdmissionGeneration == handle.AdmissionGeneration &&
        slot.OwnerKey == batch.OwnerKey && slot.OwnerKey == dispatch.OwnerKey;
}
