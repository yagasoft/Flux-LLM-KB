using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuExecutorDispatchTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Admission_atomically_creates_one_pending_fenced_dispatch()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);

        var result = await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));

        Assert.True(result.Committed);
        await using var verify = await factory.CreateDbContextAsync();
        var batch = await verify.GpuBatches.SingleAsync();
        var dispatch = await verify.GpuExecutorDispatches.SingleAsync();
        Assert.Equal(batch.Id, dispatch.BatchId);
        Assert.Equal(batch.Id, dispatch.DispatchId);
        Assert.Equal("slot-a", dispatch.CapacitySlotKey);
        Assert.Equal("test-executor", dispatch.ExecutorKey);
        Assert.Equal(batch.AdmissionGeneration, dispatch.AdmissionGeneration);
        Assert.Equal((int)GpuExecutorDispatchState.PendingDelivery, dispatch.State);
    }

    [NativeSqlServerFact]
    public async Task Acknowledgement_replay_is_idempotent_and_fingerprint_divergence_is_rejected()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var handle = new GpuExecutorBatchHandle(batch.Id, "slot-a", "test-executor", batch.AdmissionGeneration, batch.Id);
        var operationId = Guid.NewGuid();
        var store = new SqlGpuSchedulerStore(factory);

        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(operationId, handle), CancellationToken.None)).Accepted);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(operationId, handle), CancellationToken.None)).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AcknowledgeAsync(
                new GpuExecutorAcknowledgement(operationId, handle with { ExecutorKey = "other-executor" }),
                CancellationToken.None));

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verify.GpuExecutorDispatches
            .Select(dispatch => dispatch.State).SingleAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationId == operationId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Mid_transaction_admission_failure_rolls_back_batch_slot_task_parent_and_dispatch()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var setup = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(setup, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        var factory = new InterceptingDbContextFactory(_fixture.ConnectionString, new ThrowOnSecondSaveChangesInterceptor());
        var store = new SqlGpuSchedulerStore(factory);

        await Assert.ThrowsAsync<InjectedMidTransactionFailure>(async () =>
            await store.RunAdmissionRoundAsync(
                Guid.NewGuid(),
                GpuSchedulerWakeReason.WorkReady,
                Options,
                SqlGpuAdmissionTests.Admit("slot-a"),
                CancellationToken.None));

        await using var verify = await setup.CreateDbContextAsync();
        Assert.Empty(await verify.GpuBatches.ToListAsync());
        Assert.Empty(await verify.GpuExecutorDispatches.ToListAsync());
        var slot = await verify.GpuCapacitySlots.SingleAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, slot.State);
        Assert.Null(slot.ActiveBatchId);
        var task = await verify.GpuMiniTasks.SingleAsync(candidate => candidate.Id == taskId);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, task.ExecutionState);
        Assert.Null(task.BatchId);
        Assert.Equal((int)PublicJobState.GpuQueued, await verify.Jobs
            .Where(job => job.Id == task.ParentJobId).Select(job => job.PublicState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Admission_operation_replay_keeps_its_original_executor_and_one_dispatch()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        var operationId = Guid.NewGuid();
        var decisions = 0;
        var store = new SqlGpuSchedulerStore(factory);

        var first = await store.RunAdmissionRoundAsync(
            operationId, GpuSchedulerWakeReason.WorkReady, Options,
            (_, _) =>
            {
                Interlocked.Increment(ref decisions);
                return ValueTask.FromResult(new GpuAdmissionDecision(
                    GpuAdmissionDisposition.Admit, "slot-a", "test-owner", null, "executor-primary"));
            }, CancellationToken.None);
        var replay = await store.RunAdmissionRoundAsync(
            operationId, GpuSchedulerWakeReason.WorkReady, Options,
            (_, _) => throw new InvalidOperationException("A replay must not re-enter the admission gate."),
            CancellationToken.None);

        Assert.True(first.Committed);
        Assert.True(replay.Committed);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(1, decisions);
        await using var verify = await factory.CreateDbContextAsync();
        var dispatch = Assert.Single(await verify.GpuExecutorDispatches.ToListAsync());
        Assert.Equal("executor-primary", dispatch.ExecutorKey);
        Assert.Single(await verify.GpuBatches.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Rejected_result_receipts_leave_dispatch_task_slot_and_result_evidence_unchanged()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var before = await ReadSnapshotAsync(factory, taskId);
        var firstOperationId = Guid.NewGuid();

        var beforeAcknowledgement = await store.RecordReceiptAsync(
            CompletedReceipt(firstOperationId, handle, taskId), CancellationToken.None);
        var staleGeneration = await store.RecordReceiptAsync(
            CompletedReceipt(Guid.NewGuid(), handle with { AdmissionGeneration = handle.AdmissionGeneration + 1 }, taskId), CancellationToken.None);
        var wrongExecutor = await store.RecordReceiptAsync(
            CompletedReceipt(Guid.NewGuid(), handle with { ExecutorKey = "other-executor" }, taskId), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RecordReceiptAsync(
                CompletedReceipt(firstOperationId, handle with { ExecutorKey = "other-executor" }, taskId),
                CancellationToken.None));

        Assert.False(beforeAcknowledgement.Accepted);
        Assert.False(staleGeneration.Accepted);
        Assert.False(wrongExecutor.Accepted);
        Assert.Equal(before, await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Acknowledged_dispatch_rejects_stale_generation_and_mismatched_executor_receipts_without_mutation()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        var before = await ReadSnapshotAsync(factory, taskId);

        var staleGeneration = await store.RecordReceiptAsync(
            CompletedReceipt(Guid.NewGuid(), handle with { AdmissionGeneration = handle.AdmissionGeneration + 1 }, taskId),
            CancellationToken.None);
        var wrongExecutor = await store.RecordReceiptAsync(
            CompletedReceipt(Guid.NewGuid(), handle with { ExecutorKey = "other-executor" }, taskId),
            CancellationToken.None);

        Assert.False(staleGeneration.Accepted);
        Assert.False(wrongExecutor.Accepted);
        Assert.Equal(before, await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Terminal_dispatch_rejects_late_result_receipt_without_replacing_the_immutable_snapshot()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None)).Accepted);
        Assert.True((await store.ApplyBatchCallbackAsync(
            Guid.NewGuid(),
            new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed,
                [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)], true),
            CancellationToken.None)).Accepted);
        var before = await ReadSnapshotAsync(factory, taskId);

        var late = await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None);

        Assert.False(late.Accepted);
        Assert.Equal(before, await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Terminal_dispatch_rejects_new_trusted_evidence_without_replacing_the_preterminal_evidence_or_lifecycle_snapshot()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var preterminalEvidence = new GpuExecutorTrustedEvidence(
            Guid.NewGuid(),
            handle,
            "test-verifier",
            DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
            GpuExecutorEvidenceClass.TaskOutcomeConfirmed);
        Assert.True((await store.RecordTrustedEvidenceAsync(preterminalEvidence, CancellationToken.None)).Accepted);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None)).Accepted);
        Assert.True((await store.ApplyBatchCallbackAsync(
            Guid.NewGuid(),
            new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed,
                [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)], true),
            CancellationToken.None)).Accepted);
        var before = await ReadSnapshotAsync(factory, taskId);

        Assert.True((await store.RecordTrustedEvidenceAsync(preterminalEvidence, CancellationToken.None)).Accepted);
        Assert.Equal(before, await ReadSnapshotAsync(factory, taskId));

        var lateOperationId = Guid.NewGuid();
        var lateEvidence = preterminalEvidence with
        {
            OperationId = lateOperationId,
            EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed,
            ObservedAtUtc = preterminalEvidence.ObservedAtUtc.AddSeconds(1)
        };
        Assert.False((await store.RecordTrustedEvidenceAsync(lateEvidence, CancellationToken.None)).Accepted);
        Assert.False((await store.RecordTrustedEvidenceAsync(lateEvidence, CancellationToken.None)).Accepted);
        Assert.Equal(before, await ReadSnapshotAsync(factory, taskId));

        await using var verify = await factory.CreateDbContextAsync();
        var rejectionReceipt = await verify.GpuSchedulerOperationReceipts
            .SingleAsync(receipt => receipt.OperationId == lateOperationId);
        Assert.False(rejectionReceipt.Accepted);
        Assert.False(rejectionReceipt.Committed);
        Assert.Single(await verify.GpuExecutorEvidence.ToListAsync());
        Assert.Single(await verify.GpuExecutorResultReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Different_operation_duplicate_result_receipt_cannot_replace_the_first_immutable_receipt()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None)).Accepted);
        var afterFirst = await ReadSnapshotAsync(factory, taskId);

        var duplicate = await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, taskId), CancellationToken.None);

        Assert.False(duplicate.Accepted);
        Assert.Equal(afterFirst, await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Delivery_uncertainty_and_trusted_evidence_replay_or_reject_divergence_without_unrelated_mutation()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var deliveryOperationId = Guid.NewGuid();
        Assert.True((await store.MarkDeliveryUncertainAsync(new GpuExecutorDeliveryUncertainty(deliveryOperationId, handle), CancellationToken.None)).Accepted);
        Assert.True((await store.MarkDeliveryUncertainAsync(new GpuExecutorDeliveryUncertainty(deliveryOperationId, handle), CancellationToken.None)).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkDeliveryUncertainAsync(
                new GpuExecutorDeliveryUncertainty(deliveryOperationId, handle with { ExecutorKey = "other-executor" }),
                CancellationToken.None));
        var beforeEvidence = await ReadSnapshotAsync(factory, taskId);
        var evidenceOperationId = Guid.NewGuid();
        var evidence = new GpuExecutorTrustedEvidence(evidenceOperationId, handle, "test-verifier",
            DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"), GpuExecutorEvidenceClass.CapacityReleaseConfirmed);
        Assert.True((await store.RecordTrustedEvidenceAsync(evidence, CancellationToken.None)).Accepted);
        Assert.True((await store.RecordTrustedEvidenceAsync(evidence, CancellationToken.None)).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RecordTrustedEvidenceAsync(evidence with { EvidenceClass = GpuExecutorEvidenceClass.TaskOutcomeConfirmed }, CancellationToken.None));

        var afterEvidence = await ReadSnapshotAsync(factory, taskId);
        Assert.Equal(beforeEvidence with { EvidenceCount = beforeEvidence.EvidenceCount + 1 }, afterEvidence);
        Assert.Equal((int)GpuExecutorDispatchState.DeliveryUncertain, afterEvidence.DispatchState);
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, afterEvidence.TaskState);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, afterEvidence.SlotState);
    }

    [NativeSqlServerFact]
    public async Task Completed_callback_requires_each_durable_task_receipt_before_terminal_release()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var firstTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        var secondTaskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        var handle = await ReadHandleAsync(factory);
        var store = new SqlGpuSchedulerStore(factory);
        var callback = new GpuBatchCallback(handle, GpuBatchCallbackKind.Completed,
            [new GpuMiniTaskBoundaryOutcome(firstTaskId, GpuMiniTaskBoundaryDisposition.Completed), new GpuMiniTaskBoundaryOutcome(secondTaskId, GpuMiniTaskBoundaryDisposition.Completed)], true);
        var before = await ReadSnapshotAsync(factory, firstTaskId);

        Assert.False((await store.ApplyBatchCallbackAsync(Guid.NewGuid(), callback, CancellationToken.None)).Accepted);
        Assert.Equal(before, await ReadSnapshotAsync(factory, firstTaskId));
        Assert.True((await store.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, firstTaskId), CancellationToken.None)).Accepted);
        Assert.False((await store.ApplyBatchCallbackAsync(Guid.NewGuid(), callback, CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(CompletedReceipt(Guid.NewGuid(), handle, secondTaskId), CancellationToken.None)).Accepted);

        var completed = await store.ApplyBatchCallbackAsync(Guid.NewGuid(), callback, CancellationToken.None);
        Assert.True(completed.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.Terminal, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
        Assert.Equal(2, await verify.GpuMiniTasks.CountAsync(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Completed));
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Trusted_evidence_does_not_release_capacity_until_diagnostic_then_reconciliation()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var store = new SqlGpuSchedulerStore(factory);
        var evidenceOperationId = Guid.NewGuid();
        Assert.True((await store.RecordTrustedEvidenceAsync(new GpuExecutorTrustedEvidence(
            evidenceOperationId, handle, "test-verifier", DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
            GpuExecutorEvidenceClass.CapacityReleaseConfirmed), CancellationToken.None)).Accepted);
        var reserved = await ReadSnapshotAsync(factory, taskId);
        Assert.False((await store.ReconcileCapacityAsync(Guid.NewGuid(),
            new GpuTrustedCapacityReconciliation(handle, evidenceOperationId), CancellationToken.None)).Committed);
        Assert.Equal(reserved, await ReadSnapshotAsync(factory, taskId));
        var uncertain = Assert.Single(await store.ReadStaleCapacityReservationsAsync(DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"), CancellationToken.None));
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), uncertain, CancellationToken.None)).Committed);

        Assert.True((await store.ReconcileCapacityAsync(Guid.NewGuid(),
            new GpuTrustedCapacityReconciliation(handle, evidenceOperationId), CancellationToken.None)).Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync());
    }

    private static readonly GpuSchedulerOptions Options = new(3, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, Guid TaskId, GpuExecutorBatchHandle Handle)> CreateAdmittedDispatchAsync()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime", "settings", 10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        return (factory, taskId, await ReadHandleAsync(factory));
    }

    private static async Task<GpuExecutorBatchHandle> ReadHandleAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        var dispatch = await context.GpuExecutorDispatches.SingleAsync();
        return new GpuExecutorBatchHandle(dispatch.BatchId, dispatch.CapacitySlotKey, dispatch.ExecutorKey, dispatch.AdmissionGeneration, dispatch.DispatchId);
    }

    private static GpuExecutorResultReceipt CompletedReceipt(Guid operationId, GpuExecutorBatchHandle handle, Guid taskId) =>
        new(operationId, handle, taskId, GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed);

    private static async Task<DispatchSnapshot> ReadSnapshotAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, Guid taskId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return new DispatchSnapshot(
            await context.GpuExecutorDispatches.Select(value => value.State).SingleAsync(),
            await context.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync(),
            await context.GpuCapacitySlots.Select(value => value.State).SingleAsync(),
            await context.GpuExecutorResultReceipts.CountAsync(),
            await context.GpuExecutorEvidence.CountAsync());
    }

    private sealed record DispatchSnapshot(int DispatchState, int TaskState, int SlotState, int ResultReceiptCount, int EvidenceCount);

    private sealed class InterceptingDbContextFactory(string connectionString, IInterceptor interceptor) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class ThrowOnSecondSaveChangesInterceptor : SaveChangesInterceptor
    {
        private int _calls;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                throw new InjectedMidTransactionFailure();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedMidTransactionFailure : Exception;
}
