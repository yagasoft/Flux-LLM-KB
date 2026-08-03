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
}

public sealed record IndexRecoverySummary(
    string State,
    string? ActiveGeneration,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? FailureCategory,
    int CleanedCandidateCount);

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
