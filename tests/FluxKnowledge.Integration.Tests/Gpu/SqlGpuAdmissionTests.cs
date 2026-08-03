using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuAdmissionTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;
    private static readonly GpuSchedulerOptions Options = new(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00");

    [NativeSqlServerFact]
    public async Task Production_retrying_execution_strategy_allows_admission()
    {
        var factory = await CreateEnvironmentAsync(useRetryingFactory: true);
        var taskId = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);

        var result = await AdmitAsync(factory, Admit("slot-a"));

        Assert.True(result.Committed);
        await using var verify = factory.CreateDbContext();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks
            .Where(task => task.Id == taskId).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Post_commit_transient_retry_returns_the_original_admitted_round_without_a_second_batch()
    {
        var setupFactory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(setupFactory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var factory = new FaultInjectingRetryDbContextFactory(_fixture.ConnectionString);
        var postCommitFailureCount = 0;
        var store = new SqlGpuSchedulerStore(
            factory,
            timeProvider: new FixedTimeProvider(Now),
            afterAdmissionCommitted: _ => Interlocked.Increment(ref postCommitFailureCount) == 1
                ? ValueTask.FromException(new PostCommitTransientException())
                : ValueTask.CompletedTask);

        var result = await store.RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady,
            Options,
            Admit("slot-a"),
            CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(GpuAdmissionDisposition.Admit, result.Disposition);
        Assert.Equal(1, postCommitFailureCount);
        await using var verify = factory.CreateDbContext();
        var batch = await verify.GpuBatches.SingleAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(task => task.Id == taskId);
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, task.ExecutionState);
        Assert.Equal(batch.Id, task.BatchId);
        Assert.Equal(1, task.AdmissionGeneration);
        Assert.Equal(1, await verify.GpuCapacitySlots.CountAsync(slot => slot.ActiveBatchId == batch.Id && slot.State == (int)GpuCapacitySlotState.Reserved));
        Assert.Equal(1, await verify.Jobs.CountAsync(job => job.PublicState == (int)PublicJobState.GpuProcessing));
    }

    [NativeSqlServerFact]
    public async Task Post_commit_transient_retry_returns_the_original_deferral_without_extending_its_due_time()
    {
        var setupFactory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(setupFactory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var factory = new FaultInjectingRetryDbContextFactory(_fixture.ConnectionString);
        var postCommitFailureCount = 0;
        var decisionCount = 0;
        var store = new SqlGpuSchedulerStore(
            factory,
            timeProvider: new FixedTimeProvider(Now),
            afterAdmissionCommitted: _ => Interlocked.Increment(ref postCommitFailureCount) == 1
                ? ValueTask.FromException(new PostCommitTransientException())
                : ValueTask.CompletedTask);

        var result = await store.RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady,
            Options,
            (_, _) =>
            {
                Interlocked.Increment(ref decisionCount);
                return ValueTask.FromResult(new GpuAdmissionDecision(
                    GpuAdmissionDisposition.Defer,
                    null,
                    null,
                    TimeSpan.FromMinutes(3)));
            },
            CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(GpuAdmissionDisposition.Defer, result.Disposition);
        Assert.Equal(Now.AddMinutes(3), result.DeferredUntilUtc);
        Assert.Equal(1, postCommitFailureCount);
        Assert.Equal(1, decisionCount);
        await using var verify = factory.CreateDbContext();
        var task = await verify.GpuMiniTasks.SingleAsync(candidate => candidate.Id == taskId);
        Assert.Equal(1, task.ReservationAttemptCount);
        Assert.Equal(Now.AddMinutes(3), task.DeferredUntilUtc);
        var wake = await verify.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
        Assert.Equal(GpuSchedulerWakeReason.DeferredRetry, (GpuSchedulerWakeReason)wake.PendingWakeReasons);
        Assert.Equal(Now.AddMinutes(3), wake.NextDeferredAtUtc);
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationKind == "admission")
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Post_commit_transient_retry_returns_the_original_busy_result_without_a_second_gate_decision()
    {
        var setupFactory = await CreateEnvironmentAsync();
        await AddReadyAsync(setupFactory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var factory = new FaultInjectingRetryDbContextFactory(_fixture.ConnectionString);
        var postCommitFailureCount = 0;
        var decisionCount = 0;
        var store = new SqlGpuSchedulerStore(
            factory,
            timeProvider: new FixedTimeProvider(Now),
            afterAdmissionCommitted: _ => Interlocked.Increment(ref postCommitFailureCount) == 1
                ? ValueTask.FromException(new PostCommitTransientException())
                : ValueTask.CompletedTask);

        var result = await store.RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady,
            Options,
            (_, _) =>
            {
                Interlocked.Increment(ref decisionCount);
                return ValueTask.FromResult(new GpuAdmissionDecision(
                    GpuAdmissionDisposition.Busy,
                    null,
                    null,
                    null));
            },
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        Assert.Null(result.DeferredUntilUtc);
        Assert.Equal(1, postCommitFailureCount);
        Assert.Equal(1, decisionCount);
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.GpuBatches.ToListAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationKind == "admission")
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Admission_receipt_rejects_operation_id_reuse_for_a_different_wake_reason()
    {
        var factory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "r",
            "s",
            10);
        var operationId = Guid.NewGuid();
        var decisionCount = 0;
        var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));

        var first = await store.RunAdmissionRoundAsync(
            operationId,
            GpuSchedulerWakeReason.WorkReady,
            Options,
            (_, _) =>
            {
                Interlocked.Increment(ref decisionCount);
                return ValueTask.FromResult(new GpuAdmissionDecision(
                    GpuAdmissionDisposition.Defer,
                    null,
                    null,
                    TimeSpan.FromMinutes(3)));
            },
            CancellationToken.None);

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RunAdmissionRoundAsync(
                operationId,
                GpuSchedulerWakeReason.CapacityReleased,
                Options,
                (_, _) =>
                {
                    Interlocked.Increment(ref decisionCount);
                    return ValueTask.FromResult(new GpuAdmissionDecision(
                        GpuAdmissionDisposition.Admit,
                        "slot-a",
                        "other-owner",
                        null));
                },
                CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(first.Committed);
        Assert.Equal(GpuAdmissionDisposition.Defer, first.Disposition);
        Assert.Equal(1, decisionCount);
        await using var verify = await factory.CreateDbContextAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(candidate => candidate.Id == taskId);
        Assert.Equal(1, task.ReservationAttemptCount);
        Assert.Equal(Now.AddMinutes(3), task.DeferredUntilUtc);
        Assert.Empty(await verify.GpuBatches.ToListAsync());
        var receipt = Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(candidate => candidate.OperationId == operationId)
            .ToListAsync());
        Assert.Equal((int)GpuSchedulerWakeReason.WorkReady, receipt.WakeReasons);
    }

    [NativeSqlServerFact]
    public async Task Admission_receipt_rejects_same_operation_id_when_any_scheduler_option_changes_before_the_gate()
    {
        var changedOptions = new[]
        {
            new GpuSchedulerOptions(2, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            new GpuSchedulerOptions(3, 101, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)),
            new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11))
        };

        foreach (var changed in changedOptions)
        {
            var factory = await CreateEnvironmentAsync();
            var taskId = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
            var operationId = Guid.NewGuid();
            var decisionCount = 0;
            var store = new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now));

            var first = await store.RunAdmissionRoundAsync(
                operationId,
                GpuSchedulerWakeReason.CapacityReleased,
                Options,
                (_, _) =>
                {
                    Interlocked.Increment(ref decisionCount);
                    return ValueTask.FromResult(new GpuAdmissionDecision(
                        GpuAdmissionDisposition.Defer,
                        null,
                        null,
                        TimeSpan.FromMinutes(3)));
                },
                CancellationToken.None);

            var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.RunAdmissionRoundAsync(
                    operationId,
                    GpuSchedulerWakeReason.CapacityReleased,
                    changed,
                    (_, _) =>
                    {
                        Interlocked.Increment(ref decisionCount);
                        return ValueTask.FromResult(new GpuAdmissionDecision(
                            GpuAdmissionDisposition.Admit,
                            "slot-a",
                            "other-owner",
                            null));
                    },
                    CancellationToken.None));

            Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(first.Committed);
            Assert.Equal(GpuAdmissionDisposition.Defer, first.Disposition);
            Assert.Equal(1, decisionCount);
            await using var verify = await factory.CreateDbContextAsync();
            var task = await verify.GpuMiniTasks.SingleAsync(candidate => candidate.Id == taskId);
            Assert.Equal(1, task.ReservationAttemptCount);
            Assert.Equal(Now.AddMinutes(3), task.DeferredUntilUtc);
            Assert.Single(await verify.GpuSchedulerOperationReceipts
                .Where(candidate => candidate.OperationId == operationId)
                .ToListAsync());
        }
    }

    [NativeSqlServerFact]
    public async Task Legacy_wake_only_admission_receipt_fails_closed_before_selection_or_mutation()
    {
        var factory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var operationId = Guid.NewGuid();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            arrange.GpuSchedulerOperationReceipts.Add(new GpuSchedulerOperationReceiptEntity
            {
                OperationId = operationId,
                OperationKind = "admission",
                RequestFingerprint = CreateLegacyAdmissionFingerprint(GpuSchedulerWakeReason.CapacityReleased),
                Accepted = true,
                Committed = true,
                WakeReasons = (int)GpuSchedulerWakeReason.CapacityReleased,
                AdmissionDisposition = (int)GpuAdmissionDisposition.Defer,
                DeferredUntilUtc = Now.AddMinutes(3),
                CreatedAtUtc = Now
            });
            await arrange.SaveChangesAsync();
        }

        var decisionCount = 0;
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now))
                .RunAdmissionRoundAsync(
                    operationId,
                    GpuSchedulerWakeReason.CapacityReleased,
                    Options,
                    (_, _) =>
                    {
                        Interlocked.Increment(ref decisionCount);
                        return ValueTask.FromResult(new GpuAdmissionDecision(
                            GpuAdmissionDisposition.Admit,
                            "slot-a",
                            "test-owner",
                            null));
                    },
                    CancellationToken.None));

        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, decisionCount);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Ready,
            await verify.GpuMiniTasks.Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
        Assert.Empty(await verify.GpuBatches.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Admission_orders_all_lanes_by_lane_then_created_sequence_without_lower_lane_bypass()
    {
        var factory = await CreateEnvironmentAsync();
        var first = await AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "r", "s", 10);
        var highest = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var later = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        await AddReadyAsync(factory, GpuPriorityLane.ImageOcr, "r", "s", 10);
        await AddReadyAsync(factory, GpuPriorityLane.ImageEnrichment, "r", "s", 10);
        await AddReadyAsync(factory, GpuPriorityLane.VideoOrUnknown, "r", "s", 10);

        var result = await AdmitAsync(factory, Admit("slot-a"));

        Assert.True(result.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        var active = await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .OrderBy(task => task.CreatedSequence).Select(task => task.Id).ToListAsync();
        Assert.Equal([highest, later], active);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == first).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Head_forms_one_bounded_batch_only_with_consecutive_same_lane_runtime_and_settings()
    {
        var factory = await CreateEnvironmentAsync();
        var first = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 20);
        var second = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 20);
        var incompatible = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "other", "s", 20);
        await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 20);

        await AdmitAsync(factory, Admit("slot-a"));

        await using var verify = await factory.CreateDbContextAsync();
        var active = await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .OrderBy(task => task.CreatedSequence).ThenBy(task => task.Id).Select(task => task.Id).ToListAsync();
        Assert.Equal([first, second], active);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == incompatible).Select(task => task.ExecutionState).SingleAsync());
        Assert.Equal(2, await verify.GpuMiniTasks.CountAsync(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready));
    }

    [NativeSqlServerFact]
    public async Task Admission_stops_at_the_item_limit()
    {
        var factory = await CreateEnvironmentAsync();
        var one = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var two = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var three = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);

        await AdmitAsync(factory, Admit("slot-a"), new GpuSchedulerOptions(2, 1_000, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));

        await using var verify = await factory.CreateDbContextAsync();
        var batch = await verify.GpuBatches.SingleAsync();
        Assert.Equal(2, batch.ItemCount);
        Assert.Equal(20, batch.EstimatedBytes);
        Assert.Equal([one, two], await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active).OrderBy(task => task.CreatedSequence).Select(task => task.Id).ToListAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == three).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Admission_stops_at_the_byte_limit()
    {
        var factory = await CreateEnvironmentAsync();
        var one = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 50);
        var two = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 50);
        var three = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 1);

        await AdmitAsync(factory, Admit("slot-a"), new GpuSchedulerOptions(10, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));

        await using var verify = await factory.CreateDbContextAsync();
        var batch = await verify.GpuBatches.SingleAsync();
        Assert.Equal(2, batch.ItemCount);
        Assert.Equal(100, batch.EstimatedBytes);
        Assert.Equal([one, two], await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active).OrderBy(task => task.CreatedSequence).Select(task => task.Id).ToListAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == three).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Admission_breaks_created_sequence_ties_by_id()
    {
        var factory = await CreateEnvironmentAsync();
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10, id: second, createdSequence: 500);
        await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10, id: first, createdSequence: 500);

        await AdmitAsync(factory, Admit("slot-a"), new GpuSchedulerOptions(1, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));

        await using var verify = await factory.CreateDbContextAsync();
        var active = await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .OrderBy(task => task.CreatedSequence).ThenBy(task => task.Id).Select(task => task.Id).ToListAsync();
        Assert.Equal([first], active);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, await verify.GpuMiniTasks.Where(task => task.Id == second).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Busy_keeps_ready_work_undeferred_and_creates_no_batch()
    {
        var factory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);

        var result = await AdmitAsync(factory, (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null)));

        Assert.False(result.Committed);
        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        await using var verify = await factory.CreateDbContextAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(task => task.Id == taskId);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, task.ExecutionState);
        Assert.Null(task.DeferredUntilUtc);
        Assert.Equal(0, task.ReservationAttemptCount);
        Assert.Empty(await verify.GpuBatches.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Defer_caps_delay_and_persists_the_next_durable_wake()
    {
        var factory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);

        var result = await AdmitAsync(factory, (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Defer, null, null, TimeSpan.FromHours(1))));

        Assert.True(result.Committed);
        Assert.Equal(Now.AddMinutes(5), result.DeferredUntilUtc);
        await using var verify = await factory.CreateDbContextAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(task => task.Id == taskId);
        Assert.Equal(Now.AddMinutes(5), task.DeferredUntilUtc);
        Assert.Equal(1, task.ReservationAttemptCount);
        Assert.Equal(Now.AddMinutes(5), (await verify.GpuSchedulerStates.SingleAsync(state => state.Id == 1)).NextDeferredAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Capacity_released_reconsiders_future_deferred_high_priority_work_before_lower_ready_work()
    {
        var factory = await CreateEnvironmentAsync();
        var deferred = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10, Now.AddMinutes(2));
        await AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "r", "s", 10);

        await AdmitAsync(factory, Admit("slot-a"), wakeReason: GpuSchedulerWakeReason.CapacityReleased);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(task => task.Id == deferred).Select(task => task.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Capacity_release_admission_clears_the_selected_deferral_and_recomputes_the_durable_due_time()
    {
        var factory = await CreateEnvironmentAsync();
        var deferredUntilUtc = Now.AddMinutes(2);
        var deferred = await AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "r",
            "s",
            10,
            deferredUntilUtc);
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var scheduler = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            scheduler.NextDeferredAtUtc = deferredUntilUtc;
            await arrange.SaveChangesAsync();
        }

        var result = await AdmitAsync(
            factory,
            Admit("slot-a"),
            wakeReason: GpuSchedulerWakeReason.CapacityReleased);

        Assert.True(result.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(candidate => candidate.Id == deferred);
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, task.ExecutionState);
        Assert.Null(task.DeferredUntilUtc);
        var wake = await verify.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
        Assert.Null(wake.NextDeferredAtUtc);
        Assert.Equal(0, wake.PendingWakeReasons);
    }

    [NativeSqlServerFact]
    public async Task Uncertain_slot_blocks_admission_without_touching_the_queue()
    {
        var factory = await CreateEnvironmentAsync(GpuCapacitySlotState.Uncertain);
        var taskId = await AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await AdmitAsync(factory, Admit("slot-a")));

        await using var verify = await factory.CreateDbContextAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(task => task.Id == taskId);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, task.ExecutionState);
        Assert.Null(task.DeferredUntilUtc);
        Assert.Empty(await verify.GpuBatches.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Case_only_slot_admission_fence_rejects_without_reserving_or_activating_work()
    {
        var factory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "runtime",
            "settings",
            10);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AdmitAsync(factory, Admit("SLOT-A")));

        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.GpuBatches.ToListAsync());
        Assert.Equal(
            (int)GpuCapacitySlotState.Available,
            await verification.GpuCapacitySlots
                .Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
        Assert.Null(await verification.GpuCapacitySlots
            .Where(slot => slot.SlotKey == "slot-a")
            .Select(slot => slot.ActiveBatchId)
            .SingleAsync());
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Ready,
            await verification.GpuMiniTasks
                .Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Trailing_whitespace_slot_and_owner_admission_fences_reject_without_reserving_or_activating_work()
    {
        await AssertTrailingFenceRejectedAsync(new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit,
            "slot-a ",
            "test-owner",
            null));
        await AssertTrailingFenceRejectedAsync(new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit,
            "slot-a",
            "test-owner ",
            null));
    }

    private async Task AssertTrailingFenceRejectedAsync(GpuAdmissionDecision decision)
    {
        var factory = await CreateEnvironmentAsync();
        var taskId = await AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "runtime",
            "settings",
            10);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await AdmitAsync(
                factory,
                (_, _) => ValueTask.FromResult(decision)));

        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.GpuBatches.ToListAsync());
        Assert.Equal(
            (int)GpuCapacitySlotState.Available,
            await verification.GpuCapacitySlots
                .Where(slot => slot.SlotKey == "slot-a")
                .Select(slot => slot.State)
                .SingleAsync());
        Assert.Null(await verification.GpuCapacitySlots
            .Where(slot => slot.SlotKey == "slot-a")
            .Select(slot => slot.ActiveBatchId)
            .SingleAsync());
        Assert.Equal(
            (int)GpuMiniTaskExecutionState.Ready,
            await verification.GpuMiniTasks
                .Where(task => task.Id == taskId)
                .Select(task => task.ExecutionState)
                .SingleAsync());
    }

    internal async Task<IDbContextFactory<FluxKnowledgeDbContext>> CreateEnvironmentAsync(GpuCapacitySlotState slotState = GpuCapacitySlotState.Available, bool useRetryingFactory = false)
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        IDbContextFactory<FluxKnowledgeDbContext> factory = useRetryingFactory
            ? new RetryingDbContextFactory(_fixture.ConnectionString)
            : SqlTestData.CreateFactory(_fixture);
        await using var context = await factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", State = (int)slotState, UpdatedAtUtc = Now });
        await context.SaveChangesAsync();
        return factory;
    }

    internal async Task<Guid> AddReadyAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, GpuPriorityLane lane, string runtime, string settings, long bytes, DateTimeOffset? deferredUntilUtc = null, Guid? id = null, long? createdSequence = null)
    {
        var seed = await SqlTestData.SeedWorkItemAsync(_fixture, Now, PublicJobState.GpuQueued, null);
        var task = new GpuMiniTaskEntity
        {
            Id = id ?? Guid.NewGuid(), ParentJobId = seed.JobId.Value, SourceRevision = 1, PriorityLane = (int)lane,
            ModelRuntimeKey = runtime, SettingsFingerprint = settings, EstimatedBytes = bytes,
            IdempotencyKey = $"task:{Guid.NewGuid():N}", ExecutionState = (int)GpuMiniTaskExecutionState.Ready,
            DeferredUntilUtc = deferredUntilUtc, CreatedAtUtc = Now
        };
        if (createdSequence is not null)
        {
            task.CreatedSequence = createdSequence.Value;
        }
        await using var context = await factory.CreateDbContextAsync();
        context.GpuMiniTasks.Add(task);
        await context.SaveChangesAsync();
        return task.Id;
    }

    internal async Task<Guid> AddReadyForParentAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, Guid parentJobId, GpuPriorityLane lane, string runtime, string settings, long bytes)
    {
        var task = new GpuMiniTaskEntity
        {
            Id = Guid.NewGuid(), ParentJobId = parentJobId, SourceRevision = 1, PriorityLane = (int)lane,
            ModelRuntimeKey = runtime, SettingsFingerprint = settings, EstimatedBytes = bytes,
            IdempotencyKey = $"task:{Guid.NewGuid():N}", ExecutionState = (int)GpuMiniTaskExecutionState.Ready,
            CreatedAtUtc = Now
        };
        await using var context = await factory.CreateDbContextAsync();
        context.GpuMiniTasks.Add(task);
        await context.SaveChangesAsync();
        return task.Id;
    }

    internal static Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> Admit(string slotKey) =>
        (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, slotKey, "test-owner", null));

    private static string CreateLegacyAdmissionFingerprint(GpuSchedulerWakeReason wakeReason)
    {
        var fields = new[]
        {
            "admission",
            ((int)wakeReason).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var canonical = string.Concat(fields.Select(field => string.Concat(
            field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            field,
            "|")));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    internal static async Task<GpuSchedulerAdmissionRoundResult> AdmitAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decision, GpuSchedulerOptions? options = null, GpuSchedulerWakeReason wakeReason = GpuSchedulerWakeReason.WorkReady) =>
        await new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(Now)).RunAdmissionRoundAsync(wakeReason, options ?? Options, decision, CancellationToken.None);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RetryingDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure())
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class FaultInjectingRetryDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sqlServer => sqlServer.ExecutionStrategy(dependencies => new FaultInjectingRetryExecutionStrategy(dependencies)))
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class FaultInjectingRetryExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is PostCommitTransientException;
    }

    private sealed class PostCommitTransientException : Exception;
}
