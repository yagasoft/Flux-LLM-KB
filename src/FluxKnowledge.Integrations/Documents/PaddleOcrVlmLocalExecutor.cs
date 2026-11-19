using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Integrations.Models;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>
/// The fixed local-only PaddleOCR-VL executor. It writes only selected rasterized pages to a
/// short-lived app-owned directory, runs the fixed J: runtime, and accepts a bounded JSON
/// result. It has no provider selection, input paths from callers, or model acquisition path.
/// </summary>
public sealed class PaddleOcrVlmLocalExecutor : IDocumentOcrExecutor
{
    private const int MaximumResultBytes = 4 * 1024 * 1024;
    private const int MaximumPagePngBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPdfPageRasterizer _rasterizer;
    private readonly PaddleOcrVlmModelGate _modelGate;
    private readonly LiveRootLayout _layout;
    private readonly IPaddleOcrVlmProcessRunner _processRunner;

    public PaddleOcrVlmLocalExecutor(
        IPdfPageRasterizer rasterizer,
        PaddleOcrVlmModelGate modelGate,
        LiveRootLayout layout)
        : this(rasterizer, modelGate, layout, new PaddleOcrVlmProcessRunner())
    {
    }

    internal PaddleOcrVlmLocalExecutor(
        IPdfPageRasterizer rasterizer,
        PaddleOcrVlmModelGate modelGate,
        LiveRootLayout layout,
        IPaddleOcrVlmProcessRunner processRunner)
    {
        _rasterizer = rasterizer ?? throw new ArgumentNullException(nameof(rasterizer));
        _modelGate = modelGate ?? throw new ArgumentNullException(nameof(modelGate));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async ValueTask<DocumentOcrExecutionResult> ExecuteAsync(
        RetainedSourceBytes retained,
        IReadOnlyList<int> pageIndexes,
        DocumentOcrExecutionContext execution,
        CancellationToken cancellationToken) =>
        (await ExecuteCoreAsync(retained, pageIndexes, execution, cancellationToken).ConfigureAwait(false)).Result;

    internal async ValueTask<OwnedWarmModelProcess> StartWarmProcessAsync(
        Guid residentId, string machineFingerprint, CancellationToken cancellationToken)
    {
        var verified = await _modelGate.EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Succeeded) throw new InvalidOperationException(verified.ReasonCode);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(_layout.TempRoot, "document-ocr-runtime");
        var safety = new LiveRootStorageSafety(_layout, FileSystemLiveRootPathInspector.Instance);
        safety.ValidateBeforeIo(directory);
        Directory.CreateDirectory(directory);
        safety.ValidateBeforeIo(directory);

        return PaddleOcrVlmProcessRunner.StartWarm(directory, residentId, machineFingerprint);
    }

    internal ValueTask<PaddleOcrWarmExecutionResult> ExecuteWarmAsync(
        RetainedSourceBytes retained,
        IReadOnlyList<int> pageIndexes,
        DocumentOcrExecutionContext execution,
        OwnedWarmModelProcess process,
        Func<DocumentOcrExecutionLease, OwnedWarmModelCommand, CancellationToken, ValueTask<bool>> authorizeNativeStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(authorizeNativeStart);
        if (execution.Lease.Handle.CapacitySlotKey != PaddleOcrVlmRuntimeContract.CapacitySlotKey ||
            execution.Lease.Handle.ExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey)
            throw new ArgumentException("document-ocr-warm-claim-invalid", nameof(execution));

        return ExecuteCoreAsync(retained, pageIndexes, execution, cancellationToken,
            (request, token) => PaddleOcrVlmProcessRunner.RunWarmAsync(request, process, authorizeNativeStart, token));
    }

    private async ValueTask<PaddleOcrWarmExecutionResult> ExecuteCoreAsync(
        RetainedSourceBytes retained,
        IReadOnlyList<int> pageIndexes,
        DocumentOcrExecutionContext execution,
        CancellationToken cancellationToken,
        Func<PaddleOcrVlmProcessRequest, CancellationToken, ValueTask<PaddleOcrVlmProcessResult>>? runWarmProcess = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        execution.Lease.Validate();
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        if (pageIndexes.Count == 0 || pageIndexes.Any(static index => index < 0) ||
            pageIndexes.Distinct().Count() != pageIndexes.Count)
        {
            return new(Refused("document-ocr-page-selection-invalid"), null, false);
        }

        var verified = await _modelGate.EnsureVerifiedAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Succeeded)
        {
            return new(Refused(verified.ReasonCode), null, false);
        }

        string? workDirectory = null;
        var result = Refused("document-ocr-execution-failed");
        var cleanupFailed = false;
        var nativeCleanupUnproven = false;
        var nativeReleaseConfirmed = false;
        OwnedWarmModelIdle? idle = null;
        var phase = "temporary-storage";
        try
        {
            workDirectory = CreateWorkDirectory();
            using var directoryLease = runWarmProcess is null ? null : PhysicalFileIdentity.OpenDirectoryLease(workDirectory);
            var expectedPages = pageIndexes.Order().ToArray();
            phase = "rasterization";
            var rasters = await _rasterizer.RenderAsync(
                    retained,
                    cancellationToken,
                    expectedPages.ToHashSet())
                .ConfigureAwait(false);
            if (rasters is null || rasters.Count != expectedPages.Length ||
                !rasters.Select(static page => page.PageIndex).Order().SequenceEqual(expectedPages) ||
                rasters.Any(static page => page.PngBytes is null or { Length: 0 } || page.PngBytes.Length > MaximumPagePngBytes))
            {
                result = Refused("document-ocr-raster-result-invalid");
            }
            else
            {
                var imageFilenames = new List<string>(rasters.Count);
                phase = "page-write";
                foreach (var raster in rasters.OrderBy(static page => page.PageIndex))
                {
                    var filename = $"page-{raster.PageIndex:D8}.png";
                    await File.WriteAllBytesAsync(Path.Combine(workDirectory, filename), raster.PngBytes, cancellationToken)
                    .ConfigureAwait(false);
                    imageFilenames.Add(filename);
                }

                var inputPath = Path.Combine(workDirectory, "input.json");
                var outputPath = Path.Combine(workDirectory, "output.json");
                phase = "input-write";
                await using (var input = new FileStream(
                                 inputPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                            input,
                            new PaddleOcrVlmWireInput(
                                rasters.OrderBy(static page => page.PageIndex)
                                    .Select(page => new PaddleOcrVlmWireInputPage(
                                        page.PageIndex,
                                        $"page-{page.PageIndex:D8}.png"))
                                    .ToArray()),
                            JsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                phase = "provider-execution";
                nativeCleanupUnproven = runWarmProcess is not null;
                var runProcess = runWarmProcess ?? _processRunner.RunAsync;
                var processResult = await runProcess(
                        new PaddleOcrVlmProcessRequest(
                            workDirectory,
                            inputPath,
                            outputPath,
                            expectedPages,
                            imageFilenames,
                            execution),
                        cancellationToken)
                    .ConfigureAwait(false);
                idle = processResult.Idle;
                nativeReleaseConfirmed = processResult.CleanupConfirmed;
                nativeCleanupUnproven = !nativeReleaseConfirmed && idle is null && !processResult.CommandUnsent;
                phase = "provider-result";
                result = processResult.Succeeded
                    ? await ReadResultAsync(
                        outputPath,
                        expectedPages,
                        rasters.ToDictionary(static page => page.PageIndex),
                        cancellationToken,
                        requireContainedOutput: idle is not null).ConfigureAwait(false)
                    : Refused(processResult.ReasonCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (runWarmProcess is null) throw;
            result = Refused("document-ocr-owner-interrupted");
        }
        catch (RetainedProcessorException exception)
        {
            result = Refused(exception.OutcomeCode is { Length: > 0 and <= 120 }
                ? exception.OutcomeCode
                : "document-ocr-rasterization-failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            result = Refused($"document-ocr-{phase}-failed");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            result = Refused("document-ocr-execution-failed");
        }
        finally
        {
            if (workDirectory is not null && !nativeCleanupUnproven)
            {
                cleanupFailed = !TryDeleteWorkDirectory(workDirectory);
            }
        }

        if (cleanupFailed) result = Refused("document-ocr-temporary-cleanup-failed");
        return new(result, idle, nativeReleaseConfirmed);
    }

    private string CreateWorkDirectory()
    {
        if (!Directory.Exists(_layout.TempRoot))
        {
            throw new IOException("document-ocr-temp-root-unavailable");
        }

        var safety = new LiveRootStorageSafety(_layout, FileSystemLiveRootPathInspector.Instance);
        safety.ValidateBeforeIo(_layout.TempRoot);
        var parent = Path.Combine(_layout.TempRoot, "document-ocr");
        var workDirectory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        if (!_layout.IsOwnedPath(parent) || !_layout.IsOwnedPath(workDirectory))
        {
            throw new InvalidDataException("document-ocr-temp-path-invalid");
        }

        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(workDirectory);
        safety.ValidateBeforeIo(workDirectory);
        return workDirectory;
    }

    private bool TryDeleteWorkDirectory(string workDirectory)
    {
        try
        {
            if (!_layout.IsOwnedPath(workDirectory) || !Directory.Exists(workDirectory))
            {
                return false;
            }

            new LiveRootStorageSafety(_layout, FileSystemLiveRootPathInspector.Instance).ValidateBeforeIo(workDirectory);
            Directory.Delete(workDirectory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private static async ValueTask<DocumentOcrExecutionResult> ReadResultAsync(
        string outputPath,
        IReadOnlyList<int> expectedPages,
        IReadOnlyDictionary<int, PdfRasterizedPage> rasters,
        CancellationToken cancellationToken,
        bool requireContainedOutput)
    {
        byte[] bytes;
        if (requireContainedOutput)
        {
            try
            {
                var output = await ContainedFileReader.ReadAsync(Path.GetDirectoryName(outputPath)!, "output.json",
                    MaximumResultBytes, cancellationToken).ConfigureAwait(false);
                bytes = output.Bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Refused("document-ocr-provider-result-invalid");
            }
        }
        else
        {
            var output = new FileInfo(outputPath);
            if (!output.Exists || output.Length is < 1 or > MaximumResultBytes)
            {
                return Refused("document-ocr-provider-result-invalid");
            }
            bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Refused("document-ocr-provider-result-invalid");
        }

        PaddleOcrVlmWireOutput? wire;
        try
        {
            wire = JsonSerializer.Deserialize<PaddleOcrVlmWireOutput>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return Refused("document-ocr-provider-result-invalid");
        }

        if (wire is null || string.IsNullOrWhiteSpace(wire.ReasonCode) || wire.ReasonCode.Length > 128)
        {
            return Refused("document-ocr-provider-result-invalid");
        }

        if (!wire.Succeeded)
        {
            return Refused(PaddleOcrVlmProcessRunner.IsKnownFailureReason(wire.ReasonCode)
                ? wire.ReasonCode
                : "document-ocr-provider-result-invalid");
        }

        if (!string.Equals(wire.ReasonCode, "document-ocr-complete", StringComparison.Ordinal) || wire.Pages is null ||
            wire.Pages.Count != expectedPages.Count ||
            !wire.Pages.Select(static page => page.PageIndex).Order().SequenceEqual(expectedPages))
        {
            return Refused("document-ocr-provider-result-invalid");
        }

        var pages = new List<DocumentOcrPageResult>(wire.Pages.Count);
        foreach (var page in wire.Pages.OrderBy(static page => page.PageIndex))
        {
            if (page.OrientationDegrees is not (0 or 90 or 180 or 270) || page.Blocks is null ||
                page.Blocks.Count > DocumentOcrProvenance.MaximumBlocksPerPage)
            {
                return Refused("document-ocr-provider-result-invalid");
            }

            var blocks = new List<DocumentOcrBlock>(page.Blocks.Count);
            foreach (var block in page.Blocks)
            {
                if (block is null || block.Kind is null || block.Text is null ||
                    block.Kind is not ("text" or "table" or "title" or "header" or "footer" or "figure") ||
                    block.Left < 0 || block.Top < 0 || block.Width <= 0 || block.Height <= 0)
                {
                    return Refused("document-ocr-provider-result-invalid");
                }

                try
                {
                    _ = StrictUtf8.GetByteCount(block.Text);
                }
                catch (EncoderFallbackException)
                {
                    return Refused("document-ocr-provider-result-invalid");
                }

                blocks.Add(new DocumentOcrBlock(
                    block.Kind,
                    block.Left,
                    block.Top,
                    block.Width,
                    block.Height,
                    block.Text));
            }

            if (!rasters.TryGetValue(page.PageIndex, out var raster))
            {
                return Refused("document-ocr-provider-result-invalid");
            }
            pages.Add(new DocumentOcrPageResult(
                page.PageIndex,
                (page.OrientationDegrees + raster.SourceOrientationDegrees) % 360,
                blocks,
                raster.SourceWidth,
                raster.SourceHeight,
                raster.SourceTransform));
        }

        return new DocumentOcrExecutionResult(true, "document-ocr-complete", pages);
    }

    private static DocumentOcrExecutionResult Refused(string reasonCode) =>
        new(false,
            string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128
                ? "document-ocr-execution-failed"
                : reasonCode,
            []);

    private sealed record PaddleOcrVlmWireInput(IReadOnlyList<PaddleOcrVlmWireInputPage> Pages);
    private sealed record PaddleOcrVlmWireInputPage(int PageIndex, string Image);
    private sealed record PaddleOcrVlmWireOutput(bool Succeeded, string? ReasonCode, IReadOnlyList<PaddleOcrVlmWirePage>? Pages);
    private sealed record PaddleOcrVlmWirePage(int PageIndex, int OrientationDegrees, IReadOnlyList<PaddleOcrVlmWireBlock>? Blocks);
    private sealed record PaddleOcrVlmWireBlock(string? Kind, int Left, int Top, int Width, int Height, string? Text);
}

internal interface IPaddleOcrVlmProcessRunner
{
    ValueTask<PaddleOcrVlmProcessResult> RunAsync(
        PaddleOcrVlmProcessRequest request,
        CancellationToken cancellationToken);
}

internal sealed record PaddleOcrVlmProcessRequest(
    string WorkingDirectory,
    string InputPath,
    string OutputPath,
    IReadOnlyList<int> PageIndexes,
    IReadOnlyList<string> ImageFilenames,
    DocumentOcrExecutionContext Execution);

internal sealed record PaddleOcrWarmExecutionResult(
    DocumentOcrExecutionResult Result, OwnedWarmModelIdle? Idle, bool NativeReleaseConfirmed);

internal sealed record PaddleOcrVlmProcessResult(bool Succeeded, string ReasonCode, bool CleanupConfirmed = true,
    OwnedWarmModelIdle? Idle = null, bool CommandUnsent = false)
{
    public static PaddleOcrVlmProcessResult Completed { get; } = new(true, "document-ocr-complete");
}

internal sealed class PaddleOcrVlmProcessRunner : IPaddleOcrVlmProcessRunner
{

    public async ValueTask<PaddleOcrVlmProcessResult> RunAsync(
        PaddleOcrVlmProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(PaddleOcrVlmPythonScript.Path))
        {
            return new PaddleOcrVlmProcessResult(false, "document-ocr-runtime-script-missing");
        }

        var startInfo = CreateStartInfo(request.WorkingDirectory);
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(request.InputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(request.OutputPath);
        startInfo.ArgumentList.Add("--startup-gate");
        var outcome = await OwnedOcrProcessSupervisor.RunAsync(startInfo, request.Execution, cancellationToken).ConfigureAwait(false);
        if (outcome.FailureReason is not null) return new(false, outcome.FailureReason, outcome.CleanupConfirmed);
        if (outcome.ExitCode == 0) return new(true, "document-ocr-complete", outcome.CleanupConfirmed);
        var failure = await ReadFailureReasonAsync(request.WorkingDirectory, request.OutputPath, cancellationToken).ConfigureAwait(false);
        return new(false, failure == "document-ocr-runtime-failed" ? RuntimeExitReason(outcome.ExitCode ?? -1) : failure, outcome.CleanupConfirmed);
    }
    internal static OwnedWarmModelProcess StartWarm(string workingDirectory, Guid residentId, string machineFingerprint)
    {
        if (!File.Exists(PaddleOcrVlmPythonScript.Path))
            throw new InvalidOperationException("document-ocr-runtime-script-missing");
        var startInfo = CreateStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("--warm-worker");
        startInfo.Environment["TEMP"] = workingDirectory;
        startInfo.Environment["TMP"] = workingDirectory;
        startInfo.Environment["TMPDIR"] = workingDirectory;
        return OwnedWarmModelProcess.Start(startInfo, residentId, machineFingerprint);
    }

    internal static async ValueTask<PaddleOcrVlmProcessResult> RunWarmAsync(
        PaddleOcrVlmProcessRequest request,
        OwnedWarmModelProcess process,
        Func<DocumentOcrExecutionLease, OwnedWarmModelCommand, CancellationToken, ValueTask<bool>> authorizeNativeStart,
        CancellationToken cancellationToken)
    {
        var lease = request.Execution.Lease;
        var nativeStartPermitted = false;
        var permissionRefused = false;
        try
        {
            var idle = await process.RunAsync(lease.ExecutionInstanceId, request.InputPath, request.OutputPath,
                lease.DeadlineAtUtc, async (command, token) =>
                {
                    nativeStartPermitted = command.Process.MachineFingerprint == lease.Supervisor.Process.MachineFingerprint &&
                        await authorizeNativeStart(lease, command, token).ConfigureAwait(false);
                    permissionRefused = !nativeStartPermitted;
                    return nativeStartPermitted;
                }, cancellationToken).ConfigureAwait(false);
            if (idle.Model != "ocr") throw new InvalidDataException("document-ocr-warm-model-mismatch");

            return new(true, "document-ocr-complete", CleanupConfirmed: false, Idle: idle);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var released = nativeStartPermitted && await TryStopWarmAsync(process).ConfigureAwait(false);
            var reason = "document-ocr-native-supervision-failed";
            if (permissionRefused) reason = "document-ocr-native-start-refused";
            else if (cancellationToken.IsCancellationRequested) reason = "document-ocr-owner-interrupted";
            else if (error is OperationCanceledException or TimeoutException) reason = "document-ocr-runtime-timeout";
            else if (error is EndOfStreamException && released)
                reason = await ReadFailureReasonAsync(request.WorkingDirectory, request.OutputPath, CancellationToken.None)
                    .ConfigureAwait(false);
            return new(false, reason, released, CommandUnsent: !nativeStartPermitted);
        }
    }

    private static async ValueTask<bool> TryStopWarmAsync(OwnedWarmModelProcess process)
    {
        try { return await process.StopAndConfirmAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(PaddleOcrVlmModelGate.PythonExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add(PaddleOcrVlmPythonScript.Path);
        ConfigureOfflineEnvironment(startInfo);
        return startInfo;
    }

    private static void ConfigureOfflineEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(static key => key.StartsWith("HF_", StringComparison.OrdinalIgnoreCase) ||
                                          key.StartsWith("HUGGINGFACE_", StringComparison.OrdinalIgnoreCase) ||
                                          key.StartsWith("TRANSFORMERS_", StringComparison.OrdinalIgnoreCase) ||
                                          key.StartsWith("PADDLE", StringComparison.OrdinalIgnoreCase) ||
                                          key.StartsWith("PIP_", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(key, "PYTHONPATH", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        startInfo.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        startInfo.Environment["PIP_NO_INDEX"] = "1";
        startInfo.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        startInfo.Environment["HF_HOME"] = @"J:\Models\provider-cache\huggingface";
        startInfo.Environment["HUGGINGFACE_HUB_CACHE"] = @"J:\Models\provider-cache\huggingface\hub";
        startInfo.Environment["PADDLE_PDX_CACHE_HOME"] = @"J:\Models\provider-cache\paddlex";
        startInfo.Environment["PADDLE_HOME"] = @"J:\Models\provider-cache\paddle";
    }

    internal static bool IsKnownFailureReason(string reason) => reason is
        "document-ocr-dependency-unavailable" or "document-ocr-model-initialization-failed" or
        "document-ocr-orientation-failed" or "document-ocr-recognition-failed" or
        "document-ocr-offline-operation-refused" or "document-ocr-provider-result-invalid";

    internal static string RuntimeExitReason(int exitCode) => $"document-ocr-runtime-exit-{unchecked((uint)exitCode):x8}";

    internal static async ValueTask<string> ReadFailureReasonAsync(string workingDirectory, string outputPath, CancellationToken cancellationToken)
    {
        const string unknown = "document-ocr-runtime-failed";
        try
        {
            if (!string.Equals(Path.GetFullPath(outputPath), Path.Combine(Path.GetFullPath(workingDirectory), "output.json"), StringComparison.OrdinalIgnoreCase)) return unknown;
            var file = await ContainedFileReader.ReadAsync(workingDirectory, "output.json", 4096, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(file.Bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("succeeded", out var succeeded) || succeeded.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("reasonCode", out var reason) || reason.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() != 0) return unknown;
            return IsKnownFailureReason(reason.GetString()!) ? reason.GetString()! : unknown;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            return unknown;
        }
    }

}

internal static class PaddleOcrVlmPythonScript
{
    internal const string Filename = "paddleocr_vl_executor.py";
    internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Documents", Filename);
}
