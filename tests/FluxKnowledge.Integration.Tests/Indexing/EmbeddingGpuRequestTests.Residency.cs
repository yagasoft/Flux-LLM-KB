using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class EmbeddingGpuRequestTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preassigned_embedding_borrow_requires_original_host_exit_before_unstarted_settlement(bool recoverPending)
    {
        await using var environment = await PrepareRetainedEnvironmentAsync();
        var now = DateTimeOffset.UtcNow;
        var oldOwner = new GpuInteractiveOwnerIdentity(41001, now.AddMinutes(-5), new string('a', 64));
        var newOwner = oldOwner with { ProcessId = 41002, StartedAtUtc = now };
        var probe = new EmbeddingResidentOwnerProbe(oldOwner, newOwner);
        var admission = new FixtureSourceAdmission(environment.Factory);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, interactiveOwner: oldOwner, sourceAdmission: admission);
        var foreignScheduler = new SqlGpuSchedulerStore(environment.Factory, interactiveOwner: newOwner, sourceAdmission: admission);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, foreignScheduler, new ChannelGpuSchedulerWakeSignal(),
            Runtime, TimeProvider.System, sourceAdmission: admission, residentOwnerProbe: probe);
        var (handle, assigned, _) = await AdmitPreassignedEmbeddingAsync(environment, requests, scheduler, admission, oldOwner, now);
        var residentId = assigned.ResidencyId;
        var acknowledgementId = Guid.NewGuid();
        var foreignAck = await foreignScheduler.AcknowledgeAsync(new(acknowledgementId, handle), default);
        Assert.False(foreignAck.Accepted);
        Assert.False(foreignAck.Committed);
        await using (var verify = await environment.Factory.CreateDbContextAsync())
        {
            Assert.Equal((int)GpuExecutorDispatchState.PendingDelivery, (await verify.GpuExecutorDispatches.SingleAsync()).State);
            Assert.False(await verify.GpuSchedulerOperationReceipts.AnyAsync(receipt => receipt.OperationId == acknowledgementId));
        }

        if (!recoverPending)
        {
            var originalAck = await scheduler.AcknowledgeAsync(new(acknowledgementId, handle), default);
            Assert.True(originalAck.Accepted && originalAck.Committed);
        }
        var instance = Guid.NewGuid();
        var claimOperation = Guid.NewGuid();
        Assert.Null(await requests.ClaimExecutionAsync(handle, instance, claimOperation, newOwner, default));
        if (!recoverPending)
        {
            var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(now.AddMinutes(1), default));
            Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, default)).Committed);
        }
        var unstarted = Assert.Single(await requests.ReadRecoveryAsync(default));
        Assert.Equal(handle, unstarted.Handle);
        Assert.Null(unstarted.Owner);
        Assert.Null(unstarted.ExecutorInstanceId);
        Assert.Null(unstarted.ClaimOperationId);
        foreach (var state in new[] { GpuInteractiveOwnerObservation.Alive, GpuInteractiveOwnerObservation.Unknown })
        {
            probe.Observation = state;
            Assert.False(await requests.ConfirmUnstartedCleanupAsync(unstarted, instance, claimOperation, newOwner, default));
        }

        var expectedSlot = recoverPending ? GpuCapacitySlotState.Reserved : GpuCapacitySlotState.Uncertain;
        await using (var verify = await environment.Factory.CreateDbContextAsync())
        {
            var request = await verify.EmbeddingGpuRequests.SingleAsync();
            Assert.Null(request.OwnerProcessId);
            Assert.Null(request.OwnerStartedAtUtc);
            Assert.Null(request.OwnerMachineFingerprint);
            Assert.Null(request.DispatchId);
            Assert.False(request.NativeCleanupConfirmed);
            var resident = await verify.GpuModelResidencies.SingleAsync();
            Assert.Equal((int)GpuModelResidencyState.Borrowed, resident.State);
            Assert.Equal(handle.DispatchId, resident.BorrowedExecutionInstanceId);
            Assert.Null(resident.ReleasedAtUtc);
            var slot = await verify.GpuCapacitySlots.SingleAsync();
            Assert.Equal((int)expectedSlot, slot.State);
            Assert.Equal(handle.BatchId, slot.ActiveBatchId);
        }

        probe.Observation = GpuInteractiveOwnerObservation.Exited;
        var stale = unstarted with { Handle = handle with { AdmissionGeneration = handle.AdmissionGeneration + 1 } };
        Assert.False(await requests.ConfirmUnstartedCleanupAsync(stale, instance, claimOperation, newOwner, default));
        Assert.True(await requests.ConfirmUnstartedCleanupAsync(unstarted, instance, claimOperation, newOwner, default));
        Assert.NotEmpty(probe.Observed);
        Assert.All(probe.Observed, owner => Assert.Equal(oldOwner, owner));
        await using (var verify = await environment.Factory.CreateDbContextAsync())
        {
            var resident = await verify.GpuModelResidencies.SingleAsync();
            Assert.Equal(residentId, resident.ResidencyId);
            Assert.Equal(assigned.Version, resident.Version);
            Assert.Equal((int)GpuModelResidencyState.Released, resident.State);
            Assert.Equal((int)GpuResidentReleaseKind.HostingProcessExited, resident.ReleaseKind);
            var slot = await verify.GpuCapacitySlots.SingleAsync();
            Assert.Equal((int)expectedSlot, slot.State);
            Assert.Equal(handle.BatchId, slot.ActiveBatchId);
            var request = await verify.EmbeddingGpuRequests.SingleAsync();
            Assert.True(request.NativeCleanupConfirmed);
            Assert.Equal(handle.DispatchId, request.DispatchId);
            Assert.Null(request.ResultDigest);
            var state = recoverPending ? GpuExecutorDispatchState.Acknowledged : GpuExecutorDispatchState.DeliveryUncertain;
            Assert.Equal((int)state, (await verify.GpuExecutorDispatches.SingleAsync()).State);
        }

        await DeliverCleanedAsync(environment, requests, handle);
        await using var settled = await environment.Factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, (await settled.GpuCapacitySlots.SingleAsync()).State);
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await settled.GpuMiniTasks.SingleAsync()).ExecutionState);
        var completedBatch = await settled.GpuBatches.SingleAsync();
        Assert.Equal(recoverPending ? (int)GpuBatchState.Released : (int)GpuBatchState.CapacityUncertain, completedBatch.State);
        if (!recoverPending)
            Assert.True(await settled.GpuSchedulerOperationReceipts.AnyAsync(receipt => receipt.OperationKind == "capacity-reconciliation" &&
                receipt.BatchId == handle.BatchId && receipt.AdmissionGeneration == handle.AdmissionGeneration && receipt.Accepted && receipt.Committed));
        Assert.Equal((int)GpuExecutorDispatchState.DeliveryUncertain, (await settled.GpuExecutorDispatches.SingleAsync()).State);
        Assert.Equal(2, (await settled.EmbeddingGpuRequests.SingleAsync()).State);
        Assert.Empty(await settled.Vectors.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_host_recovers_warm_embedding_after_acknowledgement_response_loss_without_replay(bool watchdogExpired)
    {
        await using var environment = await PrepareRetainedEnvironmentAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = new GpuInteractiveOwnerIdentity(41003, now.AddMinutes(-5), new string('a', 64));
        var probe = new EmbeddingResidentOwnerProbe(owner, owner);
        var admission = new FixtureSourceAdmission(environment.Factory);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, interactiveOwner: owner, sourceAdmission: admission);
        var wake = new ChannelGpuSchedulerWakeSignal();
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, wake, Runtime, TimeProvider.System,
            sourceAdmission: admission, residentOwnerProbe: probe);
        var (handle, borrow, model) = await AdmitPreassignedEmbeddingAsync(environment, requests, scheduler, admission, owner, now);
        var native = new RecordingEmbeddingBorrow(scheduler,
            new SqlGpuModelResidencyStore(environment.Factory, TimeProvider.System), borrow, model);
        var executor = new EmbeddingGpuExecutor(requests, new LoseWarmAcknowledgement(scheduler), scheduler, native,
            probe, Runtime, new ChannelOutboxWakeSignal(), wake, TimeProvider.System, residency: native);

        await Assert.ThrowsAsync<ResponseLostException>(() => executor.DeliverAsync(handle, default).AsTask());
        Assert.Equal(0, native.InferenceCalls);
        await using (var verify = await environment.Factory.CreateDbContextAsync())
        {
            Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, (await verify.GpuExecutorDispatches.SingleAsync()).State);
            var request = await verify.EmbeddingGpuRequests.SingleAsync();
            Assert.Null(request.ExecutorInstanceId);
            Assert.Null(request.OwnerProcessId);
            Assert.Equal((int)GpuModelResidencyState.Borrowed, (await verify.GpuModelResidencies.SingleAsync()).State);
            Assert.Single(await verify.GpuSchedulerOperationReceipts.Where(receipt =>
                receipt.OperationKind == "executor-acknowledgement" && receipt.BatchId == handle.BatchId &&
                receipt.AdmissionGeneration == handle.AdmissionGeneration && receipt.Accepted && receipt.Committed).ToListAsync());
        }

        if (watchdogExpired)
        {
            var expired = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(now.AddMinutes(1), default));
            Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), expired, default)).Committed);
            await executor.RecoverAsync(default);
            Assert.Equal(1, native.ReleaseAttempts);
            Assert.Equal(0, native.ConfirmedReleases);
            Assert.Equal(0, native.InferenceCalls);
            await using var blocked = await environment.Factory.CreateDbContextAsync();
            var request = await blocked.EmbeddingGpuRequests.SingleAsync();
            Assert.Null(request.ExecutorInstanceId);
            Assert.False(request.NativeCleanupConfirmed);
            var residentBeforeProof = await blocked.GpuModelResidencies.SingleAsync();
            Assert.Equal((int)GpuModelResidencyState.Borrowed, residentBeforeProof.State);
            Assert.Null(residentBeforeProof.ReleasedAtUtc);
            var slot = await blocked.GpuCapacitySlots.SingleAsync();
            Assert.Equal((int)GpuCapacitySlotState.Uncertain, slot.State);
            Assert.Equal(handle.BatchId, slot.ActiveBatchId);

            native.AllowPhysicalRelease = true;
        }

        await executor.RecoverAsync(default);
        await executor.RecoverAsync(default);
        await executor.DeliverAsync(handle, default);
        Assert.Equal(watchdogExpired ? 0 : 1, native.InferenceCalls);
        Assert.Equal(watchdogExpired ? 1 : 0, native.ConfirmedReleases);

        await using var settled = await environment.Factory.CreateDbContextAsync();
        var completed = await settled.EmbeddingGpuRequests.SingleAsync();
        Assert.NotNull(completed.ExecutorInstanceId);
        Assert.Equal(owner.ProcessId, completed.OwnerProcessId);
        Assert.Equal(owner.StartedAtUtc, completed.OwnerStartedAtUtc);
        Assert.Equal(owner.MachineFingerprint, completed.OwnerMachineFingerprint);
        Assert.Equal(2, completed.State);
        var dispatch = await settled.GpuExecutorDispatches.SingleAsync();
        var capacity = await settled.GpuCapacitySlots.SingleAsync();
        var resident = await settled.GpuModelResidencies.SingleAsync();
        Assert.Equal(borrow.ResidencyId, resident.ResidencyId);
        if (watchdogExpired)
        {
            Assert.Null(completed.ResidentDetachmentDispatchId);
            Assert.Null(completed.ResultDigest);
            Assert.True(completed.NativeCleanupConfirmed);
            Assert.Equal((int)GpuExecutorDispatchState.DeliveryUncertain, dispatch.State);
            Assert.Equal((int)GpuCapacitySlotState.Available, capacity.State);
            Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await settled.GpuMiniTasks.SingleAsync()).ExecutionState);
            Assert.Equal((int)GpuModelResidencyState.Released, resident.State);
            Assert.Equal((int)GpuResidentReleaseKind.NativeSessionDisposed, resident.ReleaseKind);
            Assert.Equal(borrow.Version, resident.Version);
            Assert.NotNull(resident.ReleasedAtUtc);
            Assert.Empty(await settled.GpuModelDetachmentReceipts.ToArrayAsync());
        }
        else
        {
            Assert.Equal(handle.DispatchId, completed.ResidentDetachmentDispatchId);
            Assert.NotNull(completed.ResultDigest);
            Assert.False(completed.NativeCleanupConfirmed);
            Assert.Equal((int)GpuExecutorDispatchState.Terminal, dispatch.State);
            Assert.Equal((int)GpuCapacitySlotState.ResidentIdle, capacity.State);
            Assert.Equal(borrow.Version + 1, resident.Version);
            Assert.Null(resident.ReleasedAtUtc);
            Assert.Equal(handle.DispatchId, (await settled.GpuModelDetachmentReceipts.SingleAsync()).DispatchId);
        }
    }

    private static async Task<(GpuExecutorBatchHandle Handle, GpuResidentModelFence Borrow, GpuResidentModelKey Model)>
        AdmitPreassignedEmbeddingAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, SqlEmbeddingGpuRequestStore requests,
            SqlGpuSchedulerStore scheduler, FixtureSourceAdmission admission, GpuInteractiveOwnerIdentity owner, DateTimeOffset now)
    {
        var worker = await ClaimAsync(environment, now: now.AddSeconds(10));
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, sourceAdmission: admission)
            .ReadNextAsync(worker, Runtime.Profile, default);
        await requests.QueueAsync(worker, batch, default);

        const string slotKey = "gpu-0";
        const string ownerKey = "owner";
        var residentId = Guid.NewGuid();
        var model = new GpuResidentModelKey(Runtime.RuntimeKey, Runtime.SettingsFingerprint, "embedding-fp32");
        await using (var seed = await environment.Factory.CreateDbContextAsync())
        {
            seed.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = slotKey, State = (int)GpuCapacitySlotState.ResidentIdle, OwnerKey = ownerKey, UpdatedAtUtc = now
            });
            seed.GpuModelResidencies.Add(new GpuModelResidencyEntity
            {
                ResidencyId = residentId, SlotKey = slotKey, Version = 2, State = (int)GpuModelResidencyState.Idle,
                RuntimeKey = model.RuntimeKey, SettingsFingerprint = model.SettingsFingerprint, ModelKey = model.ModelKey,
                OwnerProcessId = owner.ProcessId, OwnerStartedAtUtc = owner.StartedAtUtc,
                MachineFingerprint = owner.MachineFingerprint, IdleUntilUtc = now.AddMinutes(2)
            });
            await seed.SaveChangesAsync();
        }

        var options = new GpuSchedulerOptions(1, 64 * 1024 * 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1), new GpuWorkloadPolicy([new(Runtime.RuntimeKey, Runtime.SettingsFingerprint, GpuWorkloadKind.Retrieval)]),
            residentEntryModels: [model]);
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, slotKey, ownerKey, null, EmbeddingGpuExecutor.Name)),
            default);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));
        var assigned = Assert.IsType<GpuResidentModelFence>(await new SqlGpuModelResidencyStore(environment.Factory, TimeProvider.System)
            .ReadBorrowAsync(handle, default));
        Assert.Equal((residentId, 3L, owner), (assigned.ResidencyId, assigned.Version, assigned.Owner));
        return (handle, assigned, model);
    }

    private sealed class LoseWarmAcknowledgement(SqlGpuSchedulerStore scheduler) : IGpuExecutorLifecycleSink
    {
        private bool _loseNext = true;

        public async ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement value, CancellationToken ct)
        {
            var result = await scheduler.AcknowledgeAsync(value, ct);
            if (_loseNext && result.Accepted && result.Committed)
            {
                _loseNext = false;
                throw new ResponseLostException();
            }
            return result;
        }

        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty value, CancellationToken ct) =>
            scheduler.MarkDeliveryUncertainAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt value, CancellationToken ct) =>
            scheduler.RecordReceiptAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence value, CancellationToken ct) =>
            scheduler.RecordTrustedEvidenceAsync(value, ct);
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operation, GpuBatchCallback value, CancellationToken ct) =>
            scheduler.ApplyBatchCallbackAsync(operation, value, ct);
    }

    private sealed class RecordingEmbeddingBorrow(SqlGpuSchedulerStore scheduler, SqlGpuModelResidencyStore store,
        GpuResidentModelFence borrow, GpuResidentModelKey model)
        : IEmbeddingGpuInference, IGpuProcessModelResidency
    {
        internal int InferenceCalls { get; private set; }
        internal int ReleaseAttempts { get; private set; }
        internal int ConfirmedReleases { get; private set; }
        internal bool AllowPhysicalRelease { get; set; }

        public ValueTask<GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>> EmbedBatchAsync(GpuOwnedWorkContext context,
            IReadOnlyList<string> texts, CancellationToken ct)
        {
            context.RequireActive(model.RuntimeKey, model.SettingsFingerprint);
            context.MarkResidentModelUsed();
            InferenceCalls++;
            return ValueTask.FromResult(new GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>(Outputs(texts.Count), false));
        }

        public bool Supports(GpuOwnedWorkContext context) => true;

        public async ValueTask<bool> CompleteAsync(GpuOwnedWorkContext context, Guid operation, Guid taskId, CancellationToken ct)
        {
            var result = await scheduler.CompleteResidentBatchAsync(operation, context.Handle, taskId, borrow, model, ct);
            return result.Accepted && result.Committed;
        }

        public ValueTask<T> GetModelAsync<T>(GpuOwnedWorkContext context, GpuResidentModelKey selected,
            Func<ValueTask<T>> open) where T : class, IGpuProcessResidentModel =>
            throw new InvalidOperationException("The test borrow already holds its synthetic model.");

        public async ValueTask<bool> ReleaseAsync(GpuOwnedWorkContext context, CancellationToken ct)
        {
            ReleaseAttempts++;
            if (!AllowPhysicalRelease) return false;
            Assert.Equal(borrow, await store.ReadBorrowAsync(context.Handle, ct));
            var confirmed = await store.ReleaseBorrowedAsync(borrow, context.Handle, context.Handle.DispatchId,
                GpuResidentReleaseKind.NativeSessionDisposed, ct);
            if (confirmed) ConfirmedReleases++;
            return confirmed;
        }

        public ValueTask<bool> RecoverExitedAsync(GpuExecutorBatchHandle handle, GpuInteractiveOwnerIdentity owner, CancellationToken ct) =>
            throw new InvalidOperationException("The original host remains alive.");
    }

    private sealed class EmbeddingResidentOwnerProbe(GpuInteractiveOwnerIdentity original, GpuInteractiveOwnerIdentity current)
        : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => current;
        internal List<GpuInteractiveOwnerIdentity> Observed { get; } = [];
        internal GpuInteractiveOwnerObservation Observation { get; set; } = GpuInteractiveOwnerObservation.Alive;

        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner)
        {
            Observed.Add(owner);
            return owner == original ? Observation : GpuInteractiveOwnerObservation.Unknown;
        }
    }
}
