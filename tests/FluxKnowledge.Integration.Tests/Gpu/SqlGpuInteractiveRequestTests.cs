using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuInteractiveRequestTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly GpuSchedulerOptions Options = new(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    [NativeSqlServerFact]
    public async Task Wake_acknowledgement_and_new_interactive_handoff_share_a_short_mutation_fence()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var consumptionId = Guid.NewGuid();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
                { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            var state = await setup.GpuSchedulerStates.SingleAsync();
            state.WakeGeneration = 1;
            state.InFlightWakeOperationId = consumptionId;
            state.InFlightWakeGeneration = 1;
            state.InFlightWakeReasons = (int)GpuSchedulerWakeReason.CapacityReleased;
            state.InFlightEffectiveAdmissionReasons = (int)GpuSchedulerWakeReason.CapacityReleased;
            await setup.SaveChangesAsync();
        }

        await using var blocker = new SqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'FluxKnowledge.GpuScheduler.Mutation', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000; SELECT @result;";
            Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 0);
        }

        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));
        Task<bool>? acknowledgement = null;
        Task<GpuMiniTaskHandoffResult>? handoff = null;
        try
        {
            acknowledgement = store.AcknowledgeWakeAsync(Guid.NewGuid(), consumptionId, CancellationToken.None).AsTask();
            await Task.Delay(250);
            Assert.False(acknowledgement.IsCompleted);
            handoff = store.HandoffInteractiveAsync(Request(), CancellationToken.None).AsTask();
            await Task.Delay(250);
            Assert.False(handoff.IsCompleted);
        }
        finally { await transaction.CommitAsync(); }
        Assert.True(await acknowledgement!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True((await handoff!.WaitAsync(TimeSpan.FromSeconds(10))).Committed);

        var pending = await store.ReadWakeStateAsync(CancellationToken.None);
        Assert.Equal(GpuSchedulerWakeReason.WorkReady, pending.Reasons);
        var consumed = await store.ConsumeWakeAsync(Guid.NewGuid(), pending.Generation, CancellationToken.None);
        Assert.True(consumed.Consumed);
        var admitted = await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(
                GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        Assert.True(admitted.Committed);
        Assert.Equal(GpuAdmissionDisposition.Admit, admitted.Disposition);
        Assert.True(await store.AcknowledgeWakeAsync(Guid.NewGuid(), consumed.Snapshot.ConsumptionOperationId!.Value,
            CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuMiniTasks.Where(t => t.ExecutionState == (int)GpuMiniTaskExecutionState.Active).ToArrayAsync());
        Assert.Single(await verify.GpuBatches.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Diagnostic_deadline_is_explicitly_bounded_and_default_store_still_refuses_it()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var request = Request() with { ExecutionDeadlineUtc = Now.AddSeconds(25) };
        var defaultStore = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await defaultStore.HandoffInteractiveAsync(request, CancellationToken.None));
        var diagnosticStore = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now),
            interactiveExecutionTimeout: TimeSpan.FromSeconds(25));
        Assert.True((await diagnosticStore.HandoffInteractiveAsync(request, CancellationToken.None)).Committed);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await diagnosticStore.HandoffInteractiveAsync(Request() with { ExecutionDeadlineUtc = Now.AddSeconds(26) },
                CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(request.ExecutionDeadlineUtc, (await verify.GpuMiniTasks.SingleAsync()).ExecutionDeadlineUtc);
    }

    [NativeSqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Interrupted_owner_recovery_resumes_without_reexecution_or_double_release(int interruption)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var identity = new GpuInteractiveOwnerIdentity(17, Now.AddMinutes(-1), new string('a', 64));
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now), interactiveOwner: identity);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await setup.SaveChangesAsync();
        }
        var request = Request();
        await store.HandoffInteractiveAsync(request, CancellationToken.None);
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var handle = Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        Assert.True((await store.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Committed);
        var reservation = Assert.Single(await store.ReadStaleCapacityReservationsAsync(Now, CancellationToken.None));
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None)).Committed);
        var outcomeEvidence = new GpuExecutorTrustedEvidence(Guid.NewGuid(), handle, "synthetic-process-exit", Now, GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
        var capacityEvidence = outcomeEvidence with { OperationId = Guid.NewGuid(), EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed };
        if (interruption >= 2)
        {
            Assert.True((await store.RecordTrustedEvidenceAsync(outcomeEvidence, CancellationToken.None)).Committed);
            Assert.True((await store.RecordTrustedEvidenceAsync(capacityEvidence, CancellationToken.None)).Committed);
        }
        if (interruption == 3) Assert.True((await store.ReconcileTaskOutcomeAsync(Guid.NewGuid(), new(handle, outcomeEvidence.OperationId, request.RequestId), CancellationToken.None)).Committed);
        if (interruption == 4) Assert.True((await store.ReconcileCapacityAsync(Guid.NewGuid(), new(handle, capacityEvidence.OperationId), CancellationToken.None)).Committed);
        var replacement = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now), interactiveOwner: identity with { StartedAtUtc = Now });
        var recovery = new GpuInteractiveOwnerRecovery(replacement, replacement, replacement,
            new OwnerProbe(identity) { Observation = GpuInteractiveOwnerObservation.Exited }, new ChannelGpuSchedulerWakeSignal(), new FixedTimeProvider(Now));
        Assert.Equal(1, await recovery.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, await recovery.RecoverAsync(CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
        Assert.Equal((int)GpuCapacitySlotState.Available, (await verify.GpuCapacitySlots.SingleAsync()).State);
        Assert.Empty(await verify.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.Empty(await verify.Jobs.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Process_identity_migration_roundtrips_empty_and_refuses_forward_or_down_with_interactive_rows()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        await using var context = await factory.CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();
        var request = Request();
        await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now)).HandoffInteractiveAsync(request, CancellationToken.None);
        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => migrator.MigrateAsync("20260926183316_AddGpuOcrTurnBound"));
        Assert.Equal(51000, error.Number);
        Assert.NotNull((await context.GpuMiniTasks.AsNoTracking().SingleAsync()).InteractiveOwnerProcessId);
        // Later reversible empty-schema migrations can commit before this older
        // migration's guarded Down refuses. Restore the current schema before using
        // the current EF model to clear its disposable test data.
        await context.Database.MigrateAsync();
        await SqlTestData.ClearPipelineAsync(fixture);
        await migrator.MigrateAsync("20260926183316_AddGpuOcrTurnBound");
        await context.Database.MigrateAsync();
        Assert.Equal(0, await context.GpuMiniTasks.CountAsync());
        await migrator.MigrateAsync("20260926183316_AddGpuOcrTurnBound");
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [GpuMiniTasks] ([Id], [SourceRevision], [PriorityLane], [InteractiveExecutorInstanceId], [RequiredExecutorKey],
                [ModelRuntimeKey], [SettingsFingerprint], [EstimatedBytes], [QueueDeadlineUtc], [ExecutionDeadlineUtc], [IdempotencyKey],
                [CreatedAtUtc], [State], [InteractiveCancellationRequested], [AdmissionGeneration], [ReservationAttemptCount])
            VALUES ({request.RequestId}, 0, 0, {request.ExecutorInstanceId}, {request.ExecutorKey}, {request.ModelRuntimeKey},
                {request.SettingsFingerprint}, 10, {request.QueueDeadlineUtc}, {request.ExecutionDeadlineUtc}, {"migration-test"}, {Now}, 0, 0, 0, 0)
            """);
        error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => context.Database.MigrateAsync());
        Assert.Equal(51000, error.Number);
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM GpuMiniTasks").SingleAsync());
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('GpuMiniTasks') AND name = 'InteractiveOwnerProcessId'").SingleAsync());
        await context.Database.ExecuteSqlRawAsync("DELETE FROM GpuMiniTasks");
        await context.Database.MigrateAsync();
    }

    [NativeSqlServerFact]
    public async Task Only_proven_process_exit_recovers_owned_capacity_without_replaying_private_work()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new FixedTimeProvider(Now);
        var identity = new GpuInteractiveOwnerIdentity(17, Now.AddMinutes(-1), new string('a', 64));
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock, interactiveOwner: identity);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await setup.SaveChangesAsync();
        }
        var active = Request();
        var ready = Request();
        await store.HandoffInteractiveAsync(active, CancellationToken.None);
        await store.HandoffInteractiveAsync(ready, CancellationToken.None);
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var handle = Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        Assert.True((await store.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Committed);
        var probe = new OwnerProbe(identity);
        var recovery = new GpuInteractiveOwnerRecovery(store, store, store, probe, new ChannelGpuSchedulerWakeSignal(), clock);
        foreach (var observation in new[] { GpuInteractiveOwnerObservation.Alive, GpuInteractiveOwnerObservation.Unknown })
        {
            probe.Observation = observation;
            Assert.Equal(0, await recovery.RecoverAsync(CancellationToken.None));
            await using var before = await factory.CreateDbContextAsync();
            Assert.Equal((int)GpuCapacitySlotState.Reserved, (await before.GpuCapacitySlots.SingleAsync()).State);
            Assert.Empty(await before.GpuExecutorEvidence.ToArrayAsync());
        }
        probe.Observation = GpuInteractiveOwnerObservation.Exited;
        Assert.Equal(2, await recovery.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, await recovery.RecoverAsync(CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        var slot = await verify.GpuCapacitySlots.SingleAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, slot.State);
        Assert.Null(slot.ActiveBatchId);
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await verify.GpuMiniTasks.SingleAsync(t => t.Id == active.RequestId)).ExecutionState);
        Assert.Equal((int)GpuMiniTaskExecutionState.Cancelled, (await verify.GpuMiniTasks.SingleAsync(t => t.Id == ready.RequestId)).ExecutionState);
        Assert.Equal(2, await verify.GpuExecutorEvidence.CountAsync());
        Assert.Empty(await verify.Jobs.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Process_incarnation_cannot_rebind_an_instance_replay_cancel_or_execute_old_work()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var identity = new GpuInteractiveOwnerIdentity(17, Now.AddMinutes(-1), new string('a', 64));
        var original = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now), interactiveOwner: identity);
        var replacement = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now),
            interactiveOwner: identity with { StartedAtUtc = Now });
        var request = Request();
        await original.HandoffInteractiveAsync(request, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await replacement.HandoffInteractiveAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await replacement.HandoffInteractiveAsync(request with { RequestId = Guid.NewGuid() }, CancellationToken.None));
        Assert.False(await replacement.CancelInteractiveAsync(request.RequestId, request.ExecutorInstanceId, CancellationToken.None));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await setup.SaveChangesAsync();
        }
        await original.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var handle = Assert.Single(await original.ReadPendingDispatchesAsync(CancellationToken.None));
        Assert.NotNull(await original.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.PendingDelivery, CancellationToken.None));
        Assert.Null(await replacement.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.PendingDelivery, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.False((await verify.GpuMiniTasks.SingleAsync()).InteractiveCancellationRequested);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, (await verify.GpuCapacitySlots.SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Interactive_ownership_records_the_host_process_without_persisting_request_text()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var request = Request();
        await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now))
            .HandoffInteractiveAsync(request, CancellationToken.None);
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT InteractiveOwnerProcessId, InteractiveOwnerStartedAtUtc, InteractiveOwnerMachineFingerprint FROM GpuMiniTasks";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Environment.ProcessId, reader.GetInt32(0));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(new DateTimeOffset(process.StartTime.ToUniversalTime()), reader.GetFieldValue<DateTimeOffset>(1));
        Assert.Matches("^[0-9a-f]{64}$", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [NativeSqlServerFact]
    public async Task Execution_read_requires_the_exact_live_owner_dispatch_and_shared_slot()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new MutableTimeProvider(Now);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey, State = 0, UpdatedAtUtc = Now });
            await setup.SaveChangesAsync();
        }
        var request = Request();
        var policy = new GpuWorkloadPolicy([
            new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
            new(request.ModelRuntimeKey, request.SettingsFingerprint, GpuWorkloadKind.Retrieval)]);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), policy);
        var gate = new SharedGpuAdmissionGate(policy, "retrieval-background", 1024);
        await store.HandoffInteractiveAsync(request, CancellationToken.None);
        var admitted = await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options, gate.DecideAsync, CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, admitted.Disposition);
        var handle = Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        var pending = await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.PendingDelivery, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(request.RequestId, pending.RequestId);
        Assert.Equal(request.ModelRuntimeKey, pending.ModelRuntimeKey);
        Assert.False(pending.CancellationRequested);
        Assert.Null(await store.ReadInteractiveExecutionAsync(handle, Guid.NewGuid(), GpuExecutorDispatchState.PendingDelivery, CancellationToken.None));
        Assert.True((await store.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Committed);
        Assert.Null(await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.PendingDelivery, CancellationToken.None));
        Assert.NotNull(await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.Acknowledged, CancellationToken.None));
        foreach (var stale in new[] { handle with { AdmissionGeneration = handle.AdmissionGeneration + 1 },
            handle with { DispatchId = Guid.NewGuid() }, handle with { BatchId = Guid.NewGuid() },
            handle with { CapacitySlotKey = "other-slot" }, handle with { ExecutorKey = "other-executor" } })
            Assert.Null(await store.ReadInteractiveExecutionAsync(stale, request.ExecutorInstanceId, GpuExecutorDispatchState.Acknowledged, CancellationToken.None));
        await using (var corruption = await factory.CreateDbContextAsync())
        {
            var dispatch = await corruption.GpuExecutorDispatches.SingleAsync();
            corruption.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "mismatched-dispatch-slot",
                State = (int)GpuCapacitySlotState.Reserved, ActiveBatchId = handle.BatchId, OwnerKey = dispatch.OwnerKey, UpdatedAtUtc = Now });
            await corruption.SaveChangesAsync();
            dispatch.CapacitySlotKey = "mismatched-dispatch-slot";
            await corruption.SaveChangesAsync();
            Assert.Null(await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.Acknowledged, CancellationToken.None));
            dispatch.CapacitySlotKey = handle.CapacitySlotKey;
            await corruption.SaveChangesAsync();
        }
        clock.Now = Now.AddSeconds(11);
        Assert.True((await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.Acknowledged, CancellationToken.None))!.CancellationRequested);
        clock.Now = Now;
        await store.CancelInteractiveAsync(request.RequestId, request.ExecutorInstanceId, CancellationToken.None);
        Assert.True((await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.Acknowledged, CancellationToken.None))!.CancellationRequested);
        await using var verify = await factory.CreateDbContextAsync();
        var batch = await verify.GpuBatches.SingleAsync();
        var slot = await verify.GpuCapacitySlots.SingleAsync(s => s.SlotKey == handle.CapacitySlotKey);
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), new(batch.Id, slot.SlotKey, batch.OwnerKey,
            batch.AdmissionGeneration, slot.LastHeartbeatAtUtc!.Value, slot.RowVersion), CancellationToken.None)).Committed);
        Assert.Null(await store.ReadInteractiveExecutionAsync(handle, request.ExecutorInstanceId, GpuExecutorDispatchState.Acknowledged, CancellationToken.None));
        await verify.Entry(slot).ReloadAsync();
        Assert.Equal(batch.Id, slot.ActiveBatchId);
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, slot.State);
    }

    [NativeSqlServerFact]
    public async Task Search_owner_has_no_fake_job_is_instance_bound_and_cancelled_active_work_keeps_capacity()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await context.SaveChangesAsync();
        }
        var request = Request();
        var queued = await store.HandoffInteractiveAsync(request, CancellationToken.None);
        Assert.Equal(request.RequestId, queued.MiniTaskId);
        Assert.True((await store.HandoffInteractiveAsync(request, CancellationToken.None)).IsIdempotentReplay);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.HandoffInteractiveAsync(request with { ExecutorInstanceId = Guid.NewGuid() }, CancellationToken.None));
        var conflicting = Request() with { RequestId = request.RequestId };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.HandoffInteractiveAsync(conflicting, CancellationToken.None));
        var gate = new Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>>((candidate, _) =>
        {
            Assert.Equal(request.ExecutorKey, candidate.RequiredExecutorKey);
            return ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, request.ExecutorKey));
        });
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options, gate, CancellationToken.None);
        Assert.False(await store.CancelInteractiveAsync(request.RequestId, Guid.NewGuid(), CancellationToken.None));
        Assert.True(await store.CancelInteractiveAsync(request.RequestId, request.ExecutorInstanceId, CancellationToken.None));
        await store.HandoffInteractiveAsync(Request(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.HandoffInteractiveAsync(Request(), CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.Jobs.ToArrayAsync());
        var task = await verify.GpuMiniTasks.SingleAsync(t => t.Id == request.RequestId);
        Assert.Null(task.ParentJobId);
        Assert.Equal(request.ExecutorInstanceId, task.InteractiveExecutorInstanceId);
        Assert.True(task.InteractiveCancellationRequested);
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, task.ExecutionState);
        var slot = await verify.GpuCapacitySlots.SingleAsync();
        Assert.Equal((int)GpuCapacitySlotState.Reserved, slot.State);
        var batch = await verify.GpuBatches.SingleAsync();
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), new(
            batch.Id, slot.SlotKey, batch.OwnerKey, batch.AdmissionGeneration,
            slot.LastHeartbeatAtUtc!.Value, slot.RowVersion), CancellationToken.None)).Committed);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.HandoffInteractiveAsync(Request(), CancellationToken.None));
        await verify.Entry(slot).ReloadAsync();
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, slot.State);
        Assert.Equal(batch.Id, slot.ActiveBatchId);
    }

    [NativeSqlServerFact]
    public async Task Queue_is_bounded_and_expired_or_cancelled_requests_are_never_admitted()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));
        var first = Request();
        var second = Request();
        await store.HandoffInteractiveAsync(first, CancellationToken.None);
        await store.HandoffInteractiveAsync(second, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.HandoffInteractiveAsync(Request(), CancellationToken.None));
        Assert.True(await store.CancelInteractiveAsync(first.RequestId, first.ExecutorInstanceId, CancellationToken.None));
        await store.HandoffInteractiveAsync(Request(), CancellationToken.None);
        var expiredStore = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now.AddSeconds(3)));
        var result = await expiredStore.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options,
            (_, _) => throw new InvalidOperationException("Expired requests reached admission"), CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.All(await verify.GpuMiniTasks.ToArrayAsync(), task => Assert.Equal((int)GpuMiniTaskExecutionState.Cancelled, task.ExecutionState));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_search_arrivals_cannot_exceed_the_two_request_bound()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var arrivals = Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now))
                    .HandoffInteractiveAsync(Request(), CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException exception) when (exception.Message == "interactive-queue-full") { return false; }
        });
        Assert.Equal(2, (await Task.WhenAll(arrivals)).Count(success => success));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.GpuMiniTasks.CountAsync());
        Assert.Empty(await verify.Jobs.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Conditional_search_handoff_declines_busy_gpu_without_persisting_a_request()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await context.SaveChangesAsync();
        }
        var first = Request() with { DeclineWhenGpuBusy = true };
        var admitted = await store.HandoffInteractiveAsync(first, CancellationToken.None);
        Assert.True(admitted.Committed);
        var second = Request() with { DeclineWhenGpuBusy = true };
        await Assert.ThrowsAsync<GpuInteractiveBusyWithoutHandoffException>(async () =>
            await store.HandoffInteractiveAsync(second, CancellationToken.None));
        Assert.True((await store.HandoffInteractiveAsync(first, CancellationToken.None)).IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuMiniTasks.ToArrayAsync());
        Assert.Null(await verify.GpuMiniTasks.SingleOrDefaultAsync(task => task.Id == second.RequestId));
    }

    [NativeSqlServerFact]
    public async Task Conditional_search_handoff_declines_reserved_or_uncertain_capacity()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = (int)GpuCapacitySlotState.Reserved, UpdatedAtUtc = Now });
            await context.SaveChangesAsync();
        }
        var request = Request() with { DeclineWhenGpuBusy = true };
        await Assert.ThrowsAsync<GpuInteractiveBusyWithoutHandoffException>(async () =>
            await store.HandoffInteractiveAsync(request, CancellationToken.None));
        await using (var context = await factory.CreateDbContextAsync())
        {
            var slot = await context.GpuCapacitySlots.SingleAsync();
            slot.State = (int)GpuCapacitySlotState.Uncertain;
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<GpuInteractiveBusyWithoutHandoffException>(async () =>
            await store.HandoffInteractiveAsync(request, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Racing_conditional_search_arrivals_commit_only_one_gpu_handoff()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await context.SaveChangesAsync();
        }
        var arrivals = Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now))
                    .HandoffInteractiveAsync(Request() with { DeclineWhenGpuBusy = true }, CancellationToken.None);
                return true;
            }
            catch (GpuInteractiveBusyWithoutHandoffException) { return false; }
        });
        Assert.Equal(1, (await Task.WhenAll(arrivals)).Count(value => value));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuMiniTasks.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Exclusive_owner_constraint_rejects_missing_owner_and_missing_execution_deadline()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var request = Request();
        await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now)).HandoffInteractiveAsync(request, CancellationToken.None);
        await using var context = await factory.CreateDbContextAsync();
        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE [GpuMiniTasks] SET [ExecutionDeadlineUtc] = NULL WHERE [Id] = {request.RequestId}"));
        Assert.Equal(547, error.Number);
        error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE [GpuMiniTasks] SET [InteractiveExecutorInstanceId] = NULL WHERE [Id] = {request.RequestId}"));
        Assert.Equal(547, error.Number);
    }

    [NativeSqlServerFact]
    public async Task Request_expiring_while_the_gate_waits_is_cancelled_without_a_capacity_reservation()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new MutableTimeProvider(Now);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock);
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = Now });
            await context.SaveChangesAsync();
        }
        var request = Request();
        await store.HandoffInteractiveAsync(request, CancellationToken.None);
        var operationId = Guid.NewGuid();
        var result = await store.RunAdmissionRoundAsync(operationId, GpuSchedulerWakeReason.WorkReady, Options,
            (candidate, _) =>
            {
                clock.Now = Now.AddSeconds(3);
                return ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey));
            }, CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        var replay = await store.RunAdmissionRoundAsync(operationId, GpuSchedulerWakeReason.WorkReady, Options,
            (_, _) => throw new InvalidOperationException("Receipt replay re-entered admission"), CancellationToken.None);
        Assert.Equal(result with { IsIdempotentReplay = true }, replay);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.GpuBatches.ToArrayAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, (await verify.GpuCapacitySlots.SingleAsync()).State);
        Assert.Equal((int)GpuMiniTaskExecutionState.Cancelled, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
    }

    private static GpuInteractiveHandoffRequest Request()
    {
        var instance = Guid.NewGuid();
        return new(Guid.NewGuid(), instance, $"retrieval-gpu:{instance:N}",
            "synthetic-retrieval-v1", "synthetic-settings-v1", 10, Now.AddSeconds(2), Now.AddSeconds(10));
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class OwnerProbe(GpuInteractiveOwnerIdentity identity) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => identity;
        public GpuInteractiveOwnerObservation Observation { get; set; }
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner)
        {
            Assert.Equal(identity, owner);
            return Observation;
        }
    }
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
