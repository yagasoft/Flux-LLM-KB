using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Allowlisted workloads use the existing exclusive physical slot.
/// This gate is not registered until the native ownership and measured memory gates pass.</summary>
public sealed class SharedGpuAdmissionGate : IGpuAdmissionGate
{
    private readonly GpuWorkloadPolicy _policy;
    private readonly string _backgroundExecutor;
    private readonly long _maximumRetrievalBytes;
    private readonly MediaTranscriptionGpuRuntime? _transcription;

    public SharedGpuAdmissionGate(GpuWorkloadPolicy policy, string backgroundExecutorKey, long maximumRetrievalBytes)
        : this(policy, backgroundExecutorKey, maximumRetrievalBytes, null)
    {
    }

    public SharedGpuAdmissionGate(GpuWorkloadPolicy policy, string backgroundExecutorKey, long maximumRetrievalBytes,
        MediaTranscriptionGpuRuntime? transcription)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(backgroundExecutorKey, nameof(backgroundExecutorKey), 256);
        if (maximumRetrievalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRetrievalBytes));
        if (policy.Classify(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint) != GpuWorkloadKind.Ocr ||
            policy.Profiles.Any(p => p.Kind == GpuWorkloadKind.Ocr && (p.ModelRuntimeKey != PaddleOcrVlmRuntimeContract.ModelRuntimeKey ||
                p.SettingsFingerprint != PaddleOcrVlmRuntimeContract.SettingsFingerprint)))
            throw new ArgumentException("shared-gpu-ocr-profile-invalid", nameof(policy));
        var transcriptionProfiles = policy.Profiles.Where(profile => profile.Kind == GpuWorkloadKind.MediaTranscription);
        if (!transcriptionProfiles.SequenceEqual(transcription is null ? [] : [transcription.WorkloadProfile]))
            throw new ArgumentException("shared-gpu-transcription-profile-invalid", nameof(transcription));
        if (transcription is not null &&
            (transcription.ExecutorKey == PaddleOcrVlmRuntimeContract.ExecutorKey || transcription.ExecutorKey == backgroundExecutorKey ||
             transcription.ExecutorKey.StartsWith("retrieval-gpu:", StringComparison.Ordinal)))
            throw new ArgumentException("shared-gpu-transcription-executor-conflict", nameof(transcription));

        _transcription = transcription;
        _backgroundExecutor = backgroundExecutorKey;
        _maximumRetrievalBytes = maximumRetrievalBytes;
    }

    public ValueTask<GpuAdmissionDecision> DecideAsync(GpuBatchCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        string? executor = null;
        var kind = _policy.Classify(candidate.ModelRuntimeKey, candidate.SettingsFingerprint);
        if (kind == GpuWorkloadKind.Ocr && candidate.ItemCount == 1 &&
            candidate.EstimatedBytes == PaddleOcrVlmRuntimeContract.EstimatedDocumentBytes &&
            candidate.RequiredExecutorKey is null && candidate.PriorityLane != GpuPriorityLane.InteractiveRetrieval)
            executor = PaddleOcrVlmRuntimeContract.ExecutorKey;
        else if (kind == GpuWorkloadKind.Retrieval && candidate.EstimatedBytes > 0 && candidate.EstimatedBytes <= _maximumRetrievalBytes)
        {
            if (candidate.PriorityLane == GpuPriorityLane.InteractiveRetrieval && candidate.ItemCount == 1 &&
                IsInteractiveExecutor(candidate.RequiredExecutorKey))
                executor = candidate.RequiredExecutorKey;
            else if (candidate.PriorityLane == GpuPriorityLane.DocumentIndexing && candidate.RequiredExecutorKey is null && candidate.ItemCount is >= 1 and <= 4)
                executor = _backgroundExecutor;
        }
        else if (kind == GpuWorkloadKind.MediaTranscription && _transcription is { } transcription &&
            candidate.PriorityLane == MediaTranscriptionGpuRuntime.PriorityLane && candidate.ItemCount == 1 &&
            candidate.EstimatedBytes == transcription.EstimatedBytes && candidate.RequiredExecutorKey is null)
            executor = transcription.ExecutorKey;

        return ValueTask.FromResult(executor is null
            ? new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null)
            : new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                PaddleOcrVlmRuntimeContract.CapacityOwnerKey, null, executor));
    }

    private static bool IsInteractiveExecutor(string? key)
    {
        const string prefix = "retrieval-gpu:";
        return key is not null && key.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(key.AsSpan(prefix.Length), "N", out var instance) && instance != Guid.Empty &&
            key == $"{prefix}{instance:N}";
    }
}
