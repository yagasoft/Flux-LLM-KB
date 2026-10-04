using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Indexing;

public sealed class CanonicalIndexStageWorker(
    IPipelineStageReader pipelineReader,
    StageTransitionService transitions,
    TimeProvider timeProvider,
    PassageBuilder? passageBuilder = null,
    CsharpDisclosureProofBuilder? disclosureProofBuilder = null) : IStageWorker
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

        var chunks = passageBuilder is null ? TextChunker.Chunk(source.InputText) :
            passageBuilder.BuildDocument(source.InputText, source.InputDocumentMetadataJson);
        var artifactId = Guid.NewGuid();
        var proof = Path.GetExtension(source.CanonicalPath).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? disclosureProofBuilder?.Build(artifactId, source.InputText, cancellationToken) : null;
        await transitions.TransitionAsync(new StageTransitionRequest(
            workItem.DispatchMessage,
            workItem.Job,
            new StageArtifact(artifactId, PipelineStage.CanonicalIndex,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.InputText))),
                passageBuilder is null ? "text/plain; charset=utf-8; canonical-chunks=v1" :
                    "text/plain; charset=utf-8; coherent-passages=v1", source.InputText, timeProvider.GetUtcNow(),
                source.InputDocumentMetadataJson),
            PipelineStage.Embed,
            PipelineOperations.Embed,
            nameof(CanonicalIndexStageWorker),
            new IndexingStageOutput(Chunks: chunks, DisclosureProof: proof)), cancellationToken);
    }
}
