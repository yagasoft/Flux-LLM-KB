using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuAdmissionConcurrencyTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Application_lock_holds_the_second_admission_until_the_first_commits()
    {
        var selection = new SqlGpuAdmissionTests(_fixture);
        var factory = await selection.CreateEnvironmentAsync();
        var first = await selection.AddReadyAsync(factory, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var firstParentId = await ReadParentIdAsync(factory, first);
        var second = await selection.AddReadyForParentAsync(factory, firstParentId, GpuPriorityLane.InteractiveRetrieval, "r", "s", 10);
        var lower = await selection.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "r", "s", 10);
        var lowerParentId = await ReadParentIdAsync(factory, lower);
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAtLockAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHook = 0;
        Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decision = (candidate, _) =>
            ValueTask.FromResult(candidate.PriorityLane == GpuPriorityLane.InteractiveRetrieval
                ? new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "slot-a", "test-owner", null, "test-executor")
                : new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
        var store = new SqlGpuSchedulerStore(
            factory,
            timeProvider: TimeProvider.System,
            beforeAdmissionLockAttempt: _ =>
            {
                if (Interlocked.Increment(ref firstHook) == 2)
                {
                    secondAtLockAttempt.SetResult();
                }

                return ValueTask.CompletedTask;
            },
            afterAdmissionLockAcquired: async _ =>
            {
                if (firstHook == 1)
                {
                    lockHeld.SetResult();
                    await releaseFirst.Task;
                }
            });

        var firstAdmission = store.RunAdmissionRoundAsync(
            GpuSchedulerWakeReason.WorkReady,
            new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            decision,
            CancellationToken.None).AsTask();
        await lockHeld.Task;
        await using (var probeConnection = new SqlConnection(_fixture.ConnectionString))
        {
            await probeConnection.OpenAsync();
            await using var probe = new SqlCommand(
                "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'FluxKnowledge.GpuScheduler.Admission', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @result;",
                probeConnection);
            var probeResult = Convert.ToInt32(await probe.ExecuteScalarAsync());
            Assert.True(probeResult < 0);
        }

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAdmission = Task.Run(async () =>
        {
            secondStarted.SetResult();
            return await store.RunAdmissionRoundAsync(
                GpuSchedulerWakeReason.WorkReady,
                new GpuSchedulerOptions(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
                decision,
                CancellationToken.None);
        });
        await secondStarted.Task;
        await secondAtLockAttempt.Task;
        Assert.False(secondAdmission.IsCompleted);
        releaseFirst.SetResult();
        var results = await Task.WhenAll(firstAdmission, secondAdmission);

        Assert.Single(results, result => result.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        var batch = await verify.GpuBatches.SingleAsync();
        Assert.Equal(1, await verify.GpuCapacitySlots.CountAsync(slot => slot.ActiveBatchId != null && slot.State == (int)GpuCapacitySlotState.Reserved));
        var selected = await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .OrderBy(task => task.CreatedSequence).Select(task => new { task.Id, task.BatchId, task.AdmissionGeneration }).ToListAsync();
        Assert.Equal([first, second], selected.Select(task => task.Id).ToArray());
        Assert.All(selected, task => Assert.Equal(1, task.AdmissionGeneration));
        Assert.All(selected, task => Assert.Equal(batch.Id, task.BatchId));
        Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.GpuProcessing, await verify.Jobs.Where(job => job.Id == firstParentId).Select(job => job.PublicState).SingleAsync());
        Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.GpuQueued, await verify.Jobs.Where(job => job.Id == lowerParentId).Select(job => job.PublicState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_acknowledgements_have_one_effective_transition_without_task_or_capacity_mutation()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);

        var results = await RunTogetherAsync(
            () => store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None).AsTask(),
            () => store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None).AsTask());

        Assert.Single(results, result => result.Accepted);
        Assert.Single(results, result => !result.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
        Assert.Equal(2, await verify.GpuSchedulerOperationReceipts.CountAsync(value => value.OperationKind == "executor-acknowledgement"));
        Assert.Equal(0, await verify.GpuExecutorResultReceipts.CountAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal(1, await verify.GpuCapacitySlots.CountAsync(value => value.ActiveBatchId != null));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_delivery_uncertainty_has_one_effective_transition_and_no_replacement()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);

        var results = await RunTogetherAsync(
            () => store.MarkDeliveryUncertainAsync(new GpuExecutorDeliveryUncertainty(Guid.NewGuid(), handle), CancellationToken.None).AsTask(),
            () => store.MarkDeliveryUncertainAsync(new GpuExecutorDeliveryUncertainty(Guid.NewGuid(), handle), CancellationToken.None).AsTask());

        Assert.Single(results, result => result.Accepted);
        Assert.Single(results, result => !result.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.DeliveryUncertain, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
        Assert.Equal(2, await verify.GpuSchedulerOperationReceipts.CountAsync(value => value.OperationKind == "executor-delivery-uncertain"));
        Assert.Equal(0, await verify.GpuExecutorResultReceipts.CountAsync());
        Assert.Equal(0, await verify.GpuExecutorEvidence.CountAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal(1, await verify.GpuCapacitySlots.CountAsync(value => value.ActiveBatchId != null));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_result_receipts_create_one_immutable_receipt_without_task_or_capacity_mutation()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);

        var results = await RunTogetherAsync(
            () => store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None).AsTask(),
            () => store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None).AsTask());

        Assert.Single(results, result => result.Accepted);
        Assert.Single(results, result => !result.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.ReceiptRecorded, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
        Assert.Single(await verify.GpuExecutorResultReceipts.ToListAsync());
        Assert.Equal(2, await verify.GpuSchedulerOperationReceipts.CountAsync(value => value.OperationKind == "executor-result-receipt"));
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal(1, await verify.GpuCapacitySlots.CountAsync(value => value.ActiveBatchId != null));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_same_operation_acknowledgement_replays_without_a_second_transition()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var operationId = Guid.NewGuid();

        var results = await RunTogetherAsync(
            () => store.AcknowledgeAsync(new GpuExecutorAcknowledgement(operationId, handle), CancellationToken.None).AsTask(),
            () => store.AcknowledgeAsync(new GpuExecutorAcknowledgement(operationId, handle), CancellationToken.None).AsTask());

        Assert.All(results, result => Assert.True(result.Accepted));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(value => value.OperationId == operationId).ToListAsync());
        Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_same_operation_with_a_different_batch_fails_before_replacing_either_dispatch()
    {
        var first = await CreateAdmittedDispatchAsync();
        var admission = new SqlGpuAdmissionTests(_fixture);
        await using (var arrange = await first.Factory.CreateDbContextAsync())
        {
            arrange.GpuCapacitySlots.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.GpuCapacitySlotEntity
            {
                SlotKey = "slot-b",
                State = (int)GpuCapacitySlotState.Available,
                UpdatedAtUtc = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00")
            });
            await arrange.SaveChangesAsync();
        }
        await admission.AddReadyAsync(first.Factory, GpuPriorityLane.InteractiveRetrieval, "other-runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(first.Factory, SqlGpuAdmissionTests.Admit("slot-b"));
        await using var read = await first.Factory.CreateDbContextAsync();
        var secondDispatch = await read.GpuExecutorDispatches.SingleAsync(value => value.DispatchId != first.Handle.DispatchId);
        var secondHandle = new GpuExecutorBatchHandle(secondDispatch.BatchId, secondDispatch.CapacitySlotKey, secondDispatch.ExecutorKey, secondDispatch.AdmissionGeneration, secondDispatch.DispatchId);
        var store = new SqlGpuSchedulerStore(first.Factory);
        var operationId = Guid.NewGuid();

        var outcomes = await RunTogetherCapturingAsync(
            () => store.AcknowledgeAsync(new GpuExecutorAcknowledgement(operationId, first.Handle), CancellationToken.None).AsTask(),
            () => store.AcknowledgeAsync(new GpuExecutorAcknowledgement(operationId, secondHandle), CancellationToken.None).AsTask());

        Assert.Single(outcomes, outcome => outcome.Error is null && outcome.Result!.Accepted);
        var failure = Assert.Single(outcomes, outcome => outcome.Error is not null).Error!;
        Assert.IsType<InvalidOperationException>(failure);
        await using var verify = await first.Factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.GpuExecutorDispatches.CountAsync(value => value.State == (int)GpuExecutorDispatchState.Acknowledged));
        Assert.Equal(1, await verify.GpuExecutorDispatches.CountAsync(value => value.State == (int)GpuExecutorDispatchState.PendingDelivery));
        Assert.Single(await verify.GpuSchedulerOperationReceipts.Where(value => value.OperationId == operationId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Held_lifecycle_fence_fails_closed_without_receipt_or_state_change()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = new SqlCommand(
            "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000; SELECT @result;",
            connection,
            (SqlTransaction)transaction))
        {
            hold.Parameters.AddWithValue("@resource", $"FluxKnowledge.GpuScheduler.BatchLifecycle:{handle.BatchId:N}");
            Assert.True(Convert.ToInt32(await hold.ExecuteScalarAsync()) >= 0);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqlGpuSchedulerStore(factory).AcknowledgeAsync(
                new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None));

        Assert.Contains("lifecycle fence", failure.Message, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync();
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.PendingDelivery, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
        Assert.Empty(await verify.GpuSchedulerOperationReceipts.Where(value => value.OperationKind == "executor-acknowledgement").ToListAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Lifecycle_fence_for_one_batch_does_not_block_an_unrelated_batch()
    {
        var first = await CreateAdmittedDispatchAsync();
        var admission = new SqlGpuAdmissionTests(_fixture);
        await using (var arrange = await first.Factory.CreateDbContextAsync())
        {
            arrange.GpuCapacitySlots.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.GpuCapacitySlotEntity
            {
                SlotKey = "slot-b", State = (int)GpuCapacitySlotState.Available,
                UpdatedAtUtc = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00")
            });
            await arrange.SaveChangesAsync();
        }
        await admission.AddReadyAsync(first.Factory, GpuPriorityLane.InteractiveRetrieval, "other-runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(first.Factory, SqlGpuAdmissionTests.Admit("slot-b"));
        await using var read = await first.Factory.CreateDbContextAsync();
        var second = await read.GpuExecutorDispatches.SingleAsync(value => value.DispatchId != first.Handle.DispatchId);
        var secondHandle = new GpuExecutorBatchHandle(second.BatchId, second.CapacitySlotKey, second.ExecutorKey, second.AdmissionGeneration, second.DispatchId);
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = new SqlCommand(
            "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000; SELECT @result;",
            connection,
            (SqlTransaction)transaction))
        {
            hold.Parameters.AddWithValue("@resource", $"FluxKnowledge.GpuScheduler.BatchLifecycle:{first.Handle.BatchId:N}");
            Assert.True(Convert.ToInt32(await hold.ExecuteScalarAsync()) >= 0);
        }

        var result = await new SqlGpuSchedulerStore(first.Factory).AcknowledgeAsync(
            new GpuExecutorAcknowledgement(Guid.NewGuid(), secondHandle), CancellationToken.None);

        Assert.True(result.Accepted);
        await transaction.RollbackAsync();
    }

    [NativeSqlServerFact]
    public async Task Concurrent_receipt_and_completed_callback_have_only_legal_serial_outcomes()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        var callback = new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed,
            [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)], true);
        GpuExecutorDispatchMutationResult? receipt = null;
        FluxKnowledge.Application.Gpu.GpuBatchCallbackResult? completion = null;

        await RunTogetherAsync(
            async () => { receipt = await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None); return 0; },
            async () => { completion = await store.ApplyBatchCallbackAsync(Guid.NewGuid(), callback, CancellationToken.None); return 0; });

        Assert.True(receipt!.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuExecutorResultReceipts.ToListAsync());
        var terminal = await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync() == (int)GpuExecutorDispatchState.Terminal;
        Assert.Equal(terminal, completion!.Accepted);
        Assert.Equal(terminal ? (int)GpuMiniTaskExecutionState.Completed : (int)GpuMiniTaskExecutionState.Active,
            await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal(terminal ? (int)GpuCapacitySlotState.Available : (int)GpuCapacitySlotState.Reserved,
            await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_trusted_evidence_and_capacity_reconciliation_never_releases_without_a_prior_evidence_record()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var uncertainty = Assert.Single(await store.ReadStaleCapacityReservationsAsync(DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"), CancellationToken.None));
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), uncertainty, CancellationToken.None)).Committed);
        var evidenceOperationId = Guid.NewGuid();
        GpuExecutorDispatchMutationResult? evidence = null;
        GpuTrustedReconciliationResult? reconciliation = null;

        await RunTogetherAsync(
            async () =>
            {
                evidence = await store.RecordTrustedEvidenceAsync(new GpuExecutorTrustedEvidence(evidenceOperationId, handle, "test-verifier", DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"), GpuExecutorEvidenceClass.CapacityReleaseConfirmed), CancellationToken.None);
                return 0;
            },
            async () =>
            {
                reconciliation = await store.ReconcileCapacityAsync(Guid.NewGuid(), new GpuTrustedCapacityReconciliation(handle, evidenceOperationId), CancellationToken.None);
                return 0;
            });

        Assert.True(evidence!.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuExecutorEvidence.ToListAsync());
        var released = await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync() == (int)GpuCapacitySlotState.Available;
        Assert.Equal(released, reconciliation!.Committed);
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_callback_and_diagnostic_uncertainty_preserve_exactly_one_legal_serialized_lifecycle_without_requeue_or_result_replacement()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(
            new GpuExecutorAcknowledgement(Guid.NewGuid(), handle),
            CancellationToken.None)).Accepted);
        var uncertainty = Assert.Single(await store.ReadStaleCapacityReservationsAsync(DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"), CancellationToken.None));
        GpuBatchCallbackResult? callback = null;
        GpuDiagnosticTransitionResult? diagnostic = null;

        await RunTogetherAsync(
            async () =>
            {
                callback = await store.ApplyBatchCallbackAsync(
                    Guid.NewGuid(),
                    new GpuBatchCallback(handle, GpuBatchCallbackKind.SafeBoundary, [], CapacityReleased: false),
                    CancellationToken.None);
                return 0;
            },
            async () =>
            {
                diagnostic = await store.MarkCapacityUncertainAsync(Guid.NewGuid(), uncertainty, CancellationToken.None);
                return 0;
            });

        await using var verify = await factory.CreateDbContextAsync();
        switch (diagnostic!.Committed, callback!.Accepted, callback.Committed)
        {
            case (true, false, false):
                Assert.Equal((int)GpuCapacitySlotState.Uncertain, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
                Assert.Equal((int)GpuBatchState.CapacityUncertain, await verify.GpuBatches.Select(value => value.State).SingleAsync());
                Assert.Equal((int)GpuExecutorDispatchState.DeliveryUncertain, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
                break;

            case (false, true, true):
                Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
                Assert.Equal(handle.BatchId, await verify.GpuCapacitySlots.Select(value => value.ActiveBatchId).SingleAsync());
                Assert.Equal((int)GpuBatchState.AtSafeBoundary, await verify.GpuBatches.Select(value => value.State).SingleAsync());
                Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
                break;

            default:
                throw new Xunit.Sdk.XunitException(
                    $"Unexpected callback/diagnostic serialisation result: " +
                    $"diagnostic={diagnostic.Committed}, callbackAccepted={callback.Accepted}, callbackCommitted={callback.Committed}.");
        }

        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal(0, await verify.GpuExecutorResultReceipts.CountAsync());
        Assert.Equal(0, await verify.GpuExecutorEvidence.CountAsync());
        Assert.Equal(0, await verify.GpuMiniTasks.CountAsync(value => value.ExecutionState == (int)GpuMiniTaskExecutionState.Ready));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_capacity_and_outcome_reconciliation_apply_each_trusted_proof_without_duplicate_evidence_or_improper_release()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var uncertainty = Assert.Single(await store.ReadStaleCapacityReservationsAsync(DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"), CancellationToken.None));
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), uncertainty, CancellationToken.None)).Committed);
        var capacityEvidenceOperationId = Guid.NewGuid();
        var outcomeEvidenceOperationId = Guid.NewGuid();
        Assert.True((await store.RecordTrustedEvidenceAsync(new GpuExecutorTrustedEvidence(
            capacityEvidenceOperationId, handle, "test-verifier", DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
            GpuExecutorEvidenceClass.CapacityReleaseConfirmed), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordTrustedEvidenceAsync(new GpuExecutorTrustedEvidence(
            outcomeEvidenceOperationId, handle, "test-verifier", DateTimeOffset.Parse("2026-08-05T08:00:01+00:00"),
            GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed), CancellationToken.None)).Accepted);
        GpuTrustedReconciliationResult? capacity = null;
        GpuTrustedReconciliationResult? outcome = null;

        await RunTogetherAsync(
            async () =>
            {
                capacity = await store.ReconcileCapacityAsync(
                    Guid.NewGuid(), new GpuTrustedCapacityReconciliation(handle, capacityEvidenceOperationId), CancellationToken.None);
                return 0;
            },
            async () =>
            {
                outcome = await store.ReconcileTaskOutcomeAsync(
                    Guid.NewGuid(), new GpuTaskOutcomeReconciliation(handle, outcomeEvidenceOperationId, taskId), CancellationToken.None);
                return 0;
            });

        Assert.True(capacity!.Committed);
        Assert.True(outcome!.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
        Assert.Equal(2, await verify.GpuExecutorEvidence.CountAsync());
        Assert.Equal(0, await verify.GpuExecutorResultReceipts.CountAsync());
        var reasons = (GpuSchedulerWakeReason)await verify.GpuSchedulerStates.Select(value => value.PendingWakeReasons).SingleAsync();
        Assert.True(reasons.HasFlag(GpuSchedulerWakeReason.Reconciliation));
        Assert.True(reasons.HasFlag(GpuSchedulerWakeReason.CapacityReleased));
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, Guid TaskId, GpuExecutorBatchHandle Handle)> CreateAdmittedDispatchAsync()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var context = await factory.CreateDbContextAsync();
        var dispatch = await context.GpuExecutorDispatches.SingleAsync();
        return (factory, taskId, new GpuExecutorBatchHandle(
            dispatch.BatchId,
            dispatch.CapacitySlotKey,
            dispatch.ExecutorKey,
            dispatch.AdmissionGeneration,
            dispatch.DispatchId));
    }

    private static GpuExecutorResultReceipt CompletedReceipt(Guid operationId, GpuExecutorBatchHandle handle, Guid taskId) =>
        new(operationId, handle, taskId, GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed);

    private static async Task<IReadOnlyList<T>> RunTogetherAsync<T>(Func<Task<T>> firstOperation, Func<Task<T>> secondOperation)
    {
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            firstReady.SetResult();
            await release.Task;
            return await firstOperation();
        });
        var second = Task.Run(async () =>
        {
            secondReady.SetResult();
            await release.Task;
            return await secondOperation();
        });
        await Task.WhenAll(firstReady.Task, secondReady.Task);
        release.SetResult();
        return await Task.WhenAll(first, second);
    }

    private static async Task<IReadOnlyList<OperationOutcome<T>>> RunTogetherCapturingAsync<T>(Func<Task<T>> firstOperation, Func<Task<T>> secondOperation)
    {
        static async Task<OperationOutcome<T>> CaptureAsync(Func<Task<T>> operation)
        {
            try
            {
                return new OperationOutcome<T>(await operation(), null);
            }
            catch (Exception error)
            {
                return new OperationOutcome<T>(default, error);
            }
        }

        return await RunTogetherAsync(() => CaptureAsync(firstOperation), () => CaptureAsync(secondOperation));
    }

    private sealed record OperationOutcome<T>(T? Result, Exception? Error);

    private static async Task<Guid> ReadParentIdAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, Guid miniTaskId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await context.GpuMiniTasks.Where(task => task.Id == miniTaskId).Select(task => task.ParentJobId).SingleAsync();
    }
}
