using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Web.Components.Status;

public sealed class PipelineActivityView
{
    public string State { get; private init; } = "Status unavailable";
    public string Message { get; private init; } = "No successful status read is available.";
    public IReadOnlyList<SourcePipelineProgress> Sources { get; private init; } = [];
    public IReadOnlyList<PipelineWorkGroup> Jobs { get; private init; } = [];
    public int RunningJobs => Jobs.Where(j => j.State == PipelineWorkState.Running).Sum(j => j.Count);
    public int QueuedJobs => Jobs.Where(j => j.State == PipelineWorkState.Queued).Sum(j => j.Count);
    public int WaitingJobs => Jobs.Where(j => j.State == PipelineWorkState.Waiting).Sum(j => j.Count);
    public int AttentionJobs => Jobs.Where(j => j.State == PipelineWorkState.Attention).Sum(j => j.Count);
    public int HistoricalJobs => Jobs.Where(j => j.IsHistorical).Sum(j => j.Count);
    public int KnownFiles => Sources.Sum(s => s.KnownFiles);
    public int PublishedFiles => Sources.Sum(s => s.PublishedFiles);
    public int PendingFiles => Sources.Sum(s => s.PendingFiles);
    public int BlockedFiles => Sources.Sum(s => s.BlockedFiles);
    public int FailedFiles => Sources.Sum(s => s.FailedFiles);
    public int ProvisionalFiles => Sources.Sum(s => s.ProvisionalFiles);
    public int ActiveGpuTasks { get; private init; }
    public int ReadyGpuTasks { get; private init; }
    public int UncertainGpuTasks { get; private init; }
    public int UnconfirmedGpuTasks { get; private init; }
    public int WaitingGpuTasks { get; private init; }
    public int HistoricalGpuTasks { get; private init; }

    public static PipelineActivityView From(PipelineActivitySummary summary, Guid? sourceRootId = null)
    {
        if (!summary.IsAvailable) return new();
        var sources = summary.Sources.Where(s => sourceRootId is null || s.Id == sourceRootId).ToArray();
        var jobs = summary.Jobs.Where(j => sourceRootId is null || j.SourceRootId == sourceRootId).ToArray();
        var gpu = summary.GpuTasks.Where(g => sourceRootId is null || g.SourceRootId == sourceRootId).ToArray();
        var activeGpu = gpu.Where(g => g.State == "Active").Sum(g => g.Count);
        var uncertainGpu = gpu.Where(g => g.State == "Outcome uncertain").Sum(g => g.Count);
        var unconfirmedGpu = gpu.Where(g => g.State == "Ownership unconfirmed").Sum(g => g.Count);
        var waitingGpu = gpu.Where(g => g.State is "Awaiting dispatch" or "Deferred" or "Held").Sum(g => g.Count);
        var running = jobs.Any(j => j.State == PipelineWorkState.Running) || activeGpu > 0 ||
            sources.Any(s => s.IsScanning || s.ActiveProcessors > 0 || s.ActiveSourceOperations > 0) || sourceRootId is null && summary.ActiveProcessors > 0;
        var pending = sources.Sum(s => s.PendingFiles);
        var catchingUp = sources.Any(s => s.PendingFiles > 0 && s.LastProgressAtUtc > s.LastDiscoveryAtUtc &&
            (s.ActiveProcessors > 0 || jobs.Any(j => j.SourceRootId == s.Id &&
                j.State == PipelineWorkState.Running && !j.IsHistorical)));
        var blocked = sources.Any(s => s.BlockedFiles > 0 || s.FailedFiles > 0 || s.ProvisionalFiles > 0 || s.LifecycleReason is not null || s.ScanErrors > 0) ||
            jobs.Any(j => j.State == PipelineWorkState.Attention);
        var waiting = pending > 0 || waitingGpu > 0 || sources.Any(s => s.DeferredFiles > 0 || s.WaitingScans > 0 || s.Lifecycle == "Deleting") ||
            jobs.Any(j => j.State == PipelineWorkState.Waiting);
        var queued = sources.Any(s => s.QueuedScans > 0) || jobs.Any(j => j.State == PipelineWorkState.Queued) || gpu.Any(g => g.State == "Ready");
        var state = running ? catchingUp ? "Catching up" : "Running"
            : unconfirmedGpu > 0 || sources.Any(s => s.UnconfirmedControls > 0) || summary.UncertainCapacitySlots > 0 || sourceRootId is null && summary.UnconfirmedControls > 0 ? "Needs attention"
            : blocked ? "Blocked" : queued ? "Queued" : waiting ? "Waiting" : "Idle";
        var message = state switch
        {
            "Catching up" => "Earlier discovered changes are progressing; known files still await completion.",
            "Running" => "Execution ownership is confirmed for the work shown below.",
            "Needs attention" => "Some execution ownership cannot be confirmed. Review the attention details.",
            "Blocked" => "No work is confirmed running. Current source work needs attention.",
            "Queued" when sources.Any(s => s.QueuedScans > 0) => "Source scans are ready to check for changes; file discovery has not completed. No execution is confirmed running.",
            "Queued" => "Work is ready for processing; no execution is confirmed running.",
            "Waiting" when sources.Any(s => s.WaitingScans > 0) => "Source scans await release, retry or source admission. File discovery may be incomplete; no execution is confirmed running.",
            "Waiting" => "No work is confirmed running. Known work is pending, deferred or held by admission or source state.",
            _ => "No current processing backlog or running work was found in this status read. Historical outcomes remain below."
        };
        return new()
        {
            State = state, Message = message, Sources = sources, Jobs = jobs, ActiveGpuTasks = activeGpu,
            ReadyGpuTasks = gpu.Where(g => g.State == "Ready").Sum(g => g.Count),
            UncertainGpuTasks = uncertainGpu, UnconfirmedGpuTasks = unconfirmedGpu, WaitingGpuTasks = waitingGpu,
            HistoricalGpuTasks = gpu.Where(g => g.State == "Historical unresolved").Sum(g => g.Count)
        };
    }
}
