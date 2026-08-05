using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Gpu;

public sealed class GpuExecutorContractTests
{
    [Fact]
    public void Executor_evidence_enums_have_only_approved_members()
    {
        Assert.Equal(
            ["PendingDelivery", "Acknowledged", "ReceiptRecorded", "DeliveryUncertain", "Terminal"],
            Enum.GetNames<GpuExecutorDispatchState>());
        Assert.Equal(
            ["CapacityReleaseConfirmed", "TaskOutcomeConfirmed", "TaskOutcomeUncertainConfirmed"],
            Enum.GetNames<GpuExecutorEvidenceClass>());
    }

    [Theory]
    [MemberData(nameof(InvalidHandles))]
    public void Handle_rejects_an_invalid_opaque_fence(GpuExecutorBatchHandle handle) =>
        Assert.ThrowsAny<ArgumentException>(handle.Validate);

    [Fact]
    public void Receipt_rejects_a_digest_that_is_not_exactly_32_bytes()
    {
        var receipt = new GpuExecutorResultReceipt(
            Guid.NewGuid(),
            CreateHandle(),
            Guid.NewGuid(),
            GpuMiniTaskBoundaryDisposition.Completed,
            new byte[31],
            GpuExecutorEvidenceClass.TaskOutcomeConfirmed);

        Assert.Throws<ArgumentException>(receipt.Validate);
    }

    [Fact]
    public void Receipt_defensively_copies_its_opaque_digest()
    {
        var suppliedDigest = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var receipt = new GpuExecutorResultReceipt(
            Guid.NewGuid(),
            CreateHandle(),
            Guid.NewGuid(),
            GpuMiniTaskBoundaryDisposition.Completed,
            suppliedDigest,
            GpuExecutorEvidenceClass.TaskOutcomeConfirmed);

        suppliedDigest[0] = 99;
        var publishedDigest = receipt.OpaqueResultDigest!;
        publishedDigest[1] = 98;

        Assert.Equal(0, receipt.OpaqueResultDigest![0]);
        Assert.Equal(1, receipt.OpaqueResultDigest![1]);
    }

    [Theory]
    [InlineData(GpuMiniTaskBoundaryDisposition.Completed, GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed)]
    [InlineData(GpuMiniTaskBoundaryDisposition.OutcomeUncertain, GpuExecutorEvidenceClass.TaskOutcomeConfirmed)]
    [InlineData(GpuMiniTaskBoundaryDisposition.Completed, GpuExecutorEvidenceClass.CapacityReleaseConfirmed)]
    public void Receipt_rejects_evidence_that_does_not_match_its_task_outcome(
        GpuMiniTaskBoundaryDisposition disposition,
        GpuExecutorEvidenceClass evidenceClass)
    {
        var receipt = new GpuExecutorResultReceipt(
            Guid.NewGuid(),
            CreateHandle(),
            Guid.NewGuid(),
            disposition,
            null,
            evidenceClass);

        Assert.Throws<ArgumentException>(receipt.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("verifier ")]
    public void Trusted_evidence_rejects_a_noncanonical_verifier_key(string verifierKey)
    {
        var evidence = new GpuExecutorTrustedEvidence(
            Guid.NewGuid(),
            CreateHandle(),
            verifierKey,
            DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
            GpuExecutorEvidenceClass.CapacityReleaseConfirmed);

        Assert.Throws<ArgumentException>(evidence.Validate);
    }

    [Fact]
    public void Trusted_evidence_rejects_an_unknown_evidence_class()
    {
        var evidence = new GpuExecutorTrustedEvidence(
            Guid.NewGuid(),
            CreateHandle(),
            "verifier-a",
            DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
            (GpuExecutorEvidenceClass)99);

        Assert.Throws<ArgumentOutOfRangeException>(evidence.Validate);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("2026-08-05T08:00:00+02:00")]
    public void Trusted_evidence_rejects_non_utc_or_default_observation(string observedAtUtc)
    {
        var evidence = new GpuExecutorTrustedEvidence(
            Guid.NewGuid(),
            CreateHandle(),
            "verifier-a",
            DateTimeOffset.Parse(observedAtUtc),
            GpuExecutorEvidenceClass.CapacityReleaseConfirmed);

        Assert.Throws<ArgumentException>(evidence.Validate);
    }

    [Fact]
    public async Task Empty_operation_id_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.AcknowledgeAsync(
                new GpuExecutorAcknowledgement(Guid.Empty, CreateHandle()),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.AcknowledgementCount);
    }

    [Fact]
    public async Task Invalid_receipt_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.RecordReceiptAsync(
                new GpuExecutorResultReceipt(
                    Guid.NewGuid(),
                    CreateHandle(),
                    Guid.Empty,
                    GpuMiniTaskBoundaryDisposition.Completed,
                    null,
                    GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.ReceiptCount);
    }

    [Fact]
    public async Task Malformed_receipt_digest_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.RecordReceiptAsync(
                new GpuExecutorResultReceipt(
                    Guid.NewGuid(),
                    CreateHandle(),
                    Guid.NewGuid(),
                    GpuMiniTaskBoundaryDisposition.Completed,
                    new byte[31],
                    GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.ReceiptCount);
    }

    [Fact]
    public async Task Empty_receipt_operation_id_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.RecordReceiptAsync(
                new GpuExecutorResultReceipt(
                    Guid.Empty,
                    CreateHandle(),
                    Guid.NewGuid(),
                    GpuMiniTaskBoundaryDisposition.Completed,
                    null,
                    GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.ReceiptCount);
    }

    [Fact]
    public async Task Invalid_delivery_uncertainty_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.MarkDeliveryUncertainAsync(
                new GpuExecutorDeliveryUncertainty(Guid.Empty, CreateHandle()),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.DeliveryUncertaintyCount);
    }

    [Fact]
    public async Task Invalid_trusted_evidence_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.RecordTrustedEvidenceAsync(
                new GpuExecutorTrustedEvidence(
                    Guid.Empty,
                    CreateHandle(),
                    "verifier-a",
                    DateTimeOffset.Parse("2026-08-05T08:00:00+00:00"),
                    GpuExecutorEvidenceClass.CapacityReleaseConfirmed),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.EvidenceCount);
    }

    [Fact]
    public async Task Invalid_evidence_timestamp_is_rejected_before_the_dispatch_store_is_called()
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.RecordTrustedEvidenceAsync(
                new GpuExecutorTrustedEvidence(
                    Guid.NewGuid(),
                    CreateHandle(),
                    "verifier-a",
                    DateTimeOffset.Parse("2026-08-05T08:00:00+02:00"),
                    GpuExecutorEvidenceClass.CapacityReleaseConfirmed),
                CancellationToken.None));

        Assert.Equal(0, dispatchStore.EvidenceCount);
    }

    [Theory]
    [MemberData(nameof(InvalidHandles))]
    public async Task Invalid_handle_is_rejected_before_the_acknowledgement_store_is_called(GpuExecutorBatchHandle handle)
    {
        var dispatchStore = new RecordingDispatchStore();
        var sink = CreateSink(dispatchStore);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await sink.AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None));

        Assert.Equal(0, dispatchStore.AcknowledgementCount);
    }

    [Fact]
    public async Task Invalid_task_reconciliation_is_rejected_before_the_scheduler_store_is_called()
    {
        var schedulerStore = new RecordingSchedulerStore();
        var coordinator = new GpuSchedulerCoordinator(
            schedulerStore,
            new BusyAdmissionGate(),
            new NoopPublisher(),
            new NoopWakeSignal(),
            TimeProvider.System,
            GpuSchedulerOptions.Default);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await coordinator.ReconcileTaskOutcomeAsync(
                Guid.NewGuid(),
                new GpuTaskOutcomeReconciliation(CreateHandle(), Guid.Empty, Guid.NewGuid()),
                CancellationToken.None));

        Assert.Equal(0, schedulerStore.TaskReconciliationCount);
    }

    [Fact]
    public async Task Invalid_callback_is_rejected_before_the_scheduler_store_is_called()
    {
        var schedulerStore = new RecordingSchedulerStore();
        var sink = CreateSink(new RecordingDispatchStore(), schedulerStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sink.HandleCallbackAsync(
                Guid.NewGuid(),
                new GpuBatchCallback(
                    CreateHandle() with { ExecutorKey = "executor-a " },
                    GpuBatchCallbackKind.Completed,
                    [new GpuMiniTaskBoundaryOutcome(Guid.NewGuid(), GpuMiniTaskBoundaryDisposition.Completed)],
                    true),
                CancellationToken.None));

        Assert.Equal(0, schedulerStore.CallbackCount);
    }

    [Fact]
    public void Admission_requires_an_executor_key_only_for_admitted_work()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit, "slot-a", "owner-a", null, ExecutorKey: null).Validate(GpuSchedulerOptions.Default));
        Assert.ThrowsAny<ArgumentException>(() => new GpuAdmissionDecision(
            GpuAdmissionDisposition.Busy, null, null, null, ExecutorKey: "executor-a").Validate(GpuSchedulerOptions.Default));

        var admitted = new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit, "slot-a", "owner-a", null, ExecutorKey: "executor-a")
            .Validate(GpuSchedulerOptions.Default);

        Assert.Equal("executor-a", admitted.ExecutorKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("executor-a ")]
    public void Admission_rejects_a_missing_or_noncanonical_executor_key(string executorKey)
    {
        Assert.ThrowsAny<ArgumentException>(() => new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit,
            "slot-a",
            "owner-a",
            null,
            ExecutorKey: executorKey).Validate(GpuSchedulerOptions.Default));
    }

    [Fact]
    public void Admission_rejects_an_overlength_executor_key()
    {
        Assert.Throws<ArgumentException>(() => new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit,
            "slot-a",
            "owner-a",
            null,
            ExecutorKey: new string('e', GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength + 1))
            .Validate(GpuSchedulerOptions.Default));
    }

    [Theory]
    [MemberData(nameof(NonAdmitDecisionsWithSuppliedKeys))]
    public void Busy_and_defer_reject_any_supplied_fence_key(GpuAdmissionDecision decision)
    {
        Assert.Throws<ArgumentException>(() => decision.Validate(GpuSchedulerOptions.Default));
    }

    [Fact]
    public void Task_reconciliation_exposes_exactly_one_non_settable_task_id()
    {
        var taskId = Guid.NewGuid();
        var request = new GpuTaskOutcomeReconciliation(CreateHandle(), Guid.NewGuid(), taskId);

        Assert.Equal([taskId], request.MiniTaskIds);
        Assert.Null(typeof(GpuTaskOutcomeReconciliation)
            .GetProperty(nameof(GpuTaskOutcomeReconciliation.MiniTaskIds))!
            .SetMethod);
    }

    [Fact]
    public void Executor_handle_accepts_the_exact_fence_key_length_limit()
    {
        var key = new string('e', GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        var handle = CreateHandle() with { ExecutorKey = key };

        handle.Validate();
    }

    public static IEnumerable<object[]> InvalidHandles()
    {
        yield return [CreateHandle() with { BatchId = Guid.Empty }];
        yield return [CreateHandle() with { CapacitySlotKey = "" }];
        yield return [CreateHandle() with { CapacitySlotKey = "slot-a " }];
        yield return [CreateHandle() with { ExecutorKey = "" }];
        yield return [CreateHandle() with { ExecutorKey = " " }];
        yield return [CreateHandle() with { ExecutorKey = "executor-a " }];
        yield return [CreateHandle() with { AdmissionGeneration = 0 }];
        yield return [CreateHandle() with { AdmissionGeneration = -1 }];
        yield return [CreateHandle() with { DispatchId = Guid.Empty }];
        yield return [CreateHandle() with { ExecutorKey = new string('e', 257) }];
    }

    public static IEnumerable<object[]> NonAdmitDecisionsWithSuppliedKeys()
    {
        yield return [new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, "slot-a", null, null)];
        yield return [new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, "owner-a", null)];
        yield return [new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null, "executor-a")];
        yield return [new GpuAdmissionDecision(GpuAdmissionDisposition.Defer, "slot-a", null, TimeSpan.FromSeconds(1))];
        yield return [new GpuAdmissionDecision(GpuAdmissionDisposition.Defer, null, "owner-a", TimeSpan.FromSeconds(1))];
        yield return [new GpuAdmissionDecision(GpuAdmissionDisposition.Defer, null, null, TimeSpan.FromSeconds(1), "executor-a")];
    }

    private static GpuExecutorBatchHandle CreateHandle() =>
        new(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());

    private static GpuExecutorLifecycleCoordinator CreateSink(
        RecordingDispatchStore dispatchStore,
        RecordingSchedulerStore? schedulerStore = null) =>
        new(
            dispatchStore,
            new GpuSchedulerCoordinator(
                schedulerStore ?? new RecordingSchedulerStore(),
                new BusyAdmissionGate(),
                new NoopPublisher(),
                new NoopWakeSignal(),
                TimeProvider.System,
                GpuSchedulerOptions.Default));

    private sealed class RecordingDispatchStore : IGpuExecutorDispatchStore
    {
        public int AcknowledgementCount { get; private set; }
        public int DeliveryUncertaintyCount { get; private set; }
        public int ReceiptCount { get; private set; }
        public int EvidenceCount { get; private set; }

        public ValueTask<IReadOnlyList<GpuExecutorBatchHandle>> ReadPendingDispatchesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<GpuExecutorBatchHandle>>([]);

        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(
            GpuExecutorAcknowledgement acknowledgement,
            CancellationToken cancellationToken)
        {
            AcknowledgementCount++;
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(false, false));
        }

        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(
            GpuExecutorDeliveryUncertainty uncertainty,
            CancellationToken cancellationToken)
        {
            DeliveryUncertaintyCount++;
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(false, false));
        }

        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(
            GpuExecutorResultReceipt receipt,
            CancellationToken cancellationToken)
        {
            ReceiptCount++;
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(false, false));
        }

        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(
            GpuExecutorTrustedEvidence evidence,
            CancellationToken cancellationToken)
        {
            EvidenceCount++;
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(false, false));
        }
    }

    private sealed class RecordingSchedulerStore : IGpuSchedulerStore
    {
        public int CallbackCount { get; private set; }
        public int TaskReconciliationCount { get; private set; }

        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(Guid operationId, GpuSchedulerWakeReason wakeReason, GpuSchedulerOptions options, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.FromResult(new GpuBatchCallbackResult(false, false));
        }

        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid operationId, GpuCapacityUncertaintyRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid operationId, GpuTrustedCapacityReconciliation request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid operationId, GpuTaskOutcomeReconciliation request, CancellationToken cancellationToken)
        {
            TaskReconciliationCount++;
            return ValueTask.FromResult(new GpuTrustedReconciliationResult(false));
        }

        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset heartbeatNotAfterUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operationId, long expectedGeneration, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> AcknowledgeWakeAsync(Guid operationId, Guid consumptionOperationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BusyAdmissionGate : IGpuAdmissionGate
    {
        public ValueTask<GpuAdmissionDecision> DecideAsync(GpuBatchCandidate candidate, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null, null));
    }

    private sealed class NoopPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoopWakeSignal : IGpuSchedulerWakeSignal
    {
        public void Notify(GpuSchedulerWakeReason reason) { }

        public ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult((GpuSchedulerWakeReason)0);
    }
}
