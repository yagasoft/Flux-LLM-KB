using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Pipeline;

public sealed record StageArtifact(
    Guid Id,
    PipelineStage Stage,
    string ContentHash,
    string ContentType,
    string SearchText,
    DateTimeOffset CreatedAtUtc);

public sealed record IndexingStageOutput(
    IReadOnlyList<CanonicalTextChunk>? Chunks = null,
    Guid? IndexGenerationId = null,
    string? ModelFingerprint = null,
    IReadOnlyList<CanonicalVector>? Vectors = null,
    IndexGenerationDescriptor? ActivateGeneration = null,
    IReadOnlyList<CanonicalVector>? ActivateMembership = null);

public sealed record StageTransitionRequest(
    ClaimedDispatchMessage DispatchMessage,
    ClaimedJob CurrentJob,
    StageArtifact Artifact,
    PipelineStage? NextStage,
    string? NextOperation,
    string Actor,
    IndexingStageOutput? IndexingOutput = null);

public sealed record StageTransitionResult(
    Guid ArtifactId,
    JobId? NextJobId,
    DispatchMessageId? NextDispatchMessageId,
    bool ExistingTransition);

public sealed record StageFailureRequest(
    ClaimedDispatchMessage DispatchMessage,
    ClaimedJob CurrentJob,
    string Reason,
    string? ErrorDetails,
    string Actor);
