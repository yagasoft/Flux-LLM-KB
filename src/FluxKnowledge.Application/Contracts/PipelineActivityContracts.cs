namespace FluxKnowledge.Application.Contracts;

public sealed record PipelineActivitySummary(
    DateTimeOffset ReadStartedAtUtc,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<SourcePipelineProgress> Sources,
    IReadOnlyList<PipelineWorkGroup> Jobs,
    IReadOnlyList<GpuActivityGroup> GpuTasks,
    int ActiveScans,
    int ActiveProcessors,
    int UnconfirmedControls)
{
    public static PipelineActivitySummary Unknown { get; } = new(default, default, [], [], [], 0, 0, 0);
    public bool IsAvailable => ObservedAtUtc != default;
    public bool IsConsistent { get; init; } = true;
    public int UncertainCapacitySlots { get; init; }
}

public sealed record SourcePipelineProgress(
    Guid Id, string Name, string Path, string Lifecycle,
    int KnownFiles, int PublishedFiles, int PendingFiles, int DeferredFiles,
    int BlockedFiles, int FailedFiles, int MetadataOnlyFiles, int CompletedWithoutTextFiles,
    DateTimeOffset? LastScanCompletedAtUtc, DateTimeOffset? LastDiscoveryAtUtc,
    DateTimeOffset? LastProgressAtUtc, bool IsScanning, string? LifecycleReason,
    int ActiveProcessors = 0, int ScanErrors = 0, int ActiveSourceOperations = 0, string? LifecyclePhase = null,
    int ProvisionalFiles = 0);

public enum PipelineWorkState { Running, Queued, Waiting, Attention, Historical }

public sealed record PipelineWorkGroup(
    Guid? SourceRootId, string SourceName, string Stage, PipelineWorkState State,
    bool IsHistorical, string Reason, int Count);

public sealed record GpuActivityGroup(
    Guid? SourceRootId, string SourceName, string State, int Count);

public sealed record PipelineAttentionItem(
    Guid Id, Guid? SourceRootId, Guid? PipelineRecordId, string SourceName,
    string Unit, string State, string Applicability, string Reason, DateTimeOffset? RecordedAtUtc);

public sealed record PipelineAttentionPage(int TotalCount, int Page, int PageSize, IReadOnlyList<PipelineAttentionItem> Items)
{
    public static PipelineAttentionPage Empty { get; } = new(0, 0, 25, []);
}
