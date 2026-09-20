using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

/// <summary>Only composed by the interactive desktop command. Never registered with the hosted worker dispatcher.</summary>
public sealed class ExtractVisioStageWorker(
    IRetainedSourceReader retainedSourceReader, IPipelineStageReader pipelineReader,
    IVisioDocumentExtractor extractor, IStageTransitionStore transitions, TimeProvider timeProvider) : IStageWorker
{
    public string Operation => PipelineOperations.ExtractVisio;
    public VisioDocumentResult? Result { get; private set; }
    public string OutcomeCode { get; private set; } = "visio-not-started";

    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        if (workItem.Job.Operation != Operation || workItem.DispatchMessage.Operation != Operation)
            throw new InvalidOperationException("visio-operation-invalid");
        try
        {
            var source = await pipelineReader.ReadStageSourceAsync(workItem.Job.PipelineRecordId,
                workItem.Job.SourceRevision, workItem.Job.Stage, cancellationToken).ConfigureAwait(false);
            if (source.RetainedSourceRevisionId is null || source.RetainedSourceClassification != DocumentProcessingInput.VisioClassification ||
                !string.Equals(source.RetainedSourceExtension, ".vsdx", StringComparison.OrdinalIgnoreCase))
                throw new RetainedProcessorException("visio-document-input-invalid");
            var retained = await retainedSourceReader.ReadBytesAsync(source.RetainedSourceRevisionId, cancellationToken).ConfigureAwait(false);
            if (retained.ContentSha256 != source.RegisteredContentHash)
                throw new RetainedProcessorException("retained-artifact-checksum-invalid");
            var result = await extractor.ExtractAsync(retained, cancellationToken).ConfigureAwait(false);
            if (!result.Extraction.IsComplete)
                throw new RetainedProcessorException("visio-extraction-incomplete");
            await transitions.TransitionAsync(new StageTransitionRequest(workItem.DispatchMessage, workItem.Job,
                new StageArtifact(Guid.NewGuid(), PipelineStage.Extract, retained.ContentSha256,
                    "text/plain; charset=utf-8", result.Extraction.Text, timeProvider.GetUtcNow(), result.MetadataJson),
                PipelineStage.Normalise, PipelineOperations.NormaliseText, nameof(ExtractVisioStageWorker)), cancellationToken).ConfigureAwait(false);
            Result = result;
            OutcomeCode = "visio-extracted";
        }
        catch (RetainedProcessorException exception) when (exception.OutcomeCode == "visio-cleanup-unproven")
        {
            // Keep WorkerProcessing as the deletion fence; neither expiry nor cancellation proves COM exit.
            OutcomeCode = exception.OutcomeCode;
            throw;
        }
        catch (RetainedProcessorException exception)
        {
            await FailAsync(exception.OutcomeCode).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The adapter may report cancellation only after its owned process has exited.
            await FailAsync("visio-cancelled").ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message is "visio-source-unavailable" or "visio-lease-expired")
        {
            await FailAsync(exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await FailAsync("visio-retained-input-unavailable").ConfigureAwait(false);
        }

        async ValueTask FailAsync(string code)
        {
            OutcomeCode = code;
            await transitions.FailAsync(new StageFailureRequest(workItem.DispatchMessage, workItem.Job,
                code, null, nameof(ExtractVisioStageWorker)), CancellationToken.None).ConfigureAwait(false);
        }
    }
}
