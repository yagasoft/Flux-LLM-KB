using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

public sealed class NormaliseTextStageWorker(
    IPipelineStageReader pipelineReader,
    StageTransitionService transitions,
    TimeProvider timeProvider) : IStageWorker
{
    public string Operation => PipelineOperations.NormaliseText;

    public async ValueTask ExecuteAsync(
        StageWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var source = await pipelineReader.ReadStageSourceAsync(
                workItem.Job.PipelineRecordId,
                workItem.Job.SourceRevision,
                workItem.Job.Stage,
                cancellationToken)
            .ConfigureAwait(false);
        if (source.InputText is null)
        {
            await transitions.FailAsync(
                    new StageFailureRequest(
                        workItem.DispatchMessage,
                        workItem.Job,
                        "required extract artefact is missing",
                        null,
                        nameof(NormaliseTextStageWorker)),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string normalised;
        string? normalisedMetadataJson;
        if (source.InputDocumentMetadataJson is null)
        {
            normalised = NormalisePlainText(source.InputText);
            normalisedMetadataJson = null;
        }
        else
        {
            normalised = DocumentOcrProvenance.NormaliseTextAndMetadata(
                source.InputText,
                source.InputDocumentMetadataJson,
                out var metadata);
            normalisedMetadataJson = metadata;
        }
        var bytes = Encoding.UTF8.GetBytes(normalised);
        await transitions.TransitionAsync(
                new StageTransitionRequest(
                    workItem.DispatchMessage,
                    workItem.Job,
                    new StageArtifact(
                        Guid.NewGuid(),
                        PipelineStage.Normalise,
                        Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        "text/plain; charset=utf-8; normalization=formkc; line-endings=lf",
                        normalised,
                        timeProvider.GetUtcNow(),
                        normalisedMetadataJson),
                    PipelineStage.CanonicalIndex,
                    PipelineOperations.CanonicalIndex,
                    nameof(NormaliseTextStageWorker)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormalisePlainText(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return UnicodeTextNormalisation.FormKC(lines);
    }
}
