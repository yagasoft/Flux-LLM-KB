namespace FluxKnowledge.Domain.Jobs;

public enum PublicJobState
{
    WorkerQueued,
    WorkerProcessing,
    GpuQueued,
    GpuProcessing,
    Completed,
    Failed
}
