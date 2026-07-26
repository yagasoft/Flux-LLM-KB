namespace FluxKnowledge.Domain.Jobs;

public static class PublicJobStateExtensions
{
    public static IReadOnlyList<string> AllWireValues { get; } =
    ["worker queued", "worker processing", "gpu queued", "gpu processing", "completed", "failed"];

    public static string ToWireValue(this PublicJobState state) => state switch
    {
        PublicJobState.WorkerQueued => "worker queued",
        PublicJobState.WorkerProcessing => "worker processing",
        PublicJobState.GpuQueued => "gpu queued",
        PublicJobState.GpuProcessing => "gpu processing",
        PublicJobState.Completed => "completed",
        PublicJobState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown public job state.")
    };
}
