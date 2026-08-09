using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

public sealed class ExtractUtf8StageWorker : IStageWorker
{
    public const string ChangedSourceReason =
        "source content changed before extraction; register a new revision";
    public const string InvalidRetainedSourceReason =
        "retained source artifact is missing or invalid; rescan the source root";

    private readonly IUtf8FileSourceReader _sourceReader;
    private readonly IRetainedSourceReader? _retainedSourceReader;
    private readonly IPipelineStageReader _pipelineReader;
    private readonly StageTransitionService _transitions;
    private readonly TimeProvider _timeProvider;

    public ExtractUtf8StageWorker(
        IUtf8FileSourceReader sourceReader,
        IPipelineStageReader pipelineReader,
        StageTransitionService transitions,
        TimeProvider timeProvider)
        : this(sourceReader, null, pipelineReader, transitions, timeProvider, true)
    {
    }

    public ExtractUtf8StageWorker(
        IUtf8FileSourceReader sourceReader,
        IRetainedSourceReader retainedSourceReader,
        IPipelineStageReader pipelineReader,
        StageTransitionService transitions,
        TimeProvider timeProvider)
        : this(sourceReader, retainedSourceReader, pipelineReader, transitions, timeProvider, true)
    {
    }

    private ExtractUtf8StageWorker(
        IUtf8FileSourceReader sourceReader,
        IRetainedSourceReader? retainedSourceReader,
        IPipelineStageReader pipelineReader,
        StageTransitionService transitions,
        TimeProvider timeProvider,
        bool _)
    {
        _sourceReader = sourceReader;
        _retainedSourceReader = retainedSourceReader;
        _pipelineReader = pipelineReader;
        _transitions = transitions;
        _timeProvider = timeProvider;
    }

    public string Operation => PipelineOperations.ExtractUtf8;

    public async ValueTask ExecuteAsync(
        StageWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var source = await _pipelineReader.ReadStageSourceAsync(
                workItem.Job.PipelineRecordId,
                workItem.Job.SourceRevision,
                workItem.Job.Stage,
                cancellationToken)
            .ConfigureAwait(false);
        Utf8FileSource current;
        if (source.RetainedSourceRevisionId is not null)
        {
            if (_retainedSourceReader is null)
            {
                await FailRetainedSourceAsync(workItem, "No retained-source reader is registered.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                current = await _retainedSourceReader.ReadUtf8Async(source.RetainedSourceRevisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                await FailRetainedSourceAsync(workItem, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
        try
        {
            current = await _sourceReader.ReadAsync(source.CanonicalPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await FailChangedSourceAsync(workItem, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
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

        await _transitions.TransitionAsync(
                new StageTransitionRequest(
                    workItem.DispatchMessage,
                    workItem.Job,
                    new StageArtifact(
                        Guid.NewGuid(),
                        workItem.Job.Stage,
                        current.ContentHash,
                        "text/plain; charset=utf-8",
                        current.Text,
                        _timeProvider.GetUtcNow()),
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
        _transitions.FailAsync(
            new StageFailureRequest(
                workItem.DispatchMessage,
                workItem.Job,
                ChangedSourceReason,
                errorDetails,
                nameof(ExtractUtf8StageWorker)),
            cancellationToken);

    private ValueTask FailRetainedSourceAsync(
        StageWorkItem workItem,
        string errorDetails,
        CancellationToken cancellationToken) =>
        _transitions.FailAsync(
            new StageFailureRequest(
                workItem.DispatchMessage,
                workItem.Job,
                InvalidRetainedSourceReason,
                errorDetails,
                nameof(ExtractUtf8StageWorker)),
            cancellationToken);
}
