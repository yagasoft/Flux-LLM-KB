using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Application.Indexing;

public sealed class PublishStageWorker(
    IIndexGenerationStore indexStore,
    IPipelineStageReader pipelineReader,
    IIndexGenerationPublisher publisher,
    StageTransitionService transitions,
    TimeProvider timeProvider,
    ILogger<PublishStageWorker>? logger = null) : IStageWorker
{
    public string Operation => PipelineOperations.Publish;

    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        var phase = "generation-resolution";
        try
        {
            await ExecuteCoreAsync(workItem, value => phase = value, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            PublicationDiagnostics.Failure(logger, workItem.Job.JobId.Value, phase, exception);
            throw;
        }
    }

    private async ValueTask ExecuteCoreAsync(StageWorkItem workItem, Action<string> setPhase, CancellationToken cancellationToken)
    {
        var generation = await FindGenerationAsync(workItem, cancellationToken);
        if (generation is null)
        {
            await transitions.FailAsync(new StageFailureRequest(workItem.DispatchMessage, workItem.Job,
                "required embedding generation is missing", null, nameof(PublishStageWorker)), cancellationToken);
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            setPhase("snapshot-vector-loading-and-placement");
            var candidate = await publisher.BuildAndPlaceAsync(generation.Id, cancellationToken);
            var placed = candidate.Generation;
            try
            {
                setPhase("activation");
                await transitions.TransitionAsync(new StageTransitionRequest(
                    workItem.DispatchMessage, workItem.Job,
                    new StageArtifact(Guid.NewGuid(), PipelineStage.Publish,
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(placed.MetadataChecksum))),
                        "application/vnd.fluxknowledge.usearch-generation", placed.Id.ToString("N"), timeProvider.GetUtcNow()),
                    null, null, nameof(PublishStageWorker), new IndexingStageOutput(
                        ActivateGeneration: placed,
                        ActivateMembership: candidate.Vectors,
                        ExpectedCorpusStamp: candidate.ExpectedCorpusStamp)), cancellationToken);
                return;
            }
            catch (PublicationSnapshotConflictException exception)
            {
                PublicationDiagnostics.Failure(logger, workItem.Job.JobId.Value, "activation-snapshot-conflict", exception);
                // Valid vectors and immutable placements are reusable; only membership changed.
            }
        }
        await transitions.RetryAsync(new StageRetryRequest(workItem.DispatchMessage, workItem.Job,
            timeProvider.GetUtcNow().AddSeconds(5), "publication-snapshot-conflict", nameof(PublishStageWorker)), cancellationToken);
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
