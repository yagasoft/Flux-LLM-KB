using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

public static class PipelineOperations
{
    public const string ExtractUtf8 = "extract utf-8";
    public const string NormaliseText = "normalise text";
    public const string CanonicalIndex = "canonical index";
    public const string Embed = "embed";
    public const string Publish = "publish";
}

public sealed record StageWorkItem(
    ClaimedDispatchMessage DispatchMessage,
    ClaimedJob Job);

public sealed record PipelineStageSource(
    PipelineRecordId PipelineRecordId,
    long SourceRevision,
    string CanonicalPath,
    string RegisteredContentHash,
    string? InputText);

public interface IPipelineStageReader
{
    ValueTask<PipelineStageSource> ReadStageSourceAsync(
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        CancellationToken cancellationToken);
}

public interface IStageWorker
{
    string Operation { get; }

    ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken);
}
