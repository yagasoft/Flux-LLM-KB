using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

/// <summary>Uses the original extraction job for the durable GPU handoff and for publishing the verified transcript.</summary>
public sealed class TranscribeMediaStageWorker(IPipelineStageReader reader, IMediaTranscriptionGpuQueue queue,
    StageTransitionService transitions, TimeProvider clock, Func<bool> runtimeReady, ITranscriptSidecarPublisher sidecars) : IStageWorker
{
    public string Operation => MediaTranscriptionGpuContracts.Operation;


    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        var job = workItem.Job;
        var source = await reader.ReadStageSourceAsync(job.PipelineRecordId, job.SourceRevision, job.Stage, cancellationToken)
            .ConfigureAwait(false);
        if (source.RetainedSourceRevisionId is not { } revisionId || source.RegisteredContentHash.Length != 64)
        {
            await FailAsync("transcription-input-binding-invalid").ConfigureAwait(false);
            return;
        }

        var transcript = await queue.ReadCompletedAsync(job, revisionId, source.RegisteredContentHash, cancellationToken)
            .ConfigureAwait(false);
        if (transcript is null)
        {
            if (!runtimeReady())
            {
                await transitions.RetryAsync(new(workItem.DispatchMessage, job, clock.GetUtcNow().AddMinutes(1),
                    "transcription-runtime-unavailable", nameof(TranscribeMediaStageWorker)), cancellationToken).ConfigureAwait(false);
                return;
            }
            var handoff = await queue.QueueAsync(job, revisionId, source.RegisteredContentHash, cancellationToken).ConfigureAwait(false);
            if (!handoff.Scheduled)
                await FailAsync(handoff.ReasonCode).ConfigureAwait(false);
            return;
        }

        if (!transcript.Result.Succeeded)
        {
            await FailAsync(transcript.Result.ReasonCode).ConfigureAwait(false);
            return;
        }

        string receipt;
        try
        {
            receipt = await sidecars.PublishAsync(workItem, revisionId, source.RegisteredContentHash, transcript, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceSnapshotChangedException)
        {
            await FailAsync("transcription-sidecar-recording-changed").ConfigureAwait(false);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            await FailAsync("transcription-sidecar-access-denied").ConfigureAwait(false);
            return;
        }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        {
            await FailAsync("transcription-sidecar-access-denied").ConfigureAwait(false);
            return;
        }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } &&
            job.AttemptCount < 5)
        {
            await transitions.RetryAsync(new(workItem.DispatchMessage, job, clock.GetUtcNow().AddMinutes(1),
                "transcription-sidecar-file-in-use", nameof(TranscribeMediaStageWorker)), cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (IOException error)
        {
            var reason = error.Message is "transcription-sidecar-existing-file-conflicts" or
                "transcription-sidecar-source-policy" or "transcription-sidecar-output-unavailable"
                ? error.Message : "transcription-sidecar-save-failed";
            await FailAsync(reason).ConfigureAwait(false);
            return;
        }

        await transitions.TransitionAsync(new StageTransitionRequest(workItem.DispatchMessage, job,
            new StageArtifact(Guid.NewGuid(), job.Stage, source.RegisteredContentHash,
                MediaTranscriptionGpuContracts.SidecarReceiptContentType, "", clock.GetUtcNow(), receipt),
            null, null, nameof(TranscribeMediaStageWorker)), cancellationToken).ConfigureAwait(false);

        if (transcript.ExecutionId is { } executionId)
            sidecars.ReleasePrivateResult(executionId);

        async ValueTask FailAsync(string reason) =>
            await transitions.FailAsync(new StageFailureRequest(workItem.DispatchMessage, job, reason, null,
                nameof(TranscribeMediaStageWorker)), cancellationToken).ConfigureAwait(false);
    }
}
