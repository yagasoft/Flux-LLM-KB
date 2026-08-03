using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

public sealed record GpuMiniTaskHandoffRequest(
    ClaimedJob ParentJob,
    GpuPriorityLane PriorityLane,
    string ModelRuntimeKey,
    string SettingsFingerprint,
    long EstimatedBytes,
    string IdempotencyKey);

public sealed record GpuMiniTaskHandoffResult(
    Guid MiniTaskId,
    bool IsIdempotentReplay,
    bool Committed);

/// <summary>
/// Guards opaque scheduler keys whose exact SQL identity must not be altered by string padding.
/// </summary>
public static class GpuSchedulerOpaqueKeyValidator
{
    public static void RequireCanonical(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                "Scheduler opaque keys cannot end with whitespace.",
                parameterName);
        }
    }
}

public sealed record GpuBatchCandidate(
    GpuPriorityLane PriorityLane,
    string ModelRuntimeKey,
    string SettingsFingerprint,
    int ItemCount,
    long EstimatedBytes);

public enum GpuAdmissionDisposition
{
    Admit,
    Busy,
    Defer
}

public sealed record GpuAdmissionDecision(
    GpuAdmissionDisposition Disposition,
    string? CapacitySlotKey,
    string? OwnerKey,
    TimeSpan? RetryAfter)
{
    public GpuAdmissionDecision Validate(GpuSchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(Disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(Disposition));
        }

        if (Disposition == GpuAdmissionDisposition.Admit)
        {
            GpuSchedulerOpaqueKeyValidator.RequireCanonical(CapacitySlotKey, nameof(CapacitySlotKey));
            GpuSchedulerOpaqueKeyValidator.RequireCanonical(OwnerKey, nameof(OwnerKey));
            if (RetryAfter is not null)
            {
                throw new ArgumentException("An admitted batch cannot have a retry delay.", nameof(RetryAfter));
            }

            return this;
        }

        if (Disposition == GpuAdmissionDisposition.Busy)
        {
            if (CapacitySlotKey is not null || OwnerKey is not null || RetryAfter is not null)
            {
                throw new ArgumentException("A busy admission decision has no capacity reservation or retry delay.");
            }

            return this;
        }

        if (CapacitySlotKey is not null || OwnerKey is not null)
        {
            throw new ArgumentException("A deferred admission decision cannot reserve capacity.");
        }

        if (RetryAfter is null || RetryAfter <= TimeSpan.Zero)
        {
            throw new ArgumentException("A deferred admission decision requires a positive retry delay.", nameof(RetryAfter));
        }

        return this with { RetryAfter = options.CapRetryDelay(RetryAfter.Value) };
    }
}

public sealed record GpuSchedulerAdmissionRoundResult(
    bool Committed,
    GpuAdmissionDisposition Disposition,
    DateTimeOffset? DeferredUntilUtc);

public enum GpuBatchCallbackKind
{
    SafeBoundary,
    Completed,
    CapacityReleased
}

public sealed record GpuBatchCallback(
    Guid BatchId,
    string CapacitySlotKey,
    string OwnerKey,
    long AdmissionGeneration,
    GpuBatchCallbackKind Kind,
    IReadOnlyList<GpuMiniTaskBoundaryOutcome> Outcomes,
    bool CapacityReleased)
{
    public void Validate()
    {
        if (BatchId == Guid.Empty)
        {
            throw new ArgumentException("A callback requires a batch ID.", nameof(BatchId));
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(CapacitySlotKey, nameof(CapacitySlotKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(OwnerKey, nameof(OwnerKey));
        if (AdmissionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AdmissionGeneration));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        ArgumentNullException.ThrowIfNull(Outcomes);
        if (Kind == GpuBatchCallbackKind.SafeBoundary && !CapacityReleased && Outcomes.Count != 0)
        {
            throw new ArgumentException("A retained safe-boundary callback cannot report task outcomes.", nameof(Outcomes));
        }

        if ((Kind != GpuBatchCallbackKind.SafeBoundary || CapacityReleased) && Outcomes.Count == 0)
        {
            throw new ArgumentException("A releasing or terminal callback must report task outcomes.", nameof(Outcomes));
        }

        if (Outcomes.Any(outcome => outcome is null || outcome.MiniTaskId == Guid.Empty || !Enum.IsDefined(outcome.Disposition)) ||
            Outcomes.Select(outcome => outcome.MiniTaskId).Distinct().Count() != Outcomes.Count)
        {
            throw new ArgumentException("Callback outcomes must have distinct, valid mini-task IDs.", nameof(Outcomes));
        }

        if ((Kind is GpuBatchCallbackKind.Completed or GpuBatchCallbackKind.CapacityReleased) && !CapacityReleased)
        {
            throw new ArgumentException("Completed and capacity-release callbacks must explicitly release capacity.", nameof(CapacityReleased));
        }

        if (Kind == GpuBatchCallbackKind.Completed && Outcomes.Any(outcome => outcome.Disposition != GpuMiniTaskBoundaryDisposition.Completed))
        {
            throw new ArgumentException("A completed callback must report only completed task outcomes.", nameof(Outcomes));
        }

        if ((Kind == GpuBatchCallbackKind.CapacityReleased || (Kind == GpuBatchCallbackKind.SafeBoundary && CapacityReleased)) &&
            Outcomes.Any(outcome => outcome.Disposition != GpuMiniTaskBoundaryDisposition.OutcomeUncertain))
        {
            throw new ArgumentException("A releasing boundary must report unresolved task outcomes.", nameof(Outcomes));
        }
    }
}

public sealed record GpuMiniTaskBoundaryOutcome(
    Guid MiniTaskId,
    GpuMiniTaskBoundaryDisposition Disposition);

public sealed record GpuBatchCallbackResult(bool Accepted, bool Committed);

public sealed record GpuCapacityUncertaintyRequest(
    Guid BatchId,
    string CapacitySlotKey,
    string OwnerKey,
    long AdmissionGeneration,
    DateTimeOffset ObservedLastHeartbeatAtUtc,
    byte[] ObservedSlotRowVersion);

public sealed record GpuDiagnosticTransitionResult(bool Committed);

public sealed record GpuTrustedCapacityReconciliation(
    Guid BatchId,
    string CapacitySlotKey,
    string OwnerKey,
    long AdmissionGeneration,
    string EvidenceClass);

public sealed record GpuTrustedReconciliationResult(bool Committed);

/// <summary>
/// Internal scheduler boundary for recording a verified unknown outcome. This is deliberately
/// distinct from capacity reconciliation and is not an executor, model, or Web contract.
/// </summary>
public sealed record GpuTaskOutcomeReconciliation(
    Guid BatchId,
    string CapacitySlotKey,
    string OwnerKey,
    long AdmissionGeneration,
    IReadOnlyList<Guid> MiniTaskIds,
    string EvidenceClass);

public sealed record GpuSchedulerWakeSnapshot(
    long Generation,
    GpuSchedulerWakeReason Reasons,
    DateTimeOffset? NextDeferredAtUtc,
    Guid? ConsumptionOperationId = null,
    GpuSchedulerWakeReason? EffectiveAdmissionReasons = null);

public sealed record GpuSchedulerWakeConsumption(
    bool Consumed,
    GpuSchedulerWakeSnapshot Snapshot);

public sealed record GpuSchedulerStatusSnapshot(
    int ReadyCount,
    int ActiveCount,
    int DeferredCount,
    int OutcomeUncertainCount,
    IReadOnlyDictionary<GpuPriorityLane, int> LaneCounts,
    bool HasActiveBatch,
    GpuPriorityLane? ActiveBatchLane,
    int AvailableSlotCount,
    int ReservedSlotCount,
    int UncertainSlotCount,
    DateTimeOffset? NextDeferredAtUtc,
    TimeSpan? UncertainCapacityAge)
{
    public static GpuSchedulerStatusSnapshot Empty { get; } = new(
        0, 0, 0, 0, new Dictionary<GpuPriorityLane, int>(), false, null, 0, 0, 0, null, null);
}
