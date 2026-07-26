using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

public sealed class ExtractUtf8StageWorker(
    IUtf8FileSourceReader sourceReader,
    IPipelineStageReader pipelineReader,
    StageTransitionService transitions,
    TimeProvider timeProvider) : IStageWorker
{
    public const string ChangedSourceReason =
        "source content changed before extraction; register a new revision";

    public string Operation => PipelineOperations.ExtractUtf8;

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
        Utf8FileSource current;
        try
        {
            current = await sourceReader.ReadAsync(source.CanonicalPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await FailChangedSourceAsync(workItem, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(
                current.ContentHash,
                source.RegisteredContentHash,
                StringComparison.Ordinal))
        {
            await FailChangedSourceAsync(workItem, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await transitions.TransitionAsync(
                new StageTransitionRequest(
                    workItem.DispatchMessage,
                    workItem.Job,
                    new StageArtifact(
                        Guid.NewGuid(),
                        workItem.Job.Stage,
                        current.ContentHash,
                        "text/plain; charset=utf-8",
                        current.Text,
                        timeProvider.GetUtcNow()),
                    PipelineStage.Normalise,
                    PipelineOperations.NormaliseText,
                    nameof(ExtractUtf8StageWorker)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask FailChangedSourceAsync(
        StageWorkItem workItem,
        string? errorDetails,
        CancellationToken cancellationToken) =>
        transitions.FailAsync(
            new StageFailureRequest(
                workItem.DispatchMessage,
                workItem.Job,
                ChangedSourceReason,
                errorDetails,
                nameof(ExtractUtf8StageWorker)),
            cancellationToken);
}
