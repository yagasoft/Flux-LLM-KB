using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Gpu;

public sealed class GpuSchedulerContractTests
{
    [Fact]
    public void Private_scheduler_enums_have_only_approved_members()
    {
        Assert.Equal(
            ["Ready", "Active", "Completed", "OutcomeUncertain"],
            Enum.GetNames<GpuMiniTaskExecutionState>());
        Assert.Equal(
            ["Active", "AtSafeBoundary", "Completed", "Released", "CapacityUncertain"],
            Enum.GetNames<GpuBatchState>());
        Assert.Equal(
            ["Available", "Reserved", "Uncertain"],
            Enum.GetNames<GpuCapacitySlotState>());
        Assert.Equal(
            ["WorkReady", "SafeBoundary", "CapacityReleased", "DeferredRetry", "StartupRecovery", "Reconciliation"],
            Enum.GetNames<GpuSchedulerWakeReason>());
        Assert.Equal(
            ["Completed", "OutcomeUncertain"],
            Enum.GetNames<GpuMiniTaskBoundaryDisposition>());
        Assert.Equal(
            ["Admit", "Busy", "Defer"],
            Enum.GetNames<GpuAdmissionDisposition>());
        Assert.Equal(
            ["SafeBoundary", "Completed", "CapacityReleased"],
            Enum.GetNames<GpuBatchCallbackKind>());
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 1, 0)]
    [InlineData(-1, 1, 1, 1, 1)]
    [InlineData(1, -1, 1, 1, 1)]
    [InlineData(1, 1, -1, 1, 1)]
    [InlineData(1, 1, 1, -1, 1)]
    [InlineData(1, 1, 1, 1, -1)]
    public void Options_reject_non_positive_bounds(
        int maxBatchItems,
        long maxBatchEstimatedBytes,
        int capacityDeferralCapSeconds,
        int fallbackIntervalSeconds,
        int unresponsiveDiagnosticAgeSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSchedulerOptions(
            maxBatchItems,
            maxBatchEstimatedBytes,
            TimeSpan.FromSeconds(capacityDeferralCapSeconds),
            TimeSpan.FromSeconds(fallbackIntervalSeconds),
            TimeSpan.FromSeconds(unresponsiveDiagnosticAgeSeconds)));
    }

    [Theory]
    [MemberData(nameof(InvalidCallbacks))]
    public async Task Invalid_callback_is_rejected_before_the_store_is_called(GpuBatchCallback callback)
    {
        var store = new RecordingStore();
        var coordinator = CreateCoordinator(store);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await coordinator.HandleCallbackAsync(callback, CancellationToken.None));

        Assert.Equal(0, store.CallbackCount);
    }

    [Fact]
    public async Task Empty_callback_operation_id_is_rejected_before_the_store_is_called()
    {
        var store = new RecordingStore();
        var coordinator = CreateCoordinator(store);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await coordinator.HandleCallbackAsync(
                Guid.Empty,
                CreateCallback(GpuBatchCallbackKind.SafeBoundary, false),
                CancellationToken.None));

        Assert.Equal(0, store.CallbackCount);
    }

    [Fact]
    public async Task Empty_uncertainty_operation_id_is_rejected_before_the_store_is_called()
    {
        var store = new RecordingStore();
        var coordinator = CreateCoordinator(store);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await coordinator.MarkCapacityUncertainAsync(
                Guid.Empty,
                new GpuCapacityUncertaintyRequest(
                    Guid.NewGuid(),
                    "slot-a",
                    "owner-a",
                    1,
                    DateTimeOffset.Parse("2026-07-28T08:00:00+00:00"),
                    new byte[8]),
                CancellationToken.None));

        Assert.Equal(0, store.UncertaintyCount);
    }

    [Fact]
    public async Task Committed_handoff_publishes_and_signals_work_ready_after_commit()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            HandoffResult = new GpuMiniTaskHandoffResult(Guid.NewGuid(), false, true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events));

        var result = await coordinator.HandoffAsync(CreateHandoffRequest(), CancellationToken.None);

        Assert.False(result.IsIdempotentReplay);
        Assert.Equal(["store", "status", "wake:WorkReady"], events);
    }

    [Fact]
    public async Task Committed_handoff_signals_work_ready_when_status_publication_fails()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            HandoffResult = new GpuMiniTaskHandoffResult(Guid.NewGuid(), false, true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new ThrowingPublisher(events),
            new RecordingWakeSignal(events));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.HandoffAsync(CreateHandoffRequest(), CancellationToken.None));

        Assert.Equal(["store", "status", "wake:WorkReady"], events);
    }

    [Fact]
    public async Task Idempotent_handoff_replay_returns_original_result_without_a_second_signal()
    {
        var events = new List<string>();
        var originalTaskId = Guid.NewGuid();
        var store = new RecordingStore
        {
            HandoffResult = new GpuMiniTaskHandoffResult(originalTaskId, true, false),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events));

        var result = await coordinator.HandoffAsync(CreateHandoffRequest(), CancellationToken.None);

        Assert.Equal(originalTaskId, result.MiniTaskId);
        Assert.True(result.IsIdempotentReplay);
        Assert.Equal(["store"], events);
    }

    [Fact]
    public async Task Uncommitted_handoff_does_not_publish_or_signal_work_ready()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            HandoffResult = new GpuMiniTaskHandoffResult(Guid.NewGuid(), false, false),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events));

        var result = await coordinator.HandoffAsync(CreateHandoffRequest(), CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(["store"], events);
    }

    [Fact]
    public async Task Failed_handoff_does_not_publish_or_signal_work_ready()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            HandoffException = new InvalidOperationException("test handoff failure"),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.HandoffAsync(CreateHandoffRequest(), CancellationToken.None));

        Assert.Equal(["store"], events);
    }

    [Fact]
    public async Task Busy_admission_leaves_work_ready_without_a_due_time()
    {
        var store = new RecordingStore
        {
            Candidate = CreateCandidate(),
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                false,
                GpuAdmissionDisposition.Busy,
                null)
        };
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.AdmitAsync(GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.Equal(GpuAdmissionDisposition.Busy, result.Disposition);
        Assert.Null(result.DeferredUntilUtc);
        Assert.Equal(GpuSchedulerWakeReason.WorkReady, store.LastWakeReason);
        Assert.Equal(GpuAdmissionDisposition.Busy, store.LastAdmissionDecision!.Disposition);
    }

    [Fact]
    public async Task Defer_admission_caps_the_retry_delay_before_persistence()
    {
        var store = new RecordingStore
        {
            Candidate = new GpuBatchCandidate(
                GpuPriorityLane.InteractiveRetrieval,
                "runtime",
                "settings",
                1,
                128)
        };
        var coordinator = CreateCoordinator(
            store,
            admissionGate: new FixedAdmissionGate(
                new GpuAdmissionDecision(
                    GpuAdmissionDisposition.Defer,
                    null,
                    null,
                    TimeSpan.FromMinutes(10))),
            options: new GpuSchedulerOptions(4, 1_024, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));

        await coordinator.AdmitAsync(GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), store.LastAdmissionDecision!.RetryAfter);
    }

    [Fact]
    public async Task Committed_deferral_publishes_then_signals_deferred_retry()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                true,
                GpuAdmissionDisposition.Defer,
                DateTimeOffset.Parse("2026-07-29T12:03:00+00:00")),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.Equal(["admission-store", "status", "wake:DeferredRetry"], events);
        Assert.Equal([GpuSchedulerWakeReason.DeferredRetry], signal.Reasons);
    }

    [Fact]
    public async Task Committed_deferral_signals_deferred_retry_when_status_publication_fails()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                true,
                GpuAdmissionDisposition.Defer,
                DateTimeOffset.Parse("2026-07-29T12:03:00+00:00")),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new ThrowingPublisher(events), signal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None));

        Assert.Equal(["admission-store", "status", "wake:DeferredRetry"], events);
        Assert.Equal([GpuSchedulerWakeReason.DeferredRetry], signal.Reasons);
    }

    [Fact]
    public async Task Uncommitted_deferral_does_not_publish_or_signal_deferred_retry()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                false,
                GpuAdmissionDisposition.Defer,
                DateTimeOffset.Parse("2026-07-29T12:03:00+00:00")),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.Equal(["admission-store"], events);
        Assert.Empty(signal.Reasons);
    }

    [Fact]
    public async Task Committed_busy_mutation_publishes_then_signals_the_observed_wake()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                true,
                GpuAdmissionDisposition.Busy,
                null),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.CapacityReleased, CancellationToken.None);

        Assert.Equal(["admission-store", "status", "wake:CapacityReleased"], events);
        Assert.Equal([GpuSchedulerWakeReason.CapacityReleased], signal.Reasons);
    }

    [Fact]
    public async Task Committed_admission_publishes_status_before_prompting_executor_dispatch()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(true, GpuAdmissionDisposition.Admit, null),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events),
            dispatchSignal: new RecordingExecutorDispatchSignal(events));

        var result = await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(["admission-store", "status", "executor-dispatch"], events);
    }

    [Fact]
    public async Task Replayed_committed_admission_publishes_status_without_a_second_executor_dispatch_prompt()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(true, GpuAdmissionDisposition.Admit, null),
            MarkRepeatedAdmissionAsReplay = true,
            EventLog = events
        };
        var dispatchSignal = new RecordingExecutorDispatchSignal(events);
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events),
            dispatchSignal: dispatchSignal);
        var operationId = Guid.NewGuid();

        await coordinator.AdmitAsync(operationId, GpuSchedulerWakeReason.WorkReady, CancellationToken.None);
        await coordinator.AdmitAsync(operationId, GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.Equal(1, dispatchSignal.Count);
        Assert.Equal(
            ["admission-store", "status", "executor-dispatch", "admission-store", "status"],
            events);
    }

    [Theory]
    [InlineData(GpuAdmissionDisposition.Busy)]
    [InlineData(GpuAdmissionDisposition.Defer)]
    public async Task Non_admit_result_does_not_prompt_executor_dispatch(GpuAdmissionDisposition disposition)
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                true,
                disposition,
                disposition == GpuAdmissionDisposition.Defer
                    ? DateTimeOffset.Parse("2026-07-29T12:03:00+00:00")
                    : null),
            EventLog = events
        };
        var dispatchSignal = new RecordingExecutorDispatchSignal(events);
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events),
            dispatchSignal: dispatchSignal);

        await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.Equal(0, dispatchSignal.Count);
    }

    [Fact]
    public async Task Dispatch_prompt_failure_does_not_alter_a_committed_admission_result()
    {
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(true, GpuAdmissionDisposition.Admit, null)
        };
        var coordinator = CreateCoordinator(
            store,
            dispatchSignal: new ThrowingExecutorDispatchSignal());

        var result = await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(GpuAdmissionDisposition.Admit, result.Disposition);
    }

    [Fact]
    public async Task Invalid_admission_decision_does_not_prompt_executor_dispatch()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            Candidate = CreateCandidate(),
            EventLog = events
        };
        var dispatchSignal = new RecordingExecutorDispatchSignal(events);
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events),
            admissionGate: new FixedAdmissionGate(
                new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "slot-a", "owner-a", null)),
            dispatchSignal: dispatchSignal);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None));

        Assert.Equal(0, dispatchSignal.Count);
        Assert.Equal(["admission-store"], events);
    }

    [Fact]
    public async Task Mutation_free_busy_does_not_publish_or_signal_the_observed_wake()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            AdmissionResult = new GpuSchedulerAdmissionRoundResult(
                false,
                GpuAdmissionDisposition.Busy,
                null),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.CapacityReleased, CancellationToken.None);

        Assert.Equal(["admission-store"], events);
        Assert.Empty(signal.Reasons);
    }

    [Fact]
    public async Task Failed_admission_does_not_publish_or_signal_deferred_retry()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            AdmissionException = new InvalidOperationException("test admission failure"),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.AdmitAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, CancellationToken.None));

        Assert.Equal(["admission-store"], events);
        Assert.Empty(signal.Reasons);
    }

    [Fact]
    public async Task Capacity_released_wake_is_preserved_when_other_reasons_are_coalesced()
    {
        var store = new RecordingStore();
        var coordinator = CreateCoordinator(store);
        var reasons = GpuSchedulerWakeReason.WorkReady | GpuSchedulerWakeReason.CapacityReleased;

        await coordinator.AdmitAsync(reasons, CancellationToken.None);

        Assert.Equal(reasons, store.LastWakeReason);
        Assert.True(store.LastWakeReason!.Value.HasFlag(GpuSchedulerWakeReason.CapacityReleased));
    }

    [Fact]
    public async Task Committed_safe_boundary_without_release_signals_only_safe_boundary()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            CallbackResult = new GpuBatchCallbackResult(true, true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.HandleCallbackAsync(CreateCallback(GpuBatchCallbackKind.SafeBoundary, false), CancellationToken.None);

        Assert.Equal(["callback-store", "status", "wake:SafeBoundary"], events);
        Assert.Equal([GpuSchedulerWakeReason.SafeBoundary], signal.Reasons);
    }

    [Fact]
    public async Task Committed_release_callback_signals_only_capacity_released()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            CallbackResult = new GpuBatchCallbackResult(true, true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.HandleCallbackAsync(CreateCallback(GpuBatchCallbackKind.CapacityReleased, true), CancellationToken.None);

        Assert.Equal(["callback-store", "status", "wake:CapacityReleased"], events);
        Assert.Equal([GpuSchedulerWakeReason.CapacityReleased], signal.Reasons);
    }

    [Fact]
    public async Task Committed_safe_boundary_that_releases_capacity_signals_both_reasons()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            CallbackResult = new GpuBatchCallbackResult(true, true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await coordinator.HandleCallbackAsync(CreateCallback(GpuBatchCallbackKind.SafeBoundary, true), CancellationToken.None);

        Assert.Equal(["callback-store", "status", "wake:SafeBoundary, CapacityReleased"], events);
        Assert.Equal(
            [GpuSchedulerWakeReason.SafeBoundary | GpuSchedulerWakeReason.CapacityReleased],
            signal.Reasons);
    }

    [Fact]
    public async Task Committed_task_outcome_reconciliation_publishes_status_then_signals_reconciliation()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            OutcomeReconciliationResult = new GpuTrustedReconciliationResult(true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new RecordingPublisher(events),
            new RecordingWakeSignal(events));

        var result = await coordinator.ReconcileTaskOutcomeAsync(
            CreateTaskOutcomeReconciliation(),
            CancellationToken.None);

        Assert.True(result.Committed);
        Assert.Equal(["outcome-store", "status", "wake:Reconciliation"], events);
    }

    [Fact]
    public async Task Outcome_reconciliation_preserves_the_named_task_evidence_before_the_store_boundary()
    {
        var taskId = Guid.NewGuid();
        var store = new RecordingStore
        {
        };
        var coordinator = CreateCoordinator(store);

        await coordinator.ReconcileTaskOutcomeAsync(
            Guid.NewGuid(),
            new GpuTaskOutcomeReconciliation(
                CreateHandle(),
                Guid.NewGuid(),
                taskId),
            CancellationToken.None);

        Assert.Equal(taskId, store.LastOutcomeReconciliation!.MiniTaskId);
    }

    [Fact]
    public async Task Committed_task_outcome_reconciliation_signals_even_when_status_publication_fails()
    {
        var events = new List<string>();
        var store = new RecordingStore
        {
            OutcomeReconciliationResult = new GpuTrustedReconciliationResult(true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(
            store,
            new ThrowingPublisher(events),
            new RecordingWakeSignal(events));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.ReconcileTaskOutcomeAsync(
                CreateTaskOutcomeReconciliation(),
                CancellationToken.None));

        Assert.Equal(["outcome-store", "status", "wake:Reconciliation"], events);
    }

    [Fact]
    public async Task Completed_callback_without_explicit_release_is_rejected_before_any_durable_call()
    {
        var events = new List<string>();
        var signal = new RecordingWakeSignal(events);
        var store = new RecordingStore
        {
            CallbackResult = new GpuBatchCallbackResult(true, true),
            EventLog = events
        };
        var coordinator = CreateCoordinator(store, new RecordingPublisher(events), signal);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await coordinator.HandleCallbackAsync(CreateCallback(GpuBatchCallbackKind.Completed, false), CancellationToken.None));

        Assert.Empty(events);
        Assert.Empty(signal.Reasons);
    }

    [Fact]
    public void Coordinator_has_only_scheduler_and_executor_prompt_boundary_dependencies()
    {
        var dependencyTypes = typeof(GpuSchedulerCoordinator)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            [
                typeof(IGpuSchedulerStore),
                typeof(IGpuAdmissionGate),
                typeof(IStatusEventPublisher),
                typeof(IGpuSchedulerWakeSignal),
                typeof(TimeProvider),
                typeof(GpuSchedulerOptions),
                typeof(IGpuExecutorDispatchSignal)
            ],
            dependencyTypes);
    }

    private static GpuSchedulerCoordinator CreateCoordinator(
        RecordingStore store,
        IStatusEventPublisher? publisher = null,
        IGpuSchedulerWakeSignal? wakeSignal = null,
        IGpuAdmissionGate? admissionGate = null,
        GpuSchedulerOptions? options = null,
        IGpuExecutorDispatchSignal? dispatchSignal = null) =>
        new(
            store,
            admissionGate ?? new FixedAdmissionGate(
                new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null)),
            publisher ?? new RecordingPublisher([]),
            wakeSignal ?? new RecordingWakeSignal([]),
            new FixedTimeProvider(),
            options ?? GpuSchedulerOptions.Default,
            dispatchSignal);

    private static GpuMiniTaskHandoffRequest CreateHandoffRequest() => new(
        new ClaimedJob(
            JobId.New(),
            PipelineRecordId.New(),
            1,
            PipelineStage.Extract,
            "extract-utf8",
            PublicJobState.WorkerProcessing,
            DateTimeOffset.Parse("2026-07-28T08:00:00+00:00"),
            1,
            "worker-a",
            DateTimeOffset.Parse("2026-07-28T08:01:00+00:00"),
            1),
        GpuPriorityLane.InteractiveRetrieval,
        "runtime",
        "settings",
        128,
        "idempotency");

    public static IEnumerable<object[]> InvalidCallbacks()
    {
        var validOutcome = new GpuMiniTaskBoundaryOutcome(
            Guid.NewGuid(),
            GpuMiniTaskBoundaryDisposition.Completed);
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Handle = CreateHandle() with { BatchId = Guid.Empty } }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Handle = CreateHandle() with { CapacitySlotKey = "" } }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Handle = CreateHandle() with { ExecutorKey = " " } }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Handle = CreateHandle() with { AdmissionGeneration = 0 } }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Handle = CreateHandle() with { AdmissionGeneration = -1 } }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Kind = (GpuBatchCallbackKind)99 }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Outcomes = [] }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with
        {
            Outcomes = [new GpuMiniTaskBoundaryOutcome(Guid.Empty, GpuMiniTaskBoundaryDisposition.Completed)]
        }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with
        {
            Outcomes = [new GpuMiniTaskBoundaryOutcome(Guid.NewGuid(), (GpuMiniTaskBoundaryDisposition)99)]
        }];
        yield return [CreateCallback(GpuBatchCallbackKind.Completed, true) with { Outcomes = [validOutcome, validOutcome] }];
        yield return [CreateCallback(GpuBatchCallbackKind.SafeBoundary, false) with { Outcomes = [validOutcome] }];
    }

    private static GpuBatchCandidate CreateCandidate() => new(
        GpuPriorityLane.InteractiveRetrieval,
        "runtime",
        "settings",
        1,
        128);

    private static GpuBatchCallback CreateCallback(
        GpuBatchCallbackKind kind,
        bool capacityReleased) =>
        new(
            CreateHandle(),
            kind,
            kind == GpuBatchCallbackKind.SafeBoundary && !capacityReleased
                ? []
                : [new GpuMiniTaskBoundaryOutcome(
                    Guid.NewGuid(),
                    kind == GpuBatchCallbackKind.Completed
                        ? GpuMiniTaskBoundaryDisposition.Completed
                        : GpuMiniTaskBoundaryDisposition.OutcomeUncertain)],
            capacityReleased);

    private static GpuExecutorBatchHandle CreateHandle() =>
        new(Guid.NewGuid(), "slot-a", "executor-a", 1, Guid.NewGuid());

    private static GpuTaskOutcomeReconciliation CreateTaskOutcomeReconciliation() =>
        new(
            CreateHandle(),
            Guid.NewGuid(),
            Guid.NewGuid());

    private sealed class RecordingStore : IGpuSchedulerStore
    {
        public GpuMiniTaskHandoffResult HandoffResult { get; init; } = new(Guid.NewGuid(), false, true);
        public Exception? HandoffException { get; init; }
        public Exception? AdmissionException { get; init; }
        public GpuSchedulerAdmissionRoundResult AdmissionResult { get; init; } = new(false, GpuAdmissionDisposition.Busy, null);
        public bool MarkRepeatedAdmissionAsReplay { get; init; }
        public GpuBatchCandidate? Candidate { get; init; }
        public GpuBatchCallbackResult CallbackResult { get; init; } = new(false, false);
        public GpuTrustedReconciliationResult OutcomeReconciliationResult { get; init; } = new(false);
        public int CallbackCount { get; private set; }
        public int UncertaintyCount { get; private set; }
        public GpuSchedulerWakeReason? LastWakeReason { get; private set; }
        public GpuAdmissionDecision? LastAdmissionDecision { get; private set; }
        public GpuTaskOutcomeReconciliation? LastOutcomeReconciliation { get; private set; }
        private readonly HashSet<Guid> _admissionOperationIds = [];

        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(
            GpuMiniTaskHandoffRequest request,
            CancellationToken cancellationToken)
        {
            EventLog?.Add("store");
            if (HandoffException is not null)
            {
                return ValueTask.FromException<GpuMiniTaskHandoffResult>(HandoffException);
            }

            return ValueTask.FromResult(HandoffResult);
        }

        public async ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(
            Guid operationId,
            GpuSchedulerWakeReason wakeReason,
            GpuSchedulerOptions options,
            Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission,
            CancellationToken cancellationToken)
        {
            EventLog?.Add("admission-store");
            if (AdmissionException is not null)
            {
                throw AdmissionException;
            }

            LastWakeReason = wakeReason;
            if (Candidate is not null)
            {
                LastAdmissionDecision = await decideAdmission(Candidate, cancellationToken);
            }

            return MarkRepeatedAdmissionAsReplay && !_admissionOperationIds.Add(operationId)
                ? AdmissionResult with { IsIdempotentReplay = true }
                : AdmissionResult;
        }

        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(
            Guid operationId,
            GpuBatchCallback callback,
            CancellationToken cancellationToken)
        {
            CallbackCount++;
            EventLog?.Add("callback-store");
            return ValueTask.FromResult(CallbackResult);
        }

        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(
            Guid operationId,
            GpuCapacityUncertaintyRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecordUncertainty());

        private GpuDiagnosticTransitionResult RecordUncertainty()
        {
            UncertaintyCount++;
            return new GpuDiagnosticTransitionResult(false);
        }

        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(
            Guid operationId,
            GpuTrustedCapacityReconciliation request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuTrustedReconciliationResult(false));

        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(
            Guid operationId,
            GpuTaskOutcomeReconciliation request,
            CancellationToken cancellationToken)
        {
            EventLog?.Add("outcome-store");
            LastOutcomeReconciliation = request;
            return ValueTask.FromResult(OutcomeReconciliationResult);
        }

        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(
            DateTimeOffset heartbeatNotAfterUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<GpuCapacityUncertaintyRequest>>([]);

        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuSchedulerWakeSnapshot(0, 0, null));

        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operationId, long expectedGeneration, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuSchedulerWakeConsumption(true, new GpuSchedulerWakeSnapshot(expectedGeneration, 0, null)));

        public ValueTask<bool> AcknowledgeWakeAsync(Guid operationId, Guid consumptionOperationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(GpuSchedulerStatusSnapshot.Empty);

        public List<string>? EventLog { get; init; }
    }

    private sealed class FixedAdmissionGate(GpuAdmissionDecision decision) : IGpuAdmissionGate
    {
        public ValueTask<GpuAdmissionDecision> DecideAsync(
            GpuBatchCandidate candidate,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(decision);
    }

    private sealed class RecordingPublisher(List<string> events) : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            events.Add("status");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher(List<string> events) : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            events.Add("status");
            return ValueTask.FromException(new InvalidOperationException("test publication failure"));
        }
    }

    private sealed class RecordingWakeSignal(List<string> events) : IGpuSchedulerWakeSignal
    {
        public List<GpuSchedulerWakeReason> Reasons { get; } = [];

        public void Notify(GpuSchedulerWakeReason reason)
        {
            Reasons.Add(reason);
            events.Add($"wake:{reason}");
        }

        public ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult((GpuSchedulerWakeReason)0);
    }

    private sealed class RecordingExecutorDispatchSignal(List<string> events) : IGpuExecutorDispatchSignal
    {
        public int Count { get; private set; }

        public void Notify()
        {
            Count++;
            events.Add("executor-dispatch");
        }
    }

    private sealed class ThrowingExecutorDispatchSignal : IGpuExecutorDispatchSignal
    {
        public void Notify() => throw new InvalidOperationException("test dispatch signal failure");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-28T08:00:00+00:00");
    }
}

internal static class GpuSchedulerContractTestExtensions
{
    public static ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(
        this GpuSchedulerCoordinator coordinator,
        GpuBatchCallback callback,
        CancellationToken cancellationToken) =>
        coordinator.HandleCallbackAsync(Guid.NewGuid(), callback, cancellationToken);

    public static ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(
        this GpuSchedulerCoordinator coordinator,
        GpuTrustedCapacityReconciliation request,
        CancellationToken cancellationToken) =>
        coordinator.ReconcileCapacityAsync(Guid.NewGuid(), request, cancellationToken);

    public static ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(
        this GpuSchedulerCoordinator coordinator,
        GpuTaskOutcomeReconciliation request,
        CancellationToken cancellationToken) =>
        coordinator.ReconcileTaskOutcomeAsync(Guid.NewGuid(), request, cancellationToken);
}
