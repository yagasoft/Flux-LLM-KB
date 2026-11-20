using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuModelResidencyStoreTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [Fact]
    public void Expiry_passed_during_a_read_probes_immediately()
    {
        var now = DateTimeOffset.UtcNow;
        var fallback = TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.Zero, GpuModelResidencyRuntime.NextIdleProbeDelay(now.AddMilliseconds(-1), now, fallback));
        Assert.Equal(TimeSpan.Zero, GpuModelResidencyRuntime.NextIdleProbeDelay(now, now, fallback));
        Assert.Equal(TimeSpan.FromSeconds(3), GpuModelResidencyRuntime.NextIdleProbeDelay(now.AddSeconds(3), now, fallback));
        Assert.Equal(fallback, GpuModelResidencyRuntime.NextIdleProbeDelay(now.AddMinutes(2), now, fallback));
        Assert.Equal(fallback, GpuModelResidencyRuntime.NextIdleProbeDelay(null, now, fallback));
    }
    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Warm_selection_respects_the_winning_lane_and_private_process(int foreground)
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var owner = new GpuInteractiveOwnerIdentity(42, clock.GetUtcNow(), new string('a', 64));
        var warm = new GpuResidentModelKey("resident-test:model", "settings", "embedding");
        var cold = new GpuResidentModelKey("resident-test:other", "settings", "reranking");
        var policy = new GpuWorkloadPolicy([
            new(warm.RuntimeKey, warm.SettingsFingerprint, GpuWorkloadKind.Retrieval),
            new(cold.RuntimeKey, cold.SettingsFingerprint, GpuWorkloadKind.Retrieval)]);
        var options = new GpuSchedulerOptions(1, 4096, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10), policy, [warm, cold]);
        await using var seed = await factory.CreateDbContextAsync();
        var slotKey = await seed.GpuCapacitySlots.Select(slot => slot.SlotKey).SingleAsync();
        var scheduler = new SqlGpuSchedulerStore(factory, timeProvider: clock, interactiveOwner: owner,
            sourceAdmission: new FixtureSourceAdmission(factory));
        var resident = new SqlGpuModelResidencyStore(factory, clock);
        ValueTask<GpuSchedulerAdmissionRoundResult> AdmitAsync() => scheduler.RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady, options, (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(
                GpuAdmissionDisposition.Admit, slotKey, "resident-test:owner", null,
                candidate.RequiredExecutorKey ?? "resident-test:executor")), default);
        Task<Guid> EnqueueAsync(GpuResidentModelKey model) => setup.AddReadyAsync(factory,
            GpuPriorityLane.DocumentIndexing, model.RuntimeKey, model.SettingsFingerprint, 1024);
        async Task<GpuExecutorBatchHandle> ActiveHandleAsync()
        {
            await using var db = await factory.CreateDbContextAsync();
            var id = await db.GpuCapacitySlots.Select(slot => slot.ActiveBatchId).SingleAsync();
            var row = await db.GpuExecutorDispatches.SingleAsync(dispatch => dispatch.BatchId == id);
            return new(row.BatchId, row.CapacitySlotKey, row.ExecutorKey, row.AdmissionGeneration, row.DispatchId);
        }

        var taskId = await EnqueueAsync(warm);
        Assert.Equal(GpuAdmissionDisposition.Admit, (await AdmitAsync()).Disposition);
        var initial = await ActiveHandleAsync();
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), initial), default)).Accepted);
        var epoch = Assert.IsType<GpuResidentModelFence>(await resident.BeginBorrowAsync(initial, Guid.NewGuid(),
            initial.DispatchId, warm, owner, null, default));
        Assert.True((await scheduler.RecordReceiptAsync(new(Guid.NewGuid(), initial, taskId, GpuMiniTaskBoundaryDisposition.Completed,
            null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed), default)).Accepted);
        Assert.False((await scheduler.ApplyBatchCallbackAsync(Guid.NewGuid(), new(initial, GpuBatchCallbackKind.Completed,
            [new(taskId, GpuMiniTaskBoundaryDisposition.Completed)], true), default)).Accepted);
        Assert.True((await scheduler.CompleteResidentBatchAsync(Guid.NewGuid(), initial, taskId, epoch, warm, default)).Accepted);

        for (var index = 0; index < 12; index++) await EnqueueAsync(cold);
        var warmTask = await EnqueueAsync(warm);
        if (foreground != 0)
        {
            var instance = Guid.NewGuid();
            var executor = $"retrieval-gpu:{instance:N}";
            var sameModelForeignProcess = foreground == 2;
            var privateOwner = sameModelForeignProcess ? new SqlGpuSchedulerStore(factory, timeProvider: clock,
                interactiveOwner: owner with { ProcessId = owner.ProcessId + 1 }, sourceAdmission: new FixtureSourceAdmission(factory)) : scheduler;
            var entryModel = sameModelForeignProcess ? warm : cold;
            await privateOwner.HandoffInteractiveAsync(new(Guid.NewGuid(), instance, executor, entryModel.RuntimeKey,
                entryModel.SettingsFingerprint, 1024, clock.GetUtcNow().AddSeconds(1), clock.GetUtcNow().AddSeconds(9)), default);
            Assert.Equal(GpuAdmissionDisposition.Busy, (await AdmitAsync()).Disposition);
            var evicting = Assert.Single(await resident.ReadLiveAsync(default));
            Assert.Equal((int)GpuModelResidencyState.Evicting, evicting.State);
            Assert.True(await resident.ConfirmEvictionAsync(SqlGpuModelResidencyStore.ReadFence(evicting),
                GpuResidentReleaseKind.NativeSessionDisposed, default));
            Assert.Equal(GpuAdmissionDisposition.Admit, (await AdmitAsync()).Disposition);
            Assert.Equal(executor, (await ActiveHandleAsync()).ExecutorKey);
        }
        else
        {
            Assert.Equal(GpuAdmissionDisposition.Admit, (await AdmitAsync()).Disposition);
            var active = await ActiveHandleAsync();
            var borrowed = Assert.IsType<GpuResidentModelFence>(await resident.ReadBorrowAsync(active, default));
            Assert.Equal(epoch.ResidencyId, borrowed.ResidencyId);
            Assert.Equal(3, borrowed.Version);
            await using var result = await factory.CreateDbContextAsync();
            Assert.Equal(active.BatchId, await result.GpuMiniTasks.Where(task => task.Id == warmTask)
                .Select(task => task.BatchId).SingleAsync());
            using var newRuntime = new GpuModelResidencyRuntime(resident, options, new LocalProcess(owner),
                new ChannelGpuSchedulerWakeSignal(), clock, new ReadyDatabase(), NullLogger<GpuModelResidencyRuntime>.Instance, scheduler);
            var context = new GpuOwnedWorkContext(active, warm.RuntimeKey, warm.SettingsFingerprint, default);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => newRuntime.GetModelAsync<FakeModel>(
                    context, warm, () => throw new Xunit.Sdk.XunitException("An unproven warm grant must not allocate a model.")).AsTask());
                Assert.Equal("gpu-process-resident-model-missing", refusal.Message);
            }
            Assert.False(await newRuntime.ReleaseAsync(context, default));
            Assert.Equal(borrowed, await resident.ReadBorrowAsync(active, default));
        }
    }

    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ready_runtime_releases_owned_parked_model_on_stop_even_without_background_loop(bool startBackgroundLoop)
    {
        var setup = new SqlGpuAdmissionTests(fixture);
        var factory = await setup.CreateEnvironmentAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var owner = new GpuInteractiveOwnerIdentity(42, clock.GetUtcNow(), new string('a', 64));
        var model = new GpuResidentModelKey("resident-test:model", "settings", "embedding");
        var policy = new GpuWorkloadPolicy([new(model.RuntimeKey, model.SettingsFingerprint, GpuWorkloadKind.Retrieval)]);
        var options = new GpuSchedulerOptions(1, 4096, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10), policy, [model]);
        var scheduler = new SqlGpuSchedulerStore(factory, timeProvider: clock, interactiveOwner: owner,
            sourceAdmission: new FixtureSourceAdmission(factory));
        var store = new SqlGpuModelResidencyStore(factory, clock);
        using var runtime = new GpuModelResidencyRuntime(store, options, new LocalProcess(owner),
            new ChannelGpuSchedulerWakeSignal(), clock, new ReadyDatabase(), NullLogger<GpuModelResidencyRuntime>.Instance, scheduler);

        await using var db = await factory.CreateDbContextAsync();
        var slotKey = await db.GpuCapacitySlots.Select(slot => slot.SlotKey).SingleAsync();
        var taskId = await setup.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, model.RuntimeKey, model.SettingsFingerprint, 1024);
        var admission = await scheduler.RunAdmissionRoundAsync(GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, slotKey,
                "resident-test:owner", null, candidate.RequiredExecutorKey ?? "resident-test:executor")), default);
        Assert.Equal(GpuAdmissionDisposition.Admit, admission.Disposition);
        var dispatch = await db.GpuExecutorDispatches.AsNoTracking().SingleAsync();
        var handle = new GpuExecutorBatchHandle(dispatch.BatchId, slotKey, dispatch.ExecutorKey,
            dispatch.AdmissionGeneration, dispatch.DispatchId);
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), default)).Accepted);
        var context = new GpuOwnedWorkContext(handle, model.RuntimeKey, model.SettingsFingerprint, default);
        var allocation = await runtime.GetModelAsync(context, model, () => ValueTask.FromResult(new FakeModel()));
        Assert.True((await scheduler.RecordReceiptAsync(new(Guid.NewGuid(), handle, taskId, GpuMiniTaskBoundaryDisposition.Completed,
            null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed), default)).Accepted);
        Assert.True(await runtime.CompleteAsync(context, Guid.NewGuid(), taskId, default));
        var idle = Assert.Single(await store.ReadLiveAsync(default));
        Assert.Equal((int)GpuModelResidencyState.Idle, idle.State);
        Assert.True(idle.IdleUntilUtc > clock.GetUtcNow());
        Assert.False(allocation.PhysicalReleaseConfirmed);

        if (startBackgroundLoop) await runtime.StartAsync(default);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.StopAsync(shutdown.Token);

        Assert.True(allocation.PhysicalReleaseConfirmed);
        var released = await db.GpuModelResidencies.AsNoTracking().SingleAsync(row => row.ResidencyId == idle.ResidencyId);
        Assert.Equal((int)GpuModelResidencyState.Released, released.State);
        Assert.Equal((int)GpuResidentReleaseKind.NativeSessionDisposed, released.ReleaseKind);
        Assert.Equal((int)GpuCapacitySlotState.Available,
            await db.GpuCapacitySlots.Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Idle_capacity_remains_reserved_until_exact_eviction_proof_and_stale_fences_are_refused()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        const string slotKey = "resident-test:gpu:0";
        const string ownerKey = "resident-test:capacity";
        var model = new GpuResidentModelKey("runtime", "settings", "embedding-fp32");
        var owner = new GpuInteractiveOwnerIdentity(42, clock.GetUtcNow(), new string('a', 64));
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), slotKey, "resident-test:executor", 1, Guid.NewGuid());
        var instance = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = slotKey, State = (int)GpuCapacitySlotState.Available, UpdatedAtUtc = clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
            db.GpuBatches.Add(new()
            {
                Id = handle.BatchId, CapacitySlotKey = slotKey, ModelRuntimeKey = model.RuntimeKey,
                SettingsFingerprint = model.SettingsFingerprint, AdmissionGeneration = handle.AdmissionGeneration,
                OwnerKey = ownerKey, State = (int)GpuBatchState.Active, ItemCount = 1, EstimatedBytes = 1,
                CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow()
            });
            db.GpuExecutorDispatches.Add(new()
            {
                DispatchId = handle.DispatchId, BatchId = handle.BatchId, CapacitySlotKey = slotKey,
                OwnerKey = ownerKey, ExecutorKey = handle.ExecutorKey, AdmissionGeneration = handle.AdmissionGeneration,
                State = (int)GpuExecutorDispatchState.Acknowledged, UpdatedAtUtc = clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
            await db.GpuCapacitySlots.Where(slot => slot.SlotKey == slotKey).ExecuteUpdateAsync(s => s
                .SetProperty(slot => slot.State, (int)GpuCapacitySlotState.Reserved)
                .SetProperty(slot => slot.ActiveBatchId, handle.BatchId).SetProperty(slot => slot.OwnerKey, ownerKey));
        }

        var store = new SqlGpuModelResidencyStore(factory, clock);
        var borrowed = Assert.IsType<GpuResidentModelFence>(await store.BeginBorrowAsync(
            handle, Guid.NewGuid(), instance, model, owner, null, default));
        Assert.Null(await store.RequestEvictionAsync(borrowed, GpuResidentEvictionReason.Maintenance, default));

        GpuResidentModelFence idle;
        await using (var db = await factory.CreateDbContextAsync())
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            Assert.Null(await SqlGpuModelResidencyStore.ParkAfterTerminalAsync(db, borrowed, handle, instance, model, clock.GetUtcNow(), default));
            (await db.GpuBatches.SingleAsync(batch => batch.Id == handle.BatchId)).State = (int)GpuBatchState.Completed;
            (await db.GpuExecutorDispatches.SingleAsync(dispatch => dispatch.DispatchId == handle.DispatchId)).State = (int)GpuExecutorDispatchState.Terminal;
            idle = Assert.IsType<GpuResidentModelFence>(await SqlGpuModelResidencyStore.ParkAfterTerminalAsync(
                db, borrowed, handle, instance, model, clock.GetUtcNow(), default));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        Assert.Null(await store.RequestEvictionAsync(idle, GpuResidentEvictionReason.Expired, default));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(await store.RequestEvictionAsync(borrowed, GpuResidentEvictionReason.Expired, default));
        var evicting = Assert.IsType<GpuResidentModelFence>(await store.RequestEvictionAsync(idle, GpuResidentEvictionReason.Expired, default));
        Assert.False(await store.ConfirmEvictionAsync(idle, GpuResidentReleaseKind.NativeSessionDisposed, default));
        Assert.False(await store.ConfirmEvictionAsync(evicting, GpuResidentReleaseKind.ContainedJobEmpty, default));
        Assert.True(await store.ConfirmEvictionAsync(evicting, GpuResidentReleaseKind.NativeSessionDisposed, default));

        await using var verify = await factory.CreateDbContextAsync();
        var slot = await verify.GpuCapacitySlots.SingleAsync(value => value.SlotKey == slotKey);
        Assert.Equal((int)GpuCapacitySlotState.Available, slot.State);
        Assert.Null(slot.ActiveBatchId);
        Assert.Equal(idle.Version, (await verify.GpuModelDetachmentReceipts.SingleAsync(row => row.DispatchId == handle.DispatchId)).Version);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LocalProcess(GpuInteractiveOwnerIdentity owner) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => owner;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity identity) => GpuInteractiveOwnerObservation.Alive;
    }

    private sealed class ReadyDatabase : IDatabaseStartupReadiness
    {
        public bool IsReady => true;
        public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeModel : IGpuProcessResidentModel
    {
        public bool IsIdle => true;
        public bool PhysicalReleaseConfirmed { get; private set; }
        public void Dispose() => PhysicalReleaseConfirmed = true;
    }
}
