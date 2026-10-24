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
    public IReadOnlyList<PipelineFileStageGroup> FileStages { get; init; } = [];
    public IReadOnlyList<PipelineFileCauseGroup> FileCauses { get; init; } = [];
    public IReadOnlyList<PipelineOcrDemand> OcrDemand { get; init; } = [];
    public IReadOnlyList<PipelineRunningTask> RunningTasks { get; init; } = [];
    public int RunningTaskCount { get; init; }
}

public sealed record SourcePipelineProgress(
    Guid Id, string Name, string Path, string Lifecycle,
    int KnownFiles, int PublishedFiles, int PendingFiles, int DeferredFiles,
    int BlockedFiles, int FailedFiles, int MetadataOnlyFiles, int CompletedWithoutTextFiles,
    DateTimeOffset? LastScanCompletedAtUtc, DateTimeOffset? LastDiscoveryAtUtc,
    DateTimeOffset? LastProgressAtUtc, bool IsScanning, string? LifecycleReason,
    int ActiveProcessors = 0, int ScanErrors = 0, int ActiveSourceOperations = 0, string? LifecyclePhase = null,
    int ProvisionalFiles = 0, int QueuedScans = 0, int WaitingScans = 0, int UnconfirmedControls = 0);

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

public sealed record PipelineFileCategoryCount(string Category, string Label, int Count);
public sealed record PipelineFileStageGroup(Guid SourceRootId, string Stage, string State, int Count);
public sealed record PipelineFileCauseGroup(Guid SourceRootId, string Category, string Reason, string NextAction, int Count);
public sealed record PipelineOcrDemand(Guid SourceRootId, int ConfirmedFiles, int WaitingFiles, int RunningFiles,
    int DecisionUnconfirmedFiles, int? RequestedPages);
public sealed record PipelineFileFilter(string Category = "all", string? Stage = null, string? Reason = null, string? State = null);
public sealed record PipelineFileJob(Guid Id, Guid PipelineRecordId, string Stage, string State, string Reason,
    DateTimeOffset? AttemptStartedAtUtc);
public sealed record PipelineEmbeddingProgress(Guid JobId, long? SavedPassages, long? TotalPassages,
    DateTimeOffset? LastSavedAtUtc, string Evidence)
{
    public long? RemainingPassages => TotalPassages is { } total && SavedPassages is { } saved ? total - saved : null;
}
public sealed record PipelineFileItem(Guid SourceRootId, Guid SourceRevisionId, string SourceName, string FullPath,
    string? ArchivePath, string Category, string Stage, string State, string Reason, string NextAction,
    IReadOnlyList<PipelineFileJob> Jobs, IReadOnlyList<PipelineEmbeddingProgress> Embedding);
public sealed record PipelineFilePage(int TotalCount, int Page, int PageSize, DateTimeOffset ObservedAtUtc,
    IReadOnlyList<PipelineFileItem> Items)
{
    public static PipelineFilePage Unavailable { get; } = new(0, 0, 25, default, []);
    public bool IsAvailable => ObservedAtUtc != default;
}
public sealed record PipelineRunningTask(Guid Id, Guid? SourceRootId, Guid? PipelineRecordId, string SourceName,
    string FullPath, string? ArchivePath, string Stage, string State, bool IsHistorical,
    DateTimeOffset? AttemptStartedAtUtc);
