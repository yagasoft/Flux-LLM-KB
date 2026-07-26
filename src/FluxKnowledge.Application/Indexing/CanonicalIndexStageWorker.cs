using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Indexing;

public sealed class CanonicalIndexStageWorker(
    IPipelineStageReader pipelineReader,
    StageTransitionService transitions,
    TimeProvider timeProvider) : IStageWorker
{
    public string Operation => PipelineOperations.CanonicalIndex;

    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        var source = await pipelineReader.ReadStageSourceAsync(
            workItem.Job.PipelineRecordId, workItem.Job.SourceRevision, workItem.Job.Stage, cancellationToken);
        if (source.InputText is null)
        {
            await transitions.FailAsync(new StageFailureRequest(
                workItem.DispatchMessage, workItem.Job, "required normalised artefact is missing", null,
                nameof(CanonicalIndexStageWorker)), cancellationToken);
            return;
        }

        var chunks = TextChunker.Chunk(source.InputText);
        await transitions.TransitionAsync(new StageTransitionRequest(
            workItem.DispatchMessage,
            workItem.Job,
            new StageArtifact(Guid.NewGuid(), PipelineStage.CanonicalIndex,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.InputText))),
                "text/plain; charset=utf-8; canonical-chunks=v1", source.InputText, timeProvider.GetUtcNow()),
            PipelineStage.Embed,
            PipelineOperations.Embed,
            nameof(CanonicalIndexStageWorker),
            new IndexingStageOutput(Chunks: chunks)), cancellationToken);
    }
}
