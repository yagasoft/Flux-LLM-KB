using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Media;

/// <summary>Materialises one admitted retained input in the private runtime and runs it behind the native start gate.</summary>
public sealed partial class MediaTranscriptionLocalExecutor(LiveRootLayout layout, IRetainedSourceReader retained)
{
    private const string StartMessage = "fluxknowledge-transcription-go-v1";
    private const int MaximumResultBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private string PendingRoot => Path.Combine(layout.SpoolRoot, "PendingTranscripts");

    public async ValueTask PersistTranscriptAsync(Guid executionId, string text, CancellationToken token)
    {
        var length = StrictUtf8.GetByteCount(text);
        if (executionId == Guid.Empty || length is <= 0 or > MediaTranscriptionGpuContracts.MaximumResultBytes)
            throw new InvalidDataException("transcription-sidecar-output-invalid");
        var bytes = StrictUtf8.GetBytes(text);
        var safety = new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance);
        safety.ValidateBeforeIo(PendingRoot);
        Directory.CreateDirectory(PendingRoot);
        safety.ValidateBeforeIo(PendingRoot);
        using var directory = WindowsSourcePathAccess.OpenDirectoryPath(PendingRoot);
        var target = PendingName(executionId);
        try
        {
            await HandleRelativeNativeFileSystem.PublishTranscriptAsync(directory.Handle, $"{Guid.NewGuid():N}.tmp",
                target, bytes, directory.ValidateCurrent, token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            var saved = await ContainedFileReader.ReadAsync(PendingRoot, target,
                MediaTranscriptionGpuContracts.MaximumResultBytes, token, expectedLength: length).ConfigureAwait(false);
            if (!saved.Bytes.AsSpan().SequenceEqual(bytes)) throw;
        }
    }

    public async ValueTask<string> ReadPendingTranscriptAsync(Guid executionId, int length, string sha256, CancellationToken token)
    {
        new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance).ValidateBeforeIo(PendingRoot);
        var saved = await ContainedFileReader.ReadAsync(PendingRoot, PendingName(executionId),
            MediaTranscriptionGpuContracts.MaximumResultBytes, token, expectedLength: length).ConfigureAwait(false);
        if (saved.ContentSha256 != sha256) throw new IOException("transcription-sidecar-output-unavailable");
        return StrictUtf8.GetString(saved.Bytes);
    }

    public void RemovePendingTranscript(Guid executionId)
    {
        try
        {
            new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance).ValidateBeforeIo(PendingRoot);
            using var directory = WindowsSourcePathAccess.OpenDirectoryPath(PendingRoot);
            File.Delete(Path.Combine(PendingRoot, PendingName(executionId)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private static string PendingName(Guid id) => $"{id:N}.txt";

    public async ValueTask<MediaTranscriptionResult?> RunAsync(SourceRevisionId sourceRevisionId, string contentSha256,
        DocumentOcrExecutionContext execution, CancellationToken cancellationToken)
    {
        var outcome = await RunCoreAsync(sourceRevisionId, contentSha256, execution, cancellationToken).ConfigureAwait(false);
        return outcome.NativeReleaseConfirmed || outcome.CommandUnsent ? outcome.Result : null;
    }

    private async ValueTask<MediaTranscriptionWarmExecutionResult> RunCoreAsync(
        SourceRevisionId sourceRevisionId, string contentSha256, DocumentOcrExecutionContext execution,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask<MediaTranscriptionWarmExecutionResult>>? runWarmProcess = null)
    {
        string? directory = null;
        var mayHaveStarted = false;
        var cleanupConfirmed = false;
        var requestIdle = false;
        var keepResult = false;
        try
        {
            if (runWarmProcess is null && !MediaTranscriptionLocalRuntime.IsAvailable())
                return new(Refused("transcription-runtime-unavailable"), null, false, true);

            var safety = new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance);
            safety.ValidateBeforeIo(layout.TempRoot);
            var parent = Path.Combine(layout.TempRoot, "media-transcription");
            Directory.CreateDirectory(parent);
            safety.ValidateBeforeIo(parent);
            var workDirectory = Path.Combine(parent, execution.Lease.ExecutionInstanceId.ToString("N"));
            if (Directory.Exists(workDirectory) || File.Exists(workDirectory))
                return new(Refused("transcription-work-directory-exists"), null, false, true);
            Directory.CreateDirectory(workDirectory);
            safety.ValidateBeforeIo(workDirectory);
            directory = workDirectory;
            using var directoryLease = PhysicalFileIdentity.OpenDirectoryLease(directory);
            await using var input = await retained.OpenReadAsync(sourceRevisionId, MediaTranscriptionDiscoveryOptions.MaximumMediaBytes, cancellationToken)
                .ConfigureAwait(false);
            if (input.SourceRevisionId != sourceRevisionId || input.ContentSha256 != contentSha256)
                return new(Refused("transcription-input-binding-invalid"), null, false, true);
            await using (var copy = new FileStream(Path.Combine(directory, "input.media"), FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await input.Stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);

            var manifest = Path.Combine(directory, "runtime.json");
            await using (var stream = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, MediaTranscriptionLocalRuntime.Manifest, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            if (runWarmProcess is not null)
            {
                mayHaveStarted = true;
                var result = await runWarmProcess(directory, cancellationToken).ConfigureAwait(false);
                mayHaveStarted = !result.CommandUnsent;
                cleanupConfirmed = result.NativeReleaseConfirmed;
                requestIdle = result.Idle is not null;
                keepResult = requestIdle;
                return result;
            }

            var start = CreateStartInfo(directory, "--manifest", manifest, "--output", Path.Combine(directory, "output.json"));
            keepResult = true;
            mayHaveStarted = true;
            var outcome = await OwnedOcrProcessSupervisor.RunAsync(start, execution, cancellationToken, StartMessage)
                .ConfigureAwait(false);
            cleanupConfirmed = outcome.CleanupConfirmed;
            if (!cleanupConfirmed) return new(Refused("transcription-worker-interrupted"), null, false, false);
            if (outcome.ExitCode != 0 || outcome.FailureReason is not null)
                return new(Refused("transcription-worker-interrupted"), null, true, false);
            var file = await ContainedFileReader.ReadAsync(directory, "output.json", MaximumResultBytes, cancellationToken)
                .ConfigureAwait(false);
            return new(ParseResult(file.Bytes), null, true, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
                                          ArgumentException or OperationCanceledException)
        {
            return new(Refused("transcription-execution-unavailable"), null, cleanupConfirmed, !mayHaveStarted);
        }
        finally
        {
            if (directory is not null && (!mayHaveStarted || cleanupConfirmed || requestIdle))
                RemoveFiles(directory, keepResult);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(MediaTranscriptionLocalRuntime.Python) { WorkingDirectory = directory };
        foreach (var argument in new[] { "-I", "-S", "-B", MediaTranscriptionLocalRuntime.Script })
            start.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // The provider receives no application connection strings, tokens or user Python paths.
        start.Environment.Clear();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["TEMP"] = directory;
        start.Environment["TMP"] = directory;
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HOME"] = @"J:\Models\provider-cache\huggingface";
        start.Environment["PIP_NO_INDEX"] = "1";
        return start;
    }

    public void RemoveSettledFiles(Guid executionInstanceId) => RemoveFiles(
        Path.Combine(layout.TempRoot, "media-transcription", executionInstanceId.ToString("N")), keepResult: false);

    public async ValueTask<MediaTranscriptionResult?> ReadRecoveredResultAsync(Guid executionInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(layout.TempRoot, "media-transcription", executionInstanceId.ToString("N"));
            if (!Directory.Exists(directory)) return null;
            var result = await ContainedFileReader.ReadAsync(directory, "output.json", MaximumResultBytes, cancellationToken)
                .ConfigureAwait(false);
            return ParseResult(result.Bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private void RemoveFiles(string directory, bool keepResult)
    {
        try
        {
            new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance).ValidateBeforeIo(directory);
            File.Delete(Path.Combine(directory, "input.media"));
            File.Delete(Path.Combine(directory, "runtime.json"));
            if (!keepResult)
            {
                File.Delete(Path.Combine(directory, "output.json"));
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    internal static MediaTranscriptionResult ParseResult(ReadOnlyMemory<byte> bytes) => ParseResult(bytes, out _);

    private static MediaTranscriptionResult ParseResult(ReadOnlyMemory<byte> bytes, out string? validatedModel)
    {
        validatedModel = null;
        try
        {
            using var response = JsonDocument.Parse(bytes);
            var root = response.RootElement;
            if (!root.GetProperty("succeeded").GetBoolean())
            {
                var reason = root.GetProperty("reasonCode").GetString();
                return Refused(reason is "transcription-recording-too-long" or "transcription-no-audio-track" or
                    "transcription-no-speech-found" or "transcription-runtime-unavailable" or "transcription-recognition-failed"
                    or "transcription-output-too-large" or "transcription-model-assets-unavailable" or "transcription-external-media-refused"
                    ? reason : "transcription-output-invalid");
            }

            var result = root.GetProperty("result");
            var profile = result.GetProperty("profile").GetString();
            var selectionReason = result.GetProperty("selectionReason").GetString();
            var language = result.GetProperty("language").GetString();
            var confidence = result.GetProperty("languageConfidence").GetDouble();
            var duration = result.GetProperty("durationMillis").GetInt32();
            var cues = result.GetProperty("cues");
            var evidence = result.GetProperty("languageEvidence");
            if (profile is not ("general" or "egyptian-ar-en") ||
                selectionReason is not ("arabic-english" or "arabic-speech" or "other-language" or "language-unconfirmed") ||
                !LanguageIsValid(language) || !ValidConfidence(confidence) || duration is <= 0 or > 7_200_000 ||
                cues.GetArrayLength() is < 1 or > 10_000 || evidence.GetArrayLength() > 5 ||
                profile == "egyptian-ar-en" && (language != "ar" || selectionReason is not ("arabic-english" or "arabic-speech")) ||
                profile == "general" && selectionReason is not ("other-language" or "language-unconfirmed"))
                return Refused("transcription-output-invalid");

            var samples = new List<object>(evidence.GetArrayLength());
            foreach (var sample in evidence.EnumerateArray())
            {
                var detected = sample.GetProperty("language").GetString();
                var probability = sample.GetProperty("confidence").GetDouble();
                if (!LanguageIsValid(detected) || !ValidConfidence(probability))
                    return Refused("transcription-output-invalid");
                samples.Add(new { language = detected, confidence = probability });
            }
            var lines = new List<string>(cues.GetArrayLength());
            var positions = new List<object>(cues.GetArrayLength());
            var previousStart = -1;
            var characters = 0;
            foreach (var cue in cues.EnumerateArray())
            {
                var start = cue.GetProperty("startMillis").GetInt32();
                var end = cue.GetProperty("endMillis").GetInt32();
                var text = cue.GetProperty("text").GetString();
                if (start < 0 || end <= start || end > duration || start < previousStart || string.IsNullOrWhiteSpace(text) ||
                    (characters += text.Length) > 1_000_000)
                    return Refused("transcription-output-invalid");
                previousStart = start;
                var timing = TimeSpan.FromMilliseconds(start).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                lines.Add($"[{timing}] {text}");
                positions.Add(new { startMillis = start, endMillis = end });
            }

            var metadata = JsonSerializer.Serialize(new { schema = MediaTranscriptionGpuContracts.MetadataSchema, profile,
                selectionReason, language, confidence, durationMillis = duration, languageEvidence = samples, cues = positions,
                reviewStatus = "unreviewed", source = "machine-generated" });
            var completion = new MediaTranscriptionResult(true, "transcription-complete", string.Join('\n', lines), metadata);
            var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            if (JsonSerializer.Serialize(completion, jsonOptions).Length * sizeof(char) > MediaTranscriptionGpuContracts.MaximumResultBytes)
                return Refused("transcription-output-too-large");
            validatedModel = profile;
            return completion;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Refused("transcription-output-invalid");
        }
    }

    private static bool LanguageIsValid(string? value) =>
        value is { Length: >= 2 and <= 3 } && value.All(char.IsAsciiLetterLower);
    private static bool ValidConfidence(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static MediaTranscriptionResult Refused(string reason) => new(false, reason, null, null);
}
