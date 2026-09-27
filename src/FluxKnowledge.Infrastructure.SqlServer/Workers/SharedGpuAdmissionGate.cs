using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Allowlisted OCR and retrieval use the existing exclusive physical slot.
/// This gate is not registered until the native ownership and measured memory gates pass.</summary>
public sealed class SharedGpuAdmissionGate : IGpuAdmissionGate
{
    private readonly GpuWorkloadPolicy _policy;
    private readonly string _backgroundExecutor;
    private readonly long _maximumRetrievalBytes;

    public SharedGpuAdmissionGate(GpuWorkloadPolicy policy, string backgroundExecutorKey, long maximumRetrievalBytes)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(backgroundExecutorKey, nameof(backgroundExecutorKey), 256);
        if (maximumRetrievalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRetrievalBytes));
        if (policy.Classify(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint) != GpuWorkloadKind.Ocr ||
            policy.Profiles.Any(p => p.Kind == GpuWorkloadKind.Ocr && (p.ModelRuntimeKey != PaddleOcrVlmRuntimeContract.ModelRuntimeKey ||
                p.SettingsFingerprint != PaddleOcrVlmRuntimeContract.SettingsFingerprint)))
            throw new ArgumentException("shared-gpu-ocr-profile-invalid", nameof(policy));
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
