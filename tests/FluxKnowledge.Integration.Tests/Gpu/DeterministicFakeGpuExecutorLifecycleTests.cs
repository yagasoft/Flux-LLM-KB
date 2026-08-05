using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class DeterministicFakeGpuExecutorLifecycleTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Scripted_fake_replays_acknowledgement_and_rejects_duplicate_receipt_without_replacing_durable_state()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var acknowledgementOperationId = Guid.NewGuid();
        var receiptOperationId = Guid.NewGuid();
        var duplicateReceiptOperationId = Guid.NewGuid();
        var fake = new DeterministicFakeGpuExecutor(
            handle.ExecutorKey,
            CreateSink(factory),
            [
                new DeterministicFakeGpuExecutorAcknowledgementStep(new GpuExecutorAcknowledgement(acknowledgementOperationId, handle)),
                new DeterministicFakeGpuExecutorAcknowledgementStep(new GpuExecutorAcknowledgement(acknowledgementOperationId, handle)),
                new DeterministicFakeGpuExecutorReceiptStep(CompletedReceipt(receiptOperationId, handle, taskId)),
                new DeterministicFakeGpuExecutorReceiptStep(CompletedReceipt(duplicateReceiptOperationId, handle, taskId))
            ]);

        await fake.DeliverAsync(handle, CancellationToken.None);

        Assert.Equal([true, true, true, false], fake.ScriptResults.Select(result => result.Accepted));
        Assert.Equal(new LifecycleSnapshot(
            (int)GpuExecutorDispatchState.ReceiptRecorded,
            (int)GpuBatchState.Active,
            (int)GpuMiniTaskExecutionState.Active,
            (int)GpuCapacitySlotState.Reserved,
            (int)PublicJobState.GpuProcessing,
            1,
            0), await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Scripted_fake_rejects_mismatched_receipt_without_replacing_an_acknowledged_dispatch()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var fake = new DeterministicFakeGpuExecutor(
            handle.ExecutorKey,
            CreateSink(factory),
            [
                new DeterministicFakeGpuExecutorAcknowledgementStep(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle)),
                new DeterministicFakeGpuExecutorReceiptStep(CompletedReceipt(
                    Guid.NewGuid(),
                    handle with { ExecutorKey = "other-executor" },
                    taskId))
            ]);

        await fake.DeliverAsync(handle, CancellationToken.None);

        Assert.Equal([true, false], fake.ScriptResults.Select(result => result.Accepted));
        Assert.Equal(new LifecycleSnapshot(
            (int)GpuExecutorDispatchState.Acknowledged,
            (int)GpuBatchState.Active,
            (int)GpuMiniTaskExecutionState.Active,
            (int)GpuCapacitySlotState.Reserved,
            (int)PublicJobState.GpuProcessing,
            0,
            0), await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Scripted_fake_applies_safe_boundary_and_completion_then_rejects_late_receipt_without_capacity_replacement()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var fake = new DeterministicFakeGpuExecutor(
            handle.ExecutorKey,
            CreateSink(factory),
            [
                new DeterministicFakeGpuExecutorAcknowledgementStep(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle)),
                new DeterministicFakeGpuExecutorReceiptStep(CompletedReceipt(Guid.NewGuid(), handle, taskId)),
                new DeterministicFakeGpuExecutorCallbackStep(
                    Guid.NewGuid(),
                    new GpuBatchCallback(handle with { ExecutorKey = "other-executor" }, GpuBatchCallbackKind.SafeBoundary, [], false)),
                new DeterministicFakeGpuExecutorCallbackStep(
                    Guid.NewGuid(),
                    new GpuBatchCallback(handle, GpuBatchCallbackKind.SafeBoundary, [], false)),
                new DeterministicFakeGpuExecutorCallbackStep(
                    Guid.NewGuid(),
                    new GpuBatchCallback(
                        handle,
                        GpuBatchCallbackKind.Completed,
                        [new GpuMiniTaskBoundaryOutcome(taskId, GpuMiniTaskBoundaryDisposition.Completed)],
                        true)),
                new DeterministicFakeGpuExecutorCallbackStep(
                    Guid.NewGuid(),
                    new GpuBatchCallback(handle, GpuBatchCallbackKind.SafeBoundary, [], false)),
                new DeterministicFakeGpuExecutorReceiptStep(CompletedReceipt(Guid.NewGuid(), handle, taskId))
            ]);

        await fake.DeliverAsync(handle, CancellationToken.None);

        Assert.Equal([true, true, false, true, true, false, false], fake.ScriptResults.Select(result => result.Accepted));
        Assert.Equal(new LifecycleSnapshot(
            (int)GpuExecutorDispatchState.Terminal,
            (int)GpuBatchState.Completed,
            (int)GpuMiniTaskExecutionState.Completed,
            (int)GpuCapacitySlotState.Available,
            (int)PublicJobState.GpuProcessing,
            1,
            0), await ReadSnapshotAsync(factory, taskId));
    }

    [NativeSqlServerFact]
    public async Task Scripted_fake_replays_delivery_uncertainty_and_trusted_evidence_without_releasing_capacity()
    {
        var (factory, taskId, handle) = await CreateAdmittedDispatchAsync();
        var uncertaintyOperationId = Guid.NewGuid();
        var evidenceOperationId = Guid.NewGuid();
        var evidence = new GpuExecutorTrustedEvidence(
            evidenceOperationId,
            handle,
            "test-verifier",
            DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
            GpuExecutorEvidenceClass.CapacityReleaseConfirmed);
        var fake = new DeterministicFakeGpuExecutor(
            handle.ExecutorKey,
            CreateSink(factory),
            [
                new DeterministicFakeGpuExecutorDeliveryUncertaintyStep(new GpuExecutorDeliveryUncertainty(uncertaintyOperationId, handle)),
                new DeterministicFakeGpuExecutorDeliveryUncertaintyStep(new GpuExecutorDeliveryUncertainty(uncertaintyOperationId, handle)),
                new DeterministicFakeGpuExecutorTrustedEvidenceStep(evidence),
                new DeterministicFakeGpuExecutorTrustedEvidenceStep(evidence)
            ]);

        await fake.DeliverAsync(handle, CancellationToken.None);

        Assert.Equal([true, true, true, true], fake.ScriptResults.Select(result => result.Accepted));
        Assert.Equal(new LifecycleSnapshot(
            (int)GpuExecutorDispatchState.DeliveryUncertain,
            (int)GpuBatchState.Active,
            (int)GpuMiniTaskExecutionState.Active,
            (int)GpuCapacitySlotState.Reserved,
            (int)PublicJobState.GpuProcessing,
            0,
            1), await ReadSnapshotAsync(factory, taskId));
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

    private static IGpuExecutorLifecycleSink CreateSink(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var store = new SqlGpuSchedulerStore(factory);
        return new GpuExecutorLifecycleCoordinator(
            store,
            new GpuSchedulerCoordinator(
                store,
                new NoGpuAdmissionGate(),
                new NullStatusPublisher(),
                new ChannelGpuSchedulerWakeSignal(),
                TimeProvider.System,
                new GpuSchedulerOptions(1, 100, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))));
    }

    private static GpuExecutorResultReceipt CompletedReceipt(Guid operationId, GpuExecutorBatchHandle handle, Guid taskId) =>
        new(operationId, handle, taskId, GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed);

    private static async Task<LifecycleSnapshot> ReadSnapshotAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, Guid taskId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return new LifecycleSnapshot(
            await context.GpuExecutorDispatches.Select(value => value.State).SingleAsync(),
            await context.GpuBatches.Select(value => value.State).SingleAsync(),
            await context.GpuMiniTasks.Where(value => value.Id == taskId).Select(value => value.ExecutionState).SingleAsync(),
            await context.GpuCapacitySlots.Select(value => value.State).SingleAsync(),
            await context.Jobs.Where(value => value.Id == context.GpuMiniTasks.Where(task => task.Id == taskId).Select(task => task.ParentJobId).Single()).Select(value => value.PublicState).SingleAsync(),
            await context.GpuExecutorResultReceipts.CountAsync(),
            await context.GpuExecutorEvidence.CountAsync());
    }

    private sealed record LifecycleSnapshot(
        int DispatchState,
        int BatchState,
        int TaskState,
        int SlotState,
        int ParentJobState,
        int ReceiptCount,
        int EvidenceCount);

    private sealed class NullStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
