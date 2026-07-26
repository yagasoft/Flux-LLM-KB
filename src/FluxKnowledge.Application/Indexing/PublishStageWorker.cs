using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Indexing;

public sealed class PublishStageWorker(
    IIndexGenerationStore indexStore,
    IPipelineStageReader pipelineReader,
    IIndexGenerationPublisher publisher,
    StageTransitionService transitions,
    TimeProvider timeProvider) : IStageWorker
{
    public string Operation => PipelineOperations.Publish;

    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        var generation = await FindGenerationAsync(workItem, cancellationToken);
        if (generation is null)
        {
            await transitions.FailAsync(new StageFailureRequest(workItem.DispatchMessage, workItem.Job,
                "required embedding generation is missing", null, nameof(PublishStageWorker)), cancellationToken);
            return;
        }

        var placed = await publisher.BuildAndPlaceAsync(generation.Id, cancellationToken);
        await transitions.TransitionAsync(new StageTransitionRequest(
            workItem.DispatchMessage, workItem.Job,
            new StageArtifact(Guid.NewGuid(), PipelineStage.Publish,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(placed.MetadataChecksum))),
                "application/vnd.fluxknowledge.usearch-generation", placed.Id.ToString("N"), timeProvider.GetUtcNow()),
            null, null, nameof(PublishStageWorker), new IndexingStageOutput(ActivateGeneration: placed)), cancellationToken);
    }

    private async ValueTask<IndexGenerationDescriptor?> FindGenerationAsync(
        StageWorkItem workItem, CancellationToken cancellationToken)
    {
        // Embed writes one generation per source revision; generation metadata is durable SQL truth.
        // The store deliberately exposes only retrieval by generation, so this stage uses its prior artefact.
        var source = await pipelineReader.ReadStageSourceAsync(
            workItem.Job.PipelineRecordId, workItem.Job.SourceRevision, workItem.Job.Stage, cancellationToken);
        if (!Guid.TryParse(source.InputText, out var generationId))
        {
            return null;
        }

        return await indexStore.GetGenerationAsync(generationId, cancellationToken);
    }
}
