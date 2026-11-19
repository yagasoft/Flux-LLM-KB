using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

/// <summary>The trusted local media model bundle, routing settings and owned executor admitted together.</summary>
public sealed class MediaTranscriptionGpuRuntime
{
    public const GpuPriorityLane PriorityLane = GpuPriorityLane.VideoOrUnknown;

    public MediaTranscriptionGpuRuntime(string modelRuntimeKey, string settingsFingerprint, string executorKey, long estimatedBytes)
    {
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(modelRuntimeKey, nameof(modelRuntimeKey), 256);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(settingsFingerprint, nameof(settingsFingerprint), 256);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(executorKey, nameof(executorKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (estimatedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(estimatedBytes));

        ModelRuntimeKey = modelRuntimeKey;
        SettingsFingerprint = settingsFingerprint;
        ExecutorKey = executorKey;
        EstimatedBytes = estimatedBytes;
        WorkloadProfile = new(modelRuntimeKey, settingsFingerprint, GpuWorkloadKind.MediaTranscription);
    }

    public string ModelRuntimeKey { get; }
    public string SettingsFingerprint { get; }
    public string ExecutorKey { get; }
    public long EstimatedBytes { get; }
    public GpuWorkloadProfile WorkloadProfile { get; }
}
