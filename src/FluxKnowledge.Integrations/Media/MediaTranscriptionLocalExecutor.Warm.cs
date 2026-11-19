using System.ComponentModel;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Media;

internal sealed record MediaTranscriptionWarmExecutionResult(MediaTranscriptionResult Result,
    OwnedWarmModelIdle? Idle, bool NativeReleaseConfirmed, bool CommandUnsent);

public sealed partial class MediaTranscriptionLocalExecutor
{
    internal ValueTask<OwnedWarmModelProcess> StartWarmProcessAsync(
        Guid residentId, string machineFingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MediaTranscriptionLocalRuntime.IsAvailable())
            throw new InvalidOperationException("transcription-runtime-unavailable");

        var directory = Path.Combine(layout.TempRoot, "media-transcription-runtime");
        var safety = new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance);
        safety.ValidateBeforeIo(directory);
        Directory.CreateDirectory(directory);
        safety.ValidateBeforeIo(directory);
        using var directoryLease = PhysicalFileIdentity.OpenDirectoryLease(directory);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OwnedWarmModelProcess.Start(
            CreateStartInfo(directory, "--warm-worker"), residentId, machineFingerprint));
    }

    internal ValueTask<MediaTranscriptionWarmExecutionResult> RunWarmAsync(
        SourceRevisionId sourceRevisionId, string contentSha256, DocumentOcrExecutionContext execution,
        OwnedWarmModelProcess process,
        Func<DocumentOcrExecutionLease, OwnedWarmModelCommand, CancellationToken, ValueTask<bool>> authorizeNativeStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(authorizeNativeStart);
        execution.Lease.Validate();
        if (execution.Lease.Handle.ExecutorKey != MediaTranscriptionLocalRuntime.Profile.ExecutorKey)
            throw new ArgumentException("transcription-warm-claim-invalid", nameof(execution));

        return RunCoreAsync(sourceRevisionId, contentSha256, execution, cancellationToken,
            (directory, token) => RunWarmProcessAsync(directory, execution.Lease, process, authorizeNativeStart, token));
    }

    private static async ValueTask<MediaTranscriptionWarmExecutionResult> RunWarmProcessAsync(
        string directory, DocumentOcrExecutionLease lease, OwnedWarmModelProcess process,
        Func<DocumentOcrExecutionLease, OwnedWarmModelCommand, CancellationToken, ValueTask<bool>> authorizeNativeStart,
        CancellationToken cancellationToken)
    {
        var nativeStartPermitted = false;
        var permissionRefused = false;
        var errorReason = "transcription-worker-interrupted";
        try
        {
            var idle = await process.RunAsync(lease.ExecutionInstanceId, Path.Combine(directory, "runtime.json"),
                Path.Combine(directory, "output.json"), lease.DeadlineAtUtc, async (command, token) =>
                {
                    nativeStartPermitted = command.Process.MachineFingerprint == lease.Supervisor.Process.MachineFingerprint &&
                        await authorizeNativeStart(lease, command, token).ConfigureAwait(false);
                    permissionRefused = !nativeStartPermitted;
                    return nativeStartPermitted;
                }, cancellationToken).ConfigureAwait(false);

            errorReason = "transcription-output-invalid";
            var file = await ContainedFileReader.ReadAsync(directory, "output.json", MaximumResultBytes, cancellationToken)
                .ConfigureAwait(false);
            var result = ParseResult(file.Bytes, out var validatedModel);
            if (idle.Model != validatedModel)
                throw new InvalidDataException("transcription-warm-model-mismatch");

            if (validatedModel is not null)
                return new(result, idle, false, false);

            // A refused transcription can be idle with no loaded model. The
            // native process is still charged until its Job is verified empty.
            return new(result, null, await TryStopWarmAsync(process).ConfigureAwait(false), false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (permissionRefused) errorReason = "transcription-native-start-refused";
            else if (cancellationToken.IsCancellationRequested) errorReason = "transcription-owner-interrupted";
            else if (error is OperationCanceledException or TimeoutException) errorReason = "transcription-runtime-timeout";
            var released = nativeStartPermitted && await TryStopWarmAsync(process).ConfigureAwait(false);
            return new(Refused(errorReason), null, released, CommandUnsent: !nativeStartPermitted);
        }
    }

    private static async ValueTask<bool> TryStopWarmAsync(OwnedWarmModelProcess process)
    {
        try { return await process.StopAndConfirmAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) { return false; }
    }
}
