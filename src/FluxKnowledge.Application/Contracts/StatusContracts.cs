using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Application.Contracts;

public sealed record StatusChanged(
    PipelineRecordId? PipelineRecordId,
    string Projection,
    DateTimeOffset OccurredAtUtc);

public sealed record OverviewProjection(
    int WorkerQueuedCount,
    int WorkerProcessingCount,
    int GpuQueuedCount,
    int GpuProcessingCount,
    int CompletedCount,
    int FailedCount,
    int IndexedRecordCount,
    string ActiveIndexGeneration,
    IndexRecoverySummary IndexRecovery)
{
    public GpuSchedulerStatusProjection GpuSchedulerStatus { get; init; } =
        GpuSchedulerStatusProjection.Empty;

    public SourceIndexingSummary SourceIndexing { get; init; } = SourceIndexingSummary.Empty;

    public OutlookCaptureSummary OutlookCapture { get; init; } = OutlookCaptureSummary.Empty;
}

public sealed record OutlookCaptureSummary(
    int ProfileCount,
    int EnabledProfileCount,
    int FolderCount,
    int IngestedCount,
    int DeferredCount,
    int BlockedCount)
{
    public static OutlookCaptureSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public sealed record SourceIndexingSummary(
    int RootCount,
    int IndexedCount,
    int DeferredCount,
    int BlockedCount,
    int ErrorCount)
{
    public static SourceIndexingSummary Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record IndexRecoverySummary(
    string State,
    string? ActiveGeneration,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? FailureCategory,
    int CleanedCandidateCount);

public sealed record OverviewDiagnosticSummary(
    string State,
    string Reason,
    string ActiveGenerationDiagnostic,
    string? RecoveryGenerationDiagnostic)
{
    public static OverviewDiagnosticSummary From(string activeIndexGeneration, IndexRecoverySummary recovery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeIndexGeneration);
        ArgumentNullException.ThrowIfNull(recovery);

        var state = recovery.State switch
        {
            "Healthy" => "Healthy",
            "Starting" or "Recovering" or "RetryScheduled" or "Rebuilding" => "Recovering",
            _ => "Blocked"
        };
        var reason = state switch
        {
            "Healthy" => "Derived index is current.",
            "Recovering" when string.Equals(recovery.FailureCategory, "TransientIo", StringComparison.Ordinal) =>
                "Retry scheduled after a transient I/O failure.",
            "Recovering" => "Derived index recovery is in progress.",
            _ when !string.IsNullOrWhiteSpace(recovery.FailureCategory) =>
                $"Index recovery is blocked by {recovery.FailureCategory}.",
            _ => "Index recovery needs operator attention."
        };

        return new OverviewDiagnosticSummary(
            state,
            reason,
            activeIndexGeneration,
            recovery.ActiveGeneration);
    }
}

public sealed record GpuSchedulerStatusProjection(
    int ReadyCount,
    int ActiveCount,
    int DeferredCount,
    int OutcomeUncertainCount,
    GpuSchedulerLaneCounts LaneCounts,
    bool HasActiveBatch,
    string? ActiveBatchLane,
    int AvailableSlotCount,
    int ReservedSlotCount,
    int UncertainSlotCount,
    DateTimeOffset? NextDeferredAtUtc,
    GpuCapacityUncertaintySummary UncertainCapacity)
{
    public static GpuSchedulerStatusProjection Empty { get; } = new(
        0,
        0,
        0,
        0,
        GpuSchedulerLaneCounts.Empty,
        false,
        null,
        0,
        0,
        0,
        null,
        GpuCapacityUncertaintySummary.None);
}

public sealed record GpuSchedulerLaneCounts(
    int InteractiveRetrieval,
    int DocumentIndexing,
    int ImageOcr,
    int ImageEnrichment,
    int VideoOrUnknown)
{
    public static GpuSchedulerLaneCounts Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record GpuCapacityUncertaintySummary(string State, int? AgeMinutes)
{
    public static GpuCapacityUncertaintySummary None { get; } = new("None", null);
}
