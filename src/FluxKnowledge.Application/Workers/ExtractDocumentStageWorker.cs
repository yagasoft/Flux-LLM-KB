using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Workers;

/// <summary>
/// Extracts one approved retained binary document. Unlike ExtractUtf8, this is
/// the only worker allowed to read a DocumentProcessingInput revision.
/// </summary>
public sealed class ExtractDocumentStageWorker(
    IRetainedSourceReader retainedSourceReader,
    IPipelineStageReader pipelineReader,
    VsdxDocumentExtractor vsdxExtractor,
    IPdfDocumentExtractor pdfExtractor,
    IDocumentOcrHandoff documentOcrHandoff,
    IDocumentOcrResultReader documentOcrResults,
    StageTransitionService transitions,
    TimeProvider timeProvider) : IStageWorker
{
    public string Operation => PipelineOperations.ExtractDocument;

    public async ValueTask ExecuteAsync(
        StageWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var source = await pipelineReader.ReadStageSourceAsync(
            workItem.Job.PipelineRecordId,
            workItem.Job.SourceRevision,
            workItem.Job.Stage,
            cancellationToken).ConfigureAwait(false);
        if (source.RetainedSourceRevisionId is null || source.RetainedSourceClassification is null || source.RetainedSourceExtension is null ||
            !DocumentProcessingInput.TryGetContract(source.RetainedSourceClassification, source.RetainedSourceExtension, out var contract) ||
            contract == DocumentProcessingInput.Visio)
        {
            await FailAsync("document-input-invalid", "The document pipeline record has no retained document input.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        RetainedSourceBytes retained;
        try
        {
            retained = await retainedSourceReader.ReadBytesAsync(source.RetainedSourceRevisionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await FailAsync("retained-artifact-invalid", exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(retained.ContentSha256, source.RegisteredContentHash, StringComparison.Ordinal))
        {
            await FailAsync("source content changed before document extraction", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (contract.ProcessorFingerprint == DocumentProcessingInput.ImageProcessorFingerprint)
            {
                var imageExtraction = new DocumentExtractionResult(
                    string.Empty,
                    IsComplete: false,
                    ["image-ocr-required"],
                    [new DocumentExtractedPage(0, string.Empty, RequiresOcr: true)]);
                var completedImageOcr = await documentOcrResults.ReadCompletedAsync(
                    workItem.Job, retained.ContentSha256, cancellationToken).ConfigureAwait(false);
                if (completedImageOcr is not null)
                {
                    if (!completedImageOcr.Succeeded)
                    {
                        await FailAsync(completedImageOcr.ReasonCode, null, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    var mergedImage = DocumentOcrProvenance.Merge(imageExtraction, completedImageOcr);
                    await TransitionAsync(mergedImage.Text, mergedImage.MetadataJson).ConfigureAwait(false);
                    return;
                }

                var imageHandoff = await documentOcrHandoff.HandoffAsync(
                    new DocumentOcrHandoffRequest(
                        workItem.Job,
                        source.RetainedSourceRevisionId!,
                        retained.ContentSha256,
                        [0]),
                    cancellationToken).ConfigureAwait(false);
                if (!imageHandoff.Scheduled)
                    await FailAsync(imageHandoff.ReasonCode, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = contract == DocumentProcessingInput.Vsdx
                ? await vsdxExtractor.ExtractAsync(retained, cancellationToken).ConfigureAwait(false)
                : await pdfExtractor.ExtractAsync(retained, cancellationToken).ConfigureAwait(false);
            if (!result.IsComplete)
            {
                if (contract == DocumentProcessingInput.Pdf)
                {
                    var completedOcr = await documentOcrResults.ReadCompletedAsync(
                            workItem.Job,
                            retained.ContentSha256,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (completedOcr is not null)
                    {
                        if (!completedOcr.Succeeded)
                        {
                            await FailAsync(completedOcr.ReasonCode, null, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        var merged = DocumentOcrProvenance.Merge(result, completedOcr);
                        await TransitionAsync(merged.Text, merged.MetadataJson).ConfigureAwait(false);
                        return;
                    }

                    if (!TryGetOcrPageIndexes(result, out var pageIndexes))
                    {
                        await FailAsync(result.Warnings.FirstOrDefault() ?? "document-extraction-incomplete", null, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var handoff = await documentOcrHandoff.HandoffAsync(
                            new DocumentOcrHandoffRequest(
                                workItem.Job,
                                source.RetainedSourceRevisionId!,
                                retained.ContentSha256,
                                pageIndexes),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (handoff.Scheduled)
                    {
                        return;
                    }

                    await FailAsync(handoff.ReasonCode, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await FailAsync(result.Warnings.FirstOrDefault() ?? "document-extraction-incomplete", null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            var nativeMetadata = contract == DocumentProcessingInput.Pdf
                ? DocumentOcrProvenance.FromNative(result)
                : null;
            await TransitionAsync(result.Text, nativeMetadata).ConfigureAwait(false);
        }
        catch (RetainedProcessorException exception)
        {
            await FailAsync(exception.OutcomeCode, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("document-ocr-", StringComparison.Ordinal))
        {
            await FailAsync(exception.Message, null, cancellationToken).ConfigureAwait(false);
        }

        async ValueTask TransitionAsync(string text, string? documentMetadataJson) =>
            await transitions.TransitionAsync(
                    new StageTransitionRequest(
                        workItem.DispatchMessage,
                        workItem.Job,
                        new StageArtifact(
                            Guid.NewGuid(),
                            workItem.Job.Stage,
                            retained.ContentSha256,
                            "text/plain; charset=utf-8",
                            text,
                            timeProvider.GetUtcNow(),
                            documentMetadataJson),
                        PipelineStage.Normalise,
                        PipelineOperations.NormaliseText,
                        nameof(ExtractDocumentStageWorker)),
                    cancellationToken)
                .ConfigureAwait(false);

        async ValueTask FailAsync(string reason, string? details, CancellationToken token) =>
            await transitions.FailAsync(
                new StageFailureRequest(workItem.DispatchMessage, workItem.Job, reason, details, nameof(ExtractDocumentStageWorker)),
                token).ConfigureAwait(false);

        static bool TryGetOcrPageIndexes(
            DocumentExtractionResult result,
            out IReadOnlyList<int> pageIndexes)
        {
            pageIndexes = result.Pages
                .Where(static page => page.RequiresOcr)
                .Select(static page => page.PageIndex)
                .Order()
                .ToArray();
            return pageIndexes.Count != 0 &&
                   pageIndexes.Distinct().Count() == pageIndexes.Count &&
                   pageIndexes.All(static page => page >= 0);
        }
    }
}
