using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuBatchLifecycleTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Releasing_safe_boundary_preserves_active_lower_work_until_boundary_then_admits_waiting_higher_priority_work()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var lowerTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        var higherTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "runtime", "settings", 10);
        await using var read = await factory.CreateDbContextAsync();
        var lowerBatch = await read.GpuBatches.SingleAsync();

        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await read.GpuMiniTasks.Where(task => task.Id == lowerTaskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await read.GpuMiniTasks.Where(task => task.Id == higherTaskId).Select(task => task.ExecutionState).SingleAsync());

        var boundary = await new SqlGpuSchedulerStore(factory).ApplyBatchCallbackAsync(
            new GpuBatchCallback(
                lowerBatch.Id, "slot-a", "test-owner", lowerBatch.AdmissionGeneration,
                GpuBatchCallbackKind.SafeBoundary,
                [new GpuMiniTaskBoundaryOutcome(lowerTaskId, GpuMiniTaskBoundaryDisposition.OutcomeUncertain)],
                CapacityReleased: true),
            CancellationToken.None);

        Assert.True(boundary.Accepted);
        Assert.True((await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"))).Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verify.GpuMiniTasks.Where(task => task.Id == lowerTaskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(task => task.Id == higherTaskId).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Capacity_released_busy_commits_obsolete_deferral_cleanup_and_preserves_lower_lane_bypass_protection()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var futureHigherTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "runtime", "settings", 10, DateTimeOffset.Parse("2026-07-29T10:02:00+00:00"));
        var lowerTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        GpuPriorityLane? releaseCandidateLane = null;
        var busyDecisionCount = 0;
        var operationId = Guid.NewGuid();
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider());
        var options = new GpuSchedulerOptions(
            3,
            100,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10));

        var result = await store.RunAdmissionRoundAsync(
            operationId,
            GpuSchedulerWakeReason.CapacityReleased,
            options,
            (candidate, _) =>
            {
                releaseCandidateLane = candidate.PriorityLane;
                busyDecisionCount++;
                return ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
            },
            CancellationToken.None);
        var replay = await store.RunAdmissionRoundAsync(
            operationId,
            GpuSchedulerWakeReason.CapacityReleased,
            options,
            (_, _) => throw new InvalidOperationException("A committed Busy receipt must replay before the gate."),
            CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        Assert.Equal(result, replay);
        Assert.Equal(GpuPriorityLane.InteractiveRetrieval, releaseCandidateLane);
        Assert.Equal(1, busyDecisionCount);
        await using (var afterBusy = await factory.CreateDbContextAsync())
        {
            Assert.Null(await afterBusy.GpuMiniTasks
                .Where(task => task.Id == futureHigherTaskId)
                .Select(task => task.DeferredUntilUtc)
                .SingleAsync());
            Assert.Null(await afterBusy.GpuSchedulerStates
                .Select(state => state.NextDeferredAtUtc)
                .SingleAsync());
            var receipt = await afterBusy.GpuSchedulerOperationReceipts
                .SingleAsync(candidate => candidate.OperationId == operationId);
            Assert.True(receipt.Committed);
            Assert.Equal((int)GpuAdmissionDisposition.Busy, receipt.AdmissionDisposition);
        }

        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"), wakeReason: GpuSchedulerWakeReason.WorkReady);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(task => task.Id == futureHigherTaskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == lowerTaskId).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Advancing_time_without_a_durable_callback_or_reconciliation_does_not_mutate_active_work_or_capacity()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var before = await factory.CreateDbContextAsync();
        var batch = await before.GpuBatches.SingleAsync();
        var initialWakeGeneration = await before.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();
        var parentState = await before.Jobs.Where(job => job.Id == before.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ParentJobId).Single()).Select(job => job.PublicState).SingleAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2036-07-29T10:00:00+00:00"));
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock);

        clock.Advance(TimeSpan.FromDays(365));
        await store.ReadWakeStateAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.GpuBatches.CountAsync());
        Assert.Equal((int)GpuBatchState.Active, await verify.GpuBatches.Where(candidate => candidate.Id == batch.Id).Select(candidate => candidate.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal(parentState, await verify.Jobs.Where(job => job.Id == verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ParentJobId).Single()).Select(job => job.PublicState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
        Assert.Equal(initialWakeGeneration, await verify.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Diagnostic_uncertainty_keeps_waiting_higher_priority_work_pending_and_blocks_admission()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        var waitingTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "runtime", "settings", 10);
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);

        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, batch.Id),
            CancellationToken.None)).Committed);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a")));

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == waitingTaskId).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Callback_fence_matrix_rejects_wrong_and_late_callbacks_without_a_durable_side_effect()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var wakeGeneration = await read.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();

        var invalidCallbacks = new[]
        {
            new GpuBatchCallback(Guid.NewGuid(), "slot-a", "test-owner", batch.AdmissionGeneration, GpuBatchCallbackKind.SafeBoundary, [], false),
            new GpuBatchCallback(batch.Id, "wrong-slot", "test-owner", batch.AdmissionGeneration, GpuBatchCallbackKind.SafeBoundary, [], false),
            new GpuBatchCallback(batch.Id, "slot-a", "wrong-owner", batch.AdmissionGeneration, GpuBatchCallbackKind.SafeBoundary, [], false),
            new GpuBatchCallback(batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration + 1, GpuBatchCallbackKind.SafeBoundary, [], false)
        };
        foreach (var callback in invalidCallbacks)
        {
            Assert.False((await store.ApplyBatchCallbackAsync(callback, CancellationToken.None)).Accepted);
        }

        var completed = new GpuBatchCallback(batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration,
            GpuBatchCallbackKind.Completed, [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)], true);
        Assert.True((await store.ApplyBatchCallbackAsync(completed, CancellationToken.None)).Accepted);
        await using var afterCompleted = await factory.CreateDbContextAsync();
        var completionWakeGeneration = await afterCompleted.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();

        Assert.False((await store.ApplyBatchCallbackAsync(completed, CancellationToken.None)).Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(wakeGeneration + 1, completionWakeGeneration);
        Assert.Equal(completionWakeGeneration, await verify.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, await verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Completed_fenced_callback_completes_only_its_task_releases_slot_and_preserves_parent_gpu_processing()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();

        var result = await new SqlGpuSchedulerStore(factory).ApplyBatchCallbackAsync(
            new GpuBatchCallback(
                batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration,
                GpuBatchCallbackKind.Completed,
                [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)],
                CapacityReleased: true),
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, await verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
        Assert.Null(await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.ActiveBatchId).SingleAsync());
        Assert.Equal((int)PublicJobState.GpuProcessing, await verify.Jobs.Where(job => job.Id == verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ParentJobId).Single()).Select(job => job.PublicState).SingleAsync());
        var wake = await verify.GpuSchedulerStates.SingleAsync();
        Assert.True(((GpuSchedulerWakeReason)wake.PendingWakeReasons).HasFlag(GpuSchedulerWakeReason.CapacityReleased));
    }

    [NativeSqlServerFact]
    public async Task Retained_safe_boundary_keeps_slot_reserved_and_rejects_a_duplicate_callback_without_state_change()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var callback = new GpuBatchCallback(
            batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration,
            GpuBatchCallbackKind.SafeBoundary, [], CapacityReleased: false);
        var store = new SqlGpuSchedulerStore(factory);

        var first = await store.ApplyBatchCallbackAsync(callback, CancellationToken.None);
        var duplicate = await store.ApplyBatchCallbackAsync(callback, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.False(duplicate.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuBatchState.AtSafeBoundary, await verify.GpuBatches.Where(candidate => candidate.Id == batch.Id).Select(candidate => candidate.State).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
        Assert.Equal(batch.Id, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.ActiveBatchId).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Wrong_fence_is_rejected_without_advancing_wake_or_changing_the_active_batch()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var wakeGeneration = (await read.GpuSchedulerStates.SingleAsync()).WakeGeneration;

        var result = await new SqlGpuSchedulerStore(factory).ApplyBatchCallbackAsync(
            new GpuBatchCallback(batch.Id, "slot-a", "wrong-owner", batch.AdmissionGeneration,
                GpuBatchCallbackKind.CapacityReleased,
                [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.OutcomeUncertain)],
                CapacityReleased: true),
            CancellationToken.None);

        Assert.False(result.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(wakeGeneration, await verify.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Case_only_lifecycle_fences_reject_without_changing_the_fenced_scheduler_state()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var initialWakeGeneration = await read.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var uncertaintyRequest = await ReadCapacityUncertaintyRequestAsync(factory, batch.Id);

        var callback = await store.ApplyBatchCallbackAsync(
            new GpuBatchCallback(
                batch.Id,
                "slot-a",
                "TEST-OWNER",
                batch.AdmissionGeneration,
                GpuBatchCallbackKind.SafeBoundary,
                [],
                CapacityReleased: false),
            CancellationToken.None);

        Assert.False(callback.Accepted);
        await AssertActiveReservationAsync(
            factory,
            batch.Id,
            taskId,
            initialWakeGeneration);

        var uncertainty = await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            uncertaintyRequest with { OwnerKey = "TEST-OWNER" },
            CancellationToken.None);

        Assert.False(uncertainty.Committed);
        await AssertActiveReservationAsync(
            factory,
            batch.Id,
            taskId,
            initialWakeGeneration);

        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            uncertaintyRequest,
            CancellationToken.None)).Committed);

        var capacity = await store.ReconcileCapacityAsync(
            Guid.NewGuid(),
            new GpuTrustedCapacityReconciliation(
                batch.Id,
                "slot-a",
                "TEST-OWNER",
                batch.AdmissionGeneration,
                SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass),
            CancellationToken.None);

        Assert.False(capacity.Committed);
        await AssertUncertainReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);

        var outcome = await store.ReconcileTaskOutcomeAsync(
            Guid.NewGuid(),
            new GpuTaskOutcomeReconciliation(
                batch.Id,
                "slot-a",
                "TEST-OWNER",
                batch.AdmissionGeneration,
                [taskId],
                SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass),
            CancellationToken.None);

        Assert.False(outcome.Committed);
        await AssertUncertainReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);
    }

    [NativeSqlServerFact]
    public async Task Trailing_whitespace_lifecycle_fences_reject_without_changing_the_fenced_scheduler_state()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var initialWakeGeneration = await read.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var uncertaintyRequest = await ReadCapacityUncertaintyRequestAsync(factory, batch.Id);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.ApplyBatchCallbackAsync(
                new GpuBatchCallback(
                    batch.Id,
                    "slot-a ",
                    "test-owner",
                    batch.AdmissionGeneration,
                    GpuBatchCallbackKind.SafeBoundary,
                    [],
                    CapacityReleased: false),
                CancellationToken.None));
        await AssertActiveReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.ApplyBatchCallbackAsync(
                new GpuBatchCallback(
                    batch.Id,
                    "slot-a",
                    "test-owner ",
                    batch.AdmissionGeneration,
                    GpuBatchCallbackKind.SafeBoundary,
                    [],
                    CapacityReleased: false),
                CancellationToken.None));
        await AssertActiveReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.MarkCapacityUncertainAsync(
                Guid.NewGuid(),
                uncertaintyRequest with { CapacitySlotKey = "slot-a " },
                CancellationToken.None));
        await AssertActiveReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.MarkCapacityUncertainAsync(
                Guid.NewGuid(),
                uncertaintyRequest with { OwnerKey = "test-owner " },
                CancellationToken.None));
        await AssertActiveReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);

        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            uncertaintyRequest,
            CancellationToken.None)).Committed);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.ReconcileCapacityAsync(
                Guid.NewGuid(),
                new GpuTrustedCapacityReconciliation(
                    batch.Id,
                    "slot-a",
                    "test-owner ",
                    batch.AdmissionGeneration,
                    SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass),
                CancellationToken.None));
        await AssertUncertainReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.ReconcileTaskOutcomeAsync(
                Guid.NewGuid(),
                new GpuTaskOutcomeReconciliation(
                    batch.Id,
                    "slot-a",
                    "test-owner ",
                    batch.AdmissionGeneration,
                    [taskId],
                    SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass),
                CancellationToken.None));
        await AssertUncertainReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);
    }

    [NativeSqlServerFact]
    public async Task Refreshed_heartbeat_after_stale_read_cannot_be_marked_uncertain()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var initialWakeGeneration = await read.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var staleCutoff = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00");
        var staleRequest = Assert.Single(
            await store.ReadStaleCapacityReservationsAsync(staleCutoff, CancellationToken.None));

        await using (var refresh = await factory.CreateDbContextAsync())
        {
            var slot = await refresh.GpuCapacitySlots.SingleAsync(candidate => candidate.SlotKey == "slot-a");
            slot.LastHeartbeatAtUtc = staleCutoff.AddMinutes(1);
            slot.UpdatedAtUtc = staleCutoff.AddMinutes(1);
            await refresh.SaveChangesAsync();
        }

        var result = await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            staleRequest,
            CancellationToken.None);

        Assert.False(result.Committed);
        await AssertActiveReservationAsync(factory, batch.Id, taskId, initialWakeGeneration);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(
            staleCutoff.AddMinutes(1),
            await verification.GpuCapacitySlots
                .Where(candidate => candidate.SlotKey == "slot-a")
                .Select(candidate => candidate.LastHeartbeatAtUtc)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Retained_safe_boundary_invalidates_stale_uncertainty_evidence_without_releasing_capacity()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var initialWakeGeneration = await read.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var staleRequest = Assert.Single(await store.ReadStaleCapacityReservationsAsync(
            DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"), CancellationToken.None));

        var boundary = await store.ApplyBatchCallbackAsync(Guid.NewGuid(), new GpuBatchCallback(
            batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration,
            GpuBatchCallbackKind.SafeBoundary, [], CapacityReleased: false), CancellationToken.None);
        var uncertainty = await store.MarkCapacityUncertainAsync(Guid.NewGuid(), staleRequest, CancellationToken.None);

        Assert.True(boundary.Committed);
        Assert.False(uncertainty.Committed);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuBatchState.AtSafeBoundary, await verification.GpuBatches
            .Where(candidate => candidate.Id == batch.Id).Select(candidate => candidate.State).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verification.GpuCapacitySlots
            .Where(candidate => candidate.SlotKey == "slot-a").Select(candidate => candidate.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verification.GpuMiniTasks
            .Where(candidate => candidate.Id == taskId).Select(candidate => candidate.ExecutionState).SingleAsync());
        Assert.Equal(initialWakeGeneration + 1, await verification.GpuSchedulerStates
            .Select(candidate => candidate.WakeGeneration).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Callback_snapshots_caller_owned_outcomes_before_validation()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var outcomes = new SwappingReadOnlyList<GpuMiniTaskBoundaryOutcome>(
            [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)],
            [new GpuMiniTaskBoundaryOutcome(Guid.NewGuid(), GpuMiniTaskBoundaryDisposition.Completed)]);

        var result = await new SqlGpuSchedulerStore(factory).ApplyBatchCallbackAsync(
            new GpuBatchCallback(
                batch.Id,
                "slot-a",
                "test-owner",
                batch.AdmissionGeneration,
                GpuBatchCallbackKind.Completed,
                outcomes,
                CapacityReleased: true),
            CancellationToken.None);

        Assert.True(result.Accepted);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Completed,
            await verification.GpuMiniTasks
                .Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Outcome_reconciliation_snapshots_caller_owned_task_ids_before_validation()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, batch.Id),
            CancellationToken.None)).Committed);
        var taskIds = new SwappingReadOnlyList<Guid>([taskId], [Guid.NewGuid()]);

        var result = await store.ReconcileTaskOutcomeAsync(
            Guid.NewGuid(),
            new GpuTaskOutcomeReconciliation(
                batch.Id,
                "slot-a",
                "test-owner",
                batch.AdmissionGeneration,
                taskIds,
                SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass),
            CancellationToken.None);

        Assert.True(result.Committed);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.OutcomeUncertain,
            await verification.GpuMiniTasks
                .Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Explicit_outcome_reconciliation_preserves_uncertain_capacity_and_parent_job_before_trusted_capacity_release()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);

        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, batch.Id),
            CancellationToken.None)).Committed);
        Assert.True((await store.ReconcileTaskOutcomeAsync(
            new GpuTaskOutcomeReconciliation(
                batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration, [taskId],
                SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass),
            CancellationToken.None)).Committed);

        await using (var uncertain = await factory.CreateDbContextAsync())
        {
            Assert.Equal((int)GpuCapacitySlotState.Uncertain, await uncertain.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
            Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await uncertain.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
            Assert.Equal((int)PublicJobState.GpuProcessing, await uncertain.Jobs.Where(job => job.Id == uncertain.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ParentJobId).Single()).Select(job => job.PublicState).SingleAsync());
        }

        Assert.True((await store.ReconcileCapacityAsync(
            new GpuTrustedCapacityReconciliation(batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration, SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass),
            CancellationToken.None)).Committed);

        await using var released = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await released.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await released.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)PublicJobState.GpuProcessing, await released.Jobs.Where(job => job.Id == released.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ParentJobId).Single()).Select(job => job.PublicState).SingleAsync());
        var reasons = (GpuSchedulerWakeReason)await released.GpuSchedulerStates
            .Select(state => state.PendingWakeReasons)
            .SingleAsync();
        Assert.True(reasons.HasFlag(GpuSchedulerWakeReason.Reconciliation));
        Assert.True(reasons.HasFlag(GpuSchedulerWakeReason.CapacityReleased));
    }

    [NativeSqlServerFact]
    public async Task Outcome_reconciliation_after_capacity_release_and_slot_reuse_changes_only_the_old_batch_tasks()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var oldTaskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var oldRead = await factory.CreateDbContextAsync();
        var oldBatch = await oldRead.GpuBatches.SingleAsync();
        var oldParentId = await oldRead.GpuMiniTasks
            .Where(task => task.Id == oldTaskId)
            .Select(task => task.ParentJobId)
            .SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);

        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, oldBatch.Id),
            CancellationToken.None)).Committed);
        Assert.True((await store.ReconcileCapacityAsync(
            new GpuTrustedCapacityReconciliation(
                oldBatch.Id,
                "slot-a",
                "test-owner",
                oldBatch.AdmissionGeneration,
                SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass),
            CancellationToken.None)).Committed);

        var laterTaskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var occupied = await factory.CreateDbContextAsync();
        var laterBatch = await occupied.GpuBatches.SingleAsync(batch => batch.Id != oldBatch.Id);

        var outcome = await store.ReconcileTaskOutcomeAsync(
            new GpuTaskOutcomeReconciliation(
                oldBatch.Id,
                "slot-a",
                "test-owner",
                oldBatch.AdmissionGeneration,
                [oldTaskId],
                SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass),
            CancellationToken.None);

        Assert.True(outcome.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.OutcomeUncertain,
            await verify.GpuMiniTasks.Where(task => task.Id == oldTaskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Active,
            await verify.GpuMiniTasks.Where(task => task.Id == laterTaskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
        var slot = await verify.GpuCapacitySlots.SingleAsync(candidate => candidate.SlotKey == "slot-a");
        Assert.Equal((int)GpuCapacitySlotState.Reserved, slot.State);
        Assert.Equal(laterBatch.Id, slot.ActiveBatchId);
        Assert.Equal(
            (int)PublicJobState.GpuProcessing,
            await verify.Jobs.Where(job => job.Id == oldParentId)
                .Select(job => job.PublicState)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Stale_capacity_reconciliation_cannot_free_or_mutate_a_later_slot_occupant()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var firstTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var firstBatch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, firstBatch.Id),
            CancellationToken.None)).Committed);
        Assert.True((await store.ReconcileTaskOutcomeAsync(new GpuTaskOutcomeReconciliation(firstBatch.Id, "slot-a", "test-owner", firstBatch.AdmissionGeneration, [firstTaskId], SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass), CancellationToken.None)).Committed);
        Assert.True((await store.ReconcileCapacityAsync(new GpuTrustedCapacityReconciliation(firstBatch.Id, "slot-a", "test-owner", firstBatch.AdmissionGeneration, SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass), CancellationToken.None)).Committed);

        var laterTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var occupied = await factory.CreateDbContextAsync();
        var laterBatch = await occupied.GpuBatches.SingleAsync(batch => batch.Id != firstBatch.Id);

        Assert.False((await store.ReconcileCapacityAsync(new GpuTrustedCapacityReconciliation(firstBatch.Id, "slot-a", "test-owner", firstBatch.AdmissionGeneration, SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass), CancellationToken.None)).Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.State).SingleAsync());
        Assert.Equal(laterBatch.Id, await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a").Select(slot => slot.ActiveBatchId).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(task => task.Id == laterTaskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verify.GpuMiniTasks.Where(task => task.Id == firstTaskId).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Uncertainty_retry_after_status_publication_failure_replays_the_same_committed_transition()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var setup = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(setup, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(setup, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await setup.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var operationId = Guid.NewGuid();
        var request = await ReadCapacityUncertaintyRequestAsync(setup, batch.Id);
        var throwingCoordinator = CreateCoordinator(
            new SqlGpuSchedulerStore(setup),
            new ThrowingStatusPublisher(),
            new RecordingWakeSignal());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await throwingCoordinator.MarkCapacityUncertainAsync(operationId, request, CancellationToken.None));

        var result = await CreateCoordinator(
                new SqlGpuSchedulerStore(setup),
                new NullStatusPublisher(),
                new RecordingWakeSignal())
            .MarkCapacityUncertainAsync(operationId, request, CancellationToken.None);

        Assert.True(result.Committed);
        await using var verify = await setup.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, await verify.GpuCapacitySlots.Select(slot => slot.State).SingleAsync());
        Assert.Equal(
            (int)GpuBatchState.CapacityUncertain,
            await verify.GpuBatches.Where(candidate => candidate.Id == batch.Id)
                .Select(candidate => candidate.State)
                .SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationId == operationId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Uncertainty_receipt_rejects_same_operation_id_with_different_immutable_input()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var operationId = Guid.NewGuid();
        var request = await ReadCapacityUncertaintyRequestAsync(factory, batch.Id);
        var store = new SqlGpuSchedulerStore(factory);

        Assert.True((await store.MarkCapacityUncertainAsync(operationId, request, CancellationToken.None)).Committed);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkCapacityUncertainAsync(
                operationId,
                request with { OwnerKey = "different-owner" },
                CancellationToken.None));

        var heartbeatMismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkCapacityUncertainAsync(
                operationId,
                request with { ObservedLastHeartbeatAtUtc = request.ObservedLastHeartbeatAtUtc.AddTicks(1) },
                CancellationToken.None));
        var differentRowVersion = request.ObservedSlotRowVersion.ToArray();
        differentRowVersion[0] ^= 0x01;
        var rowVersionMismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkCapacityUncertainAsync(
                operationId,
                request with { ObservedSlotRowVersion = differentRowVersion },
                CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not match", heartbeatMismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not match", rowVersionMismatch.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuCapacitySlotState.Uncertain,
            await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationId == operationId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Post_commit_retry_of_outcome_reconciliation_returns_the_original_result_without_a_second_transition()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var setup = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(setup, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(setup, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await setup.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        await new SqlGpuSchedulerStore(setup).MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(setup, batch.Id),
            CancellationToken.None);
        var retries = 0;
        var store = new SqlGpuSchedulerStore(new RetryFactory(_fixture.ConnectionString), afterLifecycleCommitted: _ =>
            Interlocked.Increment(ref retries) == 1 ? ValueTask.FromException(new PostCommitTransientException()) : ValueTask.CompletedTask);

        var result = await store.ReconcileTaskOutcomeAsync(new GpuTaskOutcomeReconciliation(batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration, [taskId], SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass), CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(1, retries);
        await using var verify = await setup.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verify.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts.Where(receipt => receipt.OperationKind == "outcome-reconciliation" && receipt.BatchId == batch.Id).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Post_commit_retry_of_capacity_reconciliation_returns_the_original_result_without_affecting_a_later_occupant()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var setup = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(setup, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(setup, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await setup.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        await new SqlGpuSchedulerStore(setup).MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(setup, batch.Id),
            CancellationToken.None);
        var retries = 0;
        var store = new SqlGpuSchedulerStore(new RetryFactory(_fixture.ConnectionString), afterLifecycleCommitted: _ =>
            Interlocked.Increment(ref retries) == 1 ? ValueTask.FromException(new PostCommitTransientException()) : ValueTask.CompletedTask);

        var result = await store.ReconcileCapacityAsync(new GpuTrustedCapacityReconciliation(batch.Id, "slot-a", "test-owner", batch.AdmissionGeneration, SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass), CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(1, retries);
        await using var verify = await setup.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(slot => slot.State).SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts.Where(receipt => receipt.OperationKind == "capacity-reconciliation" && receipt.BatchId == batch.Id).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Consumed_wake_is_replayable_until_its_fenced_acknowledgement()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var setup = await admission.CreateEnvironmentAsync();
        var expectedReasons = GpuSchedulerWakeReason.WorkReady | GpuSchedulerWakeReason.CapacityReleased;
        await using (var arrange = await setup.CreateDbContextAsync())
        {
            var schedulerState = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            schedulerState.WakeGeneration = 4;
            schedulerState.PendingWakeReasons = (int)expectedReasons;
            schedulerState.NextDeferredAtUtc = DateTimeOffset.Parse("2026-07-29T13:00:00+00:00");
            await arrange.SaveChangesAsync();
        }

        var retries = 0;
        var store = new SqlGpuSchedulerStore(
            new RetryFactory(_fixture.ConnectionString),
            afterWakeConsumptionCommitted: _ =>
                Interlocked.Increment(ref retries) == 1
                    ? ValueTask.FromException(new PostCommitTransientException())
                    : ValueTask.CompletedTask);

        Assert.Equal(expectedReasons, (await store.ReadWakeStateAsync(CancellationToken.None)).Reasons);
        var result = await store.ConsumeWakeAsync(4, CancellationToken.None);

        Assert.True(result.Consumed);
        Assert.Equal(4, result.Snapshot.Generation);
        Assert.Equal(expectedReasons, result.Snapshot.Reasons);
        Assert.Equal(DateTimeOffset.Parse("2026-07-29T13:00:00+00:00"), result.Snapshot.NextDeferredAtUtc);
        Assert.Equal(1, retries);
        await using var verify = await setup.CreateDbContextAsync();
        var state = await verify.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
        Assert.Equal(4, state.WakeGeneration);
        Assert.Equal(0, state.PendingWakeReasons);
        Assert.Equal(result.Snapshot.ConsumptionOperationId, state.InFlightWakeOperationId);
        var receipt = Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(candidate => candidate.OperationKind == "wake-consumption")
            .ToListAsync());
        Assert.True(receipt.Accepted);
        Assert.Equal((int)expectedReasons, receipt.WakeReasons);

        var restartedSnapshot = await new SqlGpuSchedulerStore(setup)
            .ReadWakeStateAsync(CancellationToken.None);
        Assert.Equal(result.Snapshot, restartedSnapshot);

        var acknowledgementOperationId = Guid.NewGuid();
        Assert.True(await store.AcknowledgeWakeAsync(
            acknowledgementOperationId,
            result.Snapshot.ConsumptionOperationId!.Value,
            CancellationToken.None));
        Assert.True(await store.AcknowledgeWakeAsync(
            acknowledgementOperationId,
            result.Snapshot.ConsumptionOperationId!.Value,
            CancellationToken.None));
        await using var acknowledged = await setup.CreateDbContextAsync();
        Assert.Equal(0, await acknowledged.GpuSchedulerStates.Select(candidate => candidate.PendingWakeReasons).SingleAsync());
        Assert.Single(await acknowledged.GpuSchedulerOperationReceipts
            .Where(candidate => candidate.OperationKind == "wake-acknowledgement")
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Stale_wake_acknowledgement_cannot_clear_a_newer_capacity_released_reason()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var state = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 4;
            state.PendingWakeReasons = (int)GpuSchedulerWakeReason.WorkReady;
            await arrange.SaveChangesAsync();
        }

        var store = new SqlGpuSchedulerStore(factory);
        var consumed = await store.ConsumeWakeAsync(Guid.NewGuid(), 4, CancellationToken.None);
        Assert.True(consumed.Consumed);
        await using (var afterConsumption = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await afterConsumption.GpuSchedulerStates
                .Select(candidate => candidate.PendingWakeReasons)
                .SingleAsync());
        }
        await using (var publish = await factory.CreateDbContextAsync())
        {
            var state = await publish.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 5;
            state.PendingWakeReasons |= (int)GpuSchedulerWakeReason.CapacityReleased;
            await publish.SaveChangesAsync();
        }

        Assert.True(await store.AcknowledgeWakeAsync(
            Guid.NewGuid(),
            consumed.Snapshot.ConsumptionOperationId!.Value,
            CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuSchedulerWakeReason.CapacityReleased,
            await verify.GpuSchedulerStates.Select(candidate => candidate.PendingWakeReasons).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(GpuSchedulerWakeReason.WorkReady)]
    [InlineData(GpuSchedulerWakeReason.CapacityReleased)]
    public async Task Consumed_wake_acknowledgement_preserves_a_deferral_recorded_during_the_admission_attempt(
        GpuSchedulerWakeReason consumedReason)
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var state = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 4;
            state.PendingWakeReasons = (int)consumedReason;
            await arrange.SaveChangesAsync();
        }

        var store = new SqlGpuSchedulerStore(factory);
        var consumed = await store.ConsumeWakeAsync(Guid.NewGuid(), 4, CancellationToken.None);
        Assert.True(consumed.Consumed);
        await using (var defer = await factory.CreateDbContextAsync())
        {
            var state = await defer.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 5;
            state.PendingWakeReasons |= (int)GpuSchedulerWakeReason.DeferredRetry;
            state.NextDeferredAtUtc = DateTimeOffset.Parse("2026-07-29T11:00:00+00:00");
            await defer.SaveChangesAsync();
        }

        Assert.True(await store.AcknowledgeWakeAsync(
            Guid.NewGuid(),
            consumed.Snapshot.ConsumptionOperationId!.Value,
            CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuSchedulerWakeReason.DeferredRetry,
            await verify.GpuSchedulerStates.Select(candidate => candidate.PendingWakeReasons).SingleAsync());
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-29T11:00:00+00:00"),
            await verify.GpuSchedulerStates.Select(candidate => candidate.NextDeferredAtUtc).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Callback_receipt_canonicalises_equivalent_outcome_order()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var firstTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        var secondTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var callback = new GpuBatchCallback(
            batch.Id,
            "slot-a",
            "test-owner",
            batch.AdmissionGeneration,
            GpuBatchCallbackKind.Completed,
            [
                new GpuMiniTaskBoundaryOutcome(firstTaskId, GpuMiniTaskBoundaryDisposition.Completed),
                new GpuMiniTaskBoundaryOutcome(secondTaskId, GpuMiniTaskBoundaryDisposition.Completed)
            ],
            CapacityReleased: true);
        var operationId = Guid.NewGuid();
        var store = new SqlGpuSchedulerStore(factory);

        Assert.True((await store.ApplyBatchCallbackAsync(operationId, callback, CancellationToken.None)).Accepted);
        var replay = await store.ApplyBatchCallbackAsync(
            operationId,
            callback with
            {
                Outcomes =
                [
                    new GpuMiniTaskBoundaryOutcome(secondTaskId, GpuMiniTaskBoundaryDisposition.Completed),
                    new GpuMiniTaskBoundaryOutcome(firstTaskId, GpuMiniTaskBoundaryDisposition.Completed)
                ]
            },
            CancellationToken.None);

        Assert.True(replay.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationId == operationId)
            .ToListAsync());
        Assert.Equal(1, await verify.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Pre_fingerprint_wake_receipt_fails_closed_without_consuming_state()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var operationId = Guid.NewGuid();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var state = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 4;
            state.PendingWakeReasons = (int)GpuSchedulerWakeReason.WorkReady;
            arrange.GpuSchedulerOperationReceipts.Add(new GpuSchedulerOperationReceiptEntity
            {
                OperationId = operationId,
                OperationKind = "wake-consumption",
                Accepted = true,
                Committed = true,
                WakeReasons = (int)GpuSchedulerWakeReason.WorkReady,
                WakeGeneration = 4,
                CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00")
            });
            await arrange.SaveChangesAsync();
        }

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqlGpuSchedulerStore(factory).ConsumeWakeAsync(operationId, 4, CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(4, await verify.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
        Assert.Equal((int)GpuSchedulerWakeReason.WorkReady, await verify.GpuSchedulerStates.Select(state => state.PendingWakeReasons).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Callback_receipt_rejects_same_operation_id_with_different_immutable_input()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var operationId = Guid.NewGuid();
        var callback = new GpuBatchCallback(
            batch.Id,
            "slot-a",
            "test-owner",
            batch.AdmissionGeneration,
            GpuBatchCallbackKind.SafeBoundary,
            [],
            CapacityReleased: false);

        Assert.True((await store.ApplyBatchCallbackAsync(operationId, callback, CancellationToken.None)).Accepted);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ApplyBatchCallbackAsync(
                operationId,
                callback with { OwnerKey = "different-owner" },
                CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuBatchState.AtSafeBoundary,
            await verify.GpuBatches.Where(candidate => candidate.Id == batch.Id)
                .Select(candidate => candidate.State)
                .SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(candidate => candidate.OperationId == operationId)
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Capacity_reconciliation_receipt_rejects_same_operation_id_with_different_immutable_input()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, batch.Id),
            CancellationToken.None)).Committed);
        var operationId = Guid.NewGuid();
        var request = new GpuTrustedCapacityReconciliation(
            batch.Id,
            "slot-a",
            "test-owner",
            batch.AdmissionGeneration,
            SqlGpuSchedulerStore.TrustedCapacityReleaseEvidenceClass);

        Assert.True((await store.ReconcileCapacityAsync(operationId, request, CancellationToken.None)).Committed);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReconcileCapacityAsync(
                operationId,
                request with { OwnerKey = "different-owner" },
                CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuCapacitySlotState.Available,
            await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Task_outcome_reconciliation_receipt_rejects_same_operation_id_with_different_immutable_input()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.MarkCapacityUncertainAsync(
            Guid.NewGuid(),
            await ReadCapacityUncertaintyRequestAsync(factory, batch.Id),
            CancellationToken.None)).Committed);
        var operationId = Guid.NewGuid();
        var request = new GpuTaskOutcomeReconciliation(
            batch.Id,
            "slot-a",
            "test-owner",
            batch.AdmissionGeneration,
            [taskId],
            SqlGpuSchedulerStore.TrustedOutcomeUncertainEvidenceClass);

        Assert.True((await store.ReconcileTaskOutcomeAsync(operationId, request, CancellationToken.None)).Committed);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReconcileTaskOutcomeAsync(
                operationId,
                request with { OwnerKey = "different-owner" },
                CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.OutcomeUncertain,
            await verify.GpuMiniTasks.Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Wake_consumption_receipt_rejects_same_operation_id_with_different_expected_generation()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var state = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 4;
            state.PendingWakeReasons = (int)GpuSchedulerWakeReason.WorkReady;
            await arrange.SaveChangesAsync();
        }

        var operationId = Guid.NewGuid();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.ConsumeWakeAsync(operationId, 4, CancellationToken.None)).Consumed);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ConsumeWakeAsync(operationId, 5, CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.GpuSchedulerStates.Select(state => state.PendingWakeReasons).SingleAsync());
        Assert.Equal(operationId, await verify.GpuSchedulerStates
            .Select(state => state.InFlightWakeOperationId)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_wake_consumer_retry_replays_its_original_in_flight_snapshot_after_acknowledgement_and_newer_wake()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var state = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 4;
            state.PendingWakeReasons = (int)GpuSchedulerWakeReason.WorkReady;
            await arrange.SaveChangesAsync();
        }

        var store = new SqlGpuSchedulerStore(factory);
        var firstOperationId = Guid.NewGuid();
        var secondOperationId = Guid.NewGuid();
        var first = await store.ConsumeWakeAsync(firstOperationId, 4, CancellationToken.None);
        var second = await store.ConsumeWakeAsync(secondOperationId, 4, CancellationToken.None);

        Assert.True(first.Consumed);
        Assert.True(second.Consumed);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.True(await store.AcknowledgeWakeAsync(
            Guid.NewGuid(),
            first.Snapshot.ConsumptionOperationId!.Value,
            CancellationToken.None));
        await using (var newerWake = await factory.CreateDbContextAsync())
        {
            var state = await newerWake.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            state.WakeGeneration = 5;
            state.PendingWakeReasons = (int)GpuSchedulerWakeReason.CapacityReleased;
            await newerWake.SaveChangesAsync();
        }

        var retry = await store.ConsumeWakeAsync(secondOperationId, 4, CancellationToken.None);

        Assert.Equal(second, retry);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationKind == "wake-consumption" && receipt.OperationId == secondOperationId)
            .ToListAsync());
        Assert.Equal((int)GpuSchedulerWakeReason.CapacityReleased, await verify.GpuSchedulerStates
            .Select(state => state.PendingWakeReasons)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Callback_retry_after_status_publication_failure_replays_the_same_committed_result_without_a_second_mutation()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var callback = new GpuBatchCallback(
            batch.Id,
            "slot-a",
            "test-owner",
            batch.AdmissionGeneration,
            GpuBatchCallbackKind.Completed,
            [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)],
            CapacityReleased: true);
        var operationId = Guid.NewGuid();
        var throwingCoordinator = CreateCoordinator(
            new SqlGpuSchedulerStore(factory),
            new ThrowingStatusPublisher(),
            new RecordingWakeSignal());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await throwingCoordinator.HandleCallbackAsync(operationId, callback, CancellationToken.None));

        var retry = await CreateCoordinator(
                new SqlGpuSchedulerStore(factory),
                new NullStatusPublisher(),
                new RecordingWakeSignal())
            .HandleCallbackAsync(operationId, callback, CancellationToken.None);

        Assert.True(retry.Accepted);
        Assert.True(retry.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Completed,
            await verify.GpuMiniTasks.Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
        Assert.Equal(
            (int)GpuCapacitySlotState.Available,
            await verify.GpuCapacitySlots.Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationId == operationId)
            .ToListAsync());
        Assert.Equal(1, await verify.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    private static async Task AssertActiveReservationAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid batchId,
        Guid taskId,
        long expectedWakeGeneration)
    {
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuBatchState.Active,
            await verification.GpuBatches
                .Where(candidate => candidate.Id == batchId)
                .Select(candidate => candidate.State)
                .SingleAsync());
        Assert.Equal(
            (int)GpuCapacitySlotState.Reserved,
            await verification.GpuCapacitySlots
                .Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Active,
            await verification.GpuMiniTasks
                .Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
        Assert.Equal(
            expectedWakeGeneration,
            await verification.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    private static async Task<GpuCapacityUncertaintyRequest> ReadCapacityUncertaintyRequestAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid expectedBatchId)
    {
        var request = Assert.Single(await new SqlGpuSchedulerStore(factory)
            .ReadStaleCapacityReservationsAsync(
                DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
                CancellationToken.None));
        Assert.Equal(expectedBatchId, request.BatchId);
        return request;
    }

    private static async Task AssertUncertainReservationAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid batchId,
        Guid taskId,
        long expectedWakeGeneration)
    {
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuBatchState.CapacityUncertain,
            await verification.GpuBatches
                .Where(candidate => candidate.Id == batchId)
                .Select(candidate => candidate.State)
                .SingleAsync());
        Assert.Equal(
            (int)GpuCapacitySlotState.Uncertain,
            await verification.GpuCapacitySlots
                .Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Active,
            await verification.GpuMiniTasks
                .Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
        Assert.Equal(
            expectedWakeGeneration,
            await verification.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    private sealed class SwappingReadOnlyList<T>(
        IReadOnlyList<T> initial,
        IReadOnlyList<T> replacement) : IReadOnlyList<T>
    {
        private IReadOnlyList<T> _current = initial;
        private bool _hasEnumerated;

        public int Count => _current.Count;

        public T this[int index] => _current[index];

        public IEnumerator<T> GetEnumerator()
        {
            var snapshot = _current;
            if (!_hasEnumerated)
            {
                _hasEnumerated = true;
                _current = replacement;
            }

            return snapshot.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RetryFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sqlServer => sqlServer.ExecutionStrategy(dependencies => new RetryStrategy(dependencies)))
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class RetryStrategy(ExecutionStrategyDependencies dependencies) : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is PostCommitTransientException;
    }

    private sealed class PostCommitTransientException : Exception;

    private static GpuSchedulerCoordinator CreateCoordinator(
        SqlGpuSchedulerStore store,
        IStatusEventPublisher publisher,
        IGpuSchedulerWakeSignal signal) =>
        new(
            store,
            new NoGpuAdmissionGate(),
            publisher,
            signal,
            new FixedTimeProvider(),
            GpuSchedulerOptions.Default);

    private sealed class ThrowingStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("test publication failure"));
    }

    private sealed class NullStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingWakeSignal : IGpuSchedulerWakeSignal
    {
        public void Notify(GpuSchedulerWakeReason reason)
        {
        }

        public ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult((GpuSchedulerWakeReason)0);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-29T10:00:00+00:00");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now = _now.Add(elapsed);
    }
}

internal static class SqlGpuBatchLifecycleTestStoreExtensions
{
    public static ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(
        this SqlGpuSchedulerStore store,
        GpuBatchCallback callback,
        CancellationToken cancellationToken) =>
        store.ApplyBatchCallbackAsync(Guid.NewGuid(), callback, cancellationToken);

    public static ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(
        this SqlGpuSchedulerStore store,
        GpuTrustedCapacityReconciliation request,
        CancellationToken cancellationToken) =>
        store.ReconcileCapacityAsync(Guid.NewGuid(), request, cancellationToken);

    public static ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(
        this SqlGpuSchedulerStore store,
        GpuTaskOutcomeReconciliation request,
        CancellationToken cancellationToken) =>
        store.ReconcileTaskOutcomeAsync(Guid.NewGuid(), request, cancellationToken);

    public static ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(
        this SqlGpuSchedulerStore store,
        long expectedGeneration,
        CancellationToken cancellationToken) =>
        store.ConsumeWakeAsync(Guid.NewGuid(), expectedGeneration, cancellationToken);
}
