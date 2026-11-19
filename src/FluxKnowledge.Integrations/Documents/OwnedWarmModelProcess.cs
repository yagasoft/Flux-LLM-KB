using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;

namespace FluxKnowledge.Integrations.Documents;

internal sealed record OwnedWarmModelCommand(Guid ResidentId, Guid ExecutionId, DocumentOcrProcessIdentity Process);
internal sealed record OwnedWarmModelIdle(Guid ResidentId, Guid ExecutionId, DocumentOcrProcessIdentity Process, string? Model);

/// <summary>A serial command transport for one non-breakaway native model helper.</summary>
internal sealed class OwnedWarmModelProcess : IAsyncDisposable
{
    private const int MaximumAcknowledgementChars = 4096;
    private const int MaximumRequestBytes = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly OwnedOcrJob _job;
    private readonly Process _process;
    private readonly DocumentOcrProcessIdentity _identity;
    private readonly Guid _residentId;
    private readonly SemaphoreSlim _commandGate = new(1);
    private readonly CancellationTokenSource _stderrCancellation = new();
    private readonly Task _stderr;
    private bool _retiring;
    private bool _empty;
    private int _disposed;

    internal DocumentOcrProcessIdentity Identity => _identity;

    private OwnedWarmModelProcess(OwnedOcrJob job, Process process, Guid residentId, DocumentOcrProcessIdentity identity)
    {
        _job = job;
        _process = process;
        _residentId = residentId;
        _identity = identity;
        _stderr = DrainErrorsAsync(process.StandardError, _stderrCancellation.Token);
    }

    internal static OwnedWarmModelProcess Start(ProcessStartInfo start, Guid residentId, string machineFingerprint)
    {
        if (machineFingerprint.Length != 64 || machineFingerprint.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
            throw new ArgumentException("warm-model-machine-identity-invalid", nameof(machineFingerprint));

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = Utf8;
        start.StandardOutputEncoding = Utf8;
        // Provider diagnostics are separate from the protocol and discarded.
        start.StandardErrorEncoding = Encoding.UTF8;
        var job = OwnedOcrJob.Create(residentId);
        var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("warm-model-helper-unstarted");
            started = true;
            job.Assign(process);
            var identity = new DocumentOcrProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), machineFingerprint);
            return new(job, process, residentId, identity);
        }
        catch
        {
            // The child has received no command, including when assignment fails.
            try
            {
                if (started && !process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            finally { job.Dispose(); process.Dispose(); }
            throw;
        }
    }

    internal async ValueTask<OwnedWarmModelIdle> RunAsync(Guid executionId, string manifest, string output,
        DateTimeOffset deadline, Func<OwnedWarmModelCommand, CancellationToken, ValueTask<bool>> authorizeNativeStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizeNativeStart);
        if (executionId == Guid.Empty || !Path.IsPathFullyQualified(manifest) || !Path.IsPathFullyQualified(output) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(manifest)),
                Path.GetDirectoryName(Path.GetFullPath(output)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("warm-model-command-binding-invalid");
        var request = JsonSerializer.Serialize(new { operation = "run", execution_id = executionId.ToString("N"), manifest, output });
        if (Utf8.GetByteCount(request) > MaximumRequestBytes) throw new ArgumentException("warm-model-command-too-large");

        if (!await _commandGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("warm-model-helper-busy");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_retiring || _process.HasExited) throw new InvalidOperationException("warm-model-helper-unavailable");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("warm-model-job-expired");
            budget.CancelAfter(remaining);
            var token = budget.Token;
            if (!await authorizeNativeStart(new(_residentId, executionId, _identity), token).ConfigureAwait(false))
                throw new InvalidOperationException("warm-model-command-refused");
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("warm-model-job-expired");

            try
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                var acknowledgement = await ReadLineAsync(token).ConfigureAwait(false);
                var model = ParseIdleAcknowledgement(acknowledgement, executionId);
                if (_process.HasExited) throw new IOException("warm-model-helper-exited");
                return new(_residentId, executionId, _identity, model);
            }
            catch
            {
                // Neither a malformed frame nor an interrupted run allows reuse.
                _retiring = true;
                _empty = await _job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _commandGate.Release(); }
    }

    internal async ValueTask<bool> StopAndConfirmAsync(CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_empty) return true;
            if (!_retiring)
            {
                _retiring = true;
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    if (!_process.HasExited)
                    {
                        await WriteAsync("""{"operation":"shutdown"}""", grace.Token).ConfigureAwait(false);
                        await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
            }
            _empty = await _job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return _empty;
        }
        finally { _commandGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!await StopAndConfirmAsync(CancellationToken.None).ConfigureAwait(false))
                throw new IOException("warm-model-native-release-unconfirmed");
        }
        finally
        {
            _stderrCancellation.Cancel();
            _job.Dispose();
            _process.Dispose();
            await _stderr.ConfigureAwait(false);
            _stderrCancellation.Dispose();
        }
    }

    private async Task WriteAsync(string message, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var single = new char[1];
        while (line.Length < MaximumAcknowledgementChars)
        {
            if (await _process.StandardOutput.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("warm-model-helper-exited");
            if (single[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append(single[0]);
        }
        throw new InvalidDataException("warm-model-acknowledgement-too-large");
    }

    private static string? ParseIdleAcknowledgement(string line, Guid executionId)
    {
        try
        {
            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 2, AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 3 &&
                root.GetProperty("execution_id").GetString() == executionId.ToString("N") &&
                root.GetProperty("status").GetString() == "idle")
            {
                var model = root.GetProperty("model").GetString();
                if (model is null or "ocr" or "general" or "egyptian-ar-en") return model;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("warm-model-acknowledgement-invalid", error);
        }
        throw new InvalidDataException("warm-model-acknowledgement-invalid");
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
