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
    IndexRecoverySummary IndexRecovery);

public sealed record IndexRecoverySummary(
    string State,
    string? ActiveGeneration,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? FailureCategory,
    int CleanedCandidateCount);
