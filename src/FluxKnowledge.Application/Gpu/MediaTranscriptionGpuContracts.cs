using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Gpu;

public static class MediaTranscriptionGpuContracts
{
    public const string Operation = MediaTranscriptionDiscoveryOptions.Operation;
    public const string MetadataSchema = "fluxknowledge/media-transcript-v1";
    public const string SidecarSuffix = ".transcript.txt";
    public const string SidecarReceiptContentType = "application/vnd.fluxknowledge.transcript-sidecar+json";
    public const int MaximumResultBytes = 4 * 1024 * 1024;
}

public interface IMediaTranscriptionGpuQueue
{
    ValueTask<MediaTranscriptionGpuHandoff> QueueAsync(ClaimedJob job, SourceRevisionId revision, string contentSha256, CancellationToken ct);
    ValueTask<MediaTranscriptionCompletedResult?> ReadCompletedAsync(ClaimedJob job, SourceRevisionId revision, string contentSha256, CancellationToken ct);
}

public interface ITranscriptSidecarPublisher
{
    ValueTask<string> PublishAsync(StageWorkItem workItem, SourceRevisionId revision, string inputHash,
        MediaTranscriptionCompletedResult completed, CancellationToken ct);

    void ReleasePrivateResult(Guid executionId);
}

public sealed record MediaTranscriptionGpuHandoff(bool Scheduled, string ReasonCode, Guid? MiniTaskId = null);

public sealed record MediaTranscriptionResult(bool Succeeded, string ReasonCode, string? Transcript, string? MetadataJson);

public sealed record MediaTranscriptionCompletedResult(MediaTranscriptionResult Result, Guid? ExecutionId = null,
    string? TranscriptSha256 = null, int? TranscriptByteLength = null, bool UseNativeResult = false);

public sealed record MediaTranscriptionGpuClaim(DocumentOcrLifecycleResult Lifecycle, SourceRevisionId? SourceRevisionId = null,
    string? ContentSha256 = null, bool SourceAvailable = false);

public sealed record MediaTranscriptionGpuCompletion(Guid MiniTaskId, byte[] ResultDigest, bool NativeCleanupConfirmed);

public sealed record MediaTranscriptionGpuRecovery(DocumentOcrExecutionLease Lease, DocumentOcrProcessIdentity? Process,
    bool HasStoredResult, bool NativeCleanupConfirmed);
