using System.ComponentModel;
using System.Diagnostics;
using FluxKnowledge.Application.Documents;

namespace FluxKnowledge.Integrations.Documents;

internal sealed record OwnedOcrProcessOutcome(int? ExitCode, string? FailureReason, bool CleanupConfirmed);

/// <summary>Native execution cannot pass its stdin gate before containment and durable permission.</summary>
internal static class OwnedOcrProcessSupervisor
{
    internal const string StartMessage = "fluxknowledge-ocr-go-v1";

    internal static async ValueTask<OwnedOcrProcessOutcome> RunAsync(ProcessStartInfo start,
        DocumentOcrExecutionContext execution, CancellationToken cancellationToken)
    {
        var lease = execution.Lease;
        lease.Validate();
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var job = OwnedOcrJob.Create(lease.ExecutionInstanceId);
        using var process = new Process { StartInfo = start };
        using var drainCancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        DocumentOcrProcessIdentity? identity = null;
        var assigned = false;
        var cleanupConfirmed = false;
        int? exitCode = null;
        string? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return new(null, "document-ocr-runtime-launch-failed", false);
            identity = new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), lease.Supervisor.Process.MachineFingerprint);
            stdout = DrainAsync(process.StandardOutput, drainCancellation.Token);
            stderr = DrainAsync(process.StandardError, drainCancellation.Token);
            job.Assign(process); assigned = true;
            var permission = await execution.Observer.PermitNativeStartAsync(lease, identity, cancellationToken).ConfigureAwait(false);
            if (!permission.Accepted) failure = permission.ReasonCode;
            else
            {
                await process.StandardInput.WriteLineAsync(StartMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                var remaining = lease.DeadlineAtUtc - DateTimeOffset.UtcNow;
                deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
        }
        catch (OperationCanceledException)
        {
            failure = deadline.IsCancellationRequested || DateTimeOffset.UtcNow >= lease.DeadlineAtUtc
                ? "document-ocr-runtime-timeout" : "document-ocr-owner-interrupted";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            failure = "document-ocr-native-supervision-failed";
        }
        finally
        {
            deadline.Dispose();
            var cleanup = Stopwatch.StartNew();
            var empty = assigned && await job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            if (!assigned && identity is not null)
            {
                // The fixed child is still behind its gate. Never assert proof unless this exact handle exits.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                    empty = process.HasExited;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or OperationCanceledException) { }
            }
            var drainRemaining = TimeSpan.FromSeconds(30) - cleanup.Elapsed;
            if (drainRemaining > TimeSpan.FromSeconds(5)) drainRemaining = TimeSpan.FromSeconds(5);
            try
            {
                if (drainRemaining > TimeSpan.Zero)
                    await Task.WhenAll(stdout, stderr).WaitAsync(drainRemaining).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException) { }
            drainCancellation.Cancel();
            if (identity is not null)
            {
                process.StandardOutput.Close(); process.StandardError.Close();
            }
            _ = ObserveDrainAsync(stdout); _ = ObserveDrainAsync(stderr);
            if (empty)
            {
                var observed = DateTimeOffset.UtcNow;
                var proof = new DocumentOcrCleanupProof(lease.ExecutionInstanceId, DocumentOcrCleanupKind.OwnedJobEmpty, identity, observed);
                // Keep the exact empty job held while retrying an ambiguous proof commit, at most two minutes overall.
                while (cleanup.Elapsed < TimeSpan.FromMinutes(2))
                {
                    try
                    {
                        using var proofBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var confirmed = await execution.Observer.ConfirmCleanupAsync(lease, proof, proofBudget.Token).ConfigureAwait(false);
                        if (!confirmed.Accepted)
                            confirmed = await execution.Observer.ConfirmCleanupAsync(lease,
                                new(lease.ExecutionInstanceId, DocumentOcrCleanupKind.VerifiedUnstarted, null, observed), proofBudget.Token).ConfigureAwait(false);
                        cleanupConfirmed = confirmed.Accepted;
                        break;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
            }
        }
        return new(exitCode, failure, cleanupConfirmed);
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { }
    }
    private static async Task ObserveDrainAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
