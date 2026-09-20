using System.Diagnostics;
using FluxKnowledge.Application.Models;

namespace TesseractTextBaseline;

internal sealed record TesseractBenchmarkRequest(
    string RuntimePath,
    string TruthPath,
    string OutputDirectory,
    byte[] NativeGateManifest,
    IReadOnlyList<TesseractBenchmarkInput> Inputs,
    TimeSpan? ProcessTimeout = null,
    int PageSegmentationMode = 3);

internal sealed record TesseractBenchmarkInput(string Id, string ImagePath);

internal sealed record TesseractProcessStart(
    string RuntimePath,
    string? ImagePath,
    string? TessdataDirectory,
    TimeSpan Timeout,
    bool IsVersionProbe,
    int PageSegmentationMode)
{
    public static TesseractProcessStart Version(string runtimePath, TimeSpan timeout) =>
        new(runtimePath, null, null, timeout, true, 3);
}

internal sealed record ProcessExecutionResult(
    bool Completed,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? EngineVersion,
    TimeSpan Elapsed,
    long? SampledPeakWorkingSetBytes)
{
    public static ProcessExecutionResult Succeeded(int exitCode, string standardOutput, string standardError, string? engineVersion, TimeSpan elapsed, long? sampledPeakWorkingSetBytes) =>
        new(true, false, exitCode, standardOutput, standardError, engineVersion, elapsed, sampledPeakWorkingSetBytes);

    public static ProcessExecutionResult Failed(int exitCode, string standardError) =>
        new(true, false, exitCode, string.Empty, standardError, null, TimeSpan.Zero, null);

    public static ProcessExecutionResult Timeout(string standardError) =>
        new(false, true, null, string.Empty, standardError, null, TimeSpan.Zero, null);
}

internal interface ITesseractProcessRunner
{
    ValueTask<ProcessExecutionResult> RunAsync(TesseractProcessStart start, CancellationToken cancellationToken);
}

internal sealed record TesseractSampleResult(
    string Id,
    string PageText,
    string? FailureReason,
    int? ExitCode,
    string? StandardError,
    long? SampledPeakWorkingSetBytes,
    TimeSpan Elapsed);

internal sealed record TesseractBenchmarkResult(
    bool Succeeded,
    string? FailureReason,
    string? EngineVersion,
    string? ModelSha256,
    string? ModelRevision,
    IReadOnlyList<TesseractSampleResult> Samples);

internal sealed class TesseractBenchmarkRunner
{
    public const string ExpectedRuntimePath = @"J:\Models\runtimes\tesseract\5.5.3.20260724\tesseract.exe";
    private const string ModelFilename = "eng.traineddata";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly ILocalModelStore _modelStore;
    private readonly ITesseractProcessRunner _processRunner;

    public TesseractBenchmarkRunner(ILocalModelStore modelStore, ITesseractProcessRunner processRunner)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async ValueTask<TesseractBenchmarkResult> RunAsync(TesseractBenchmarkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = request.ProcessTimeout ?? DefaultTimeout;
        if (!IsExpectedRuntime(request.RuntimePath) || !IsSupportedPageSegmentationMode(request.PageSegmentationMode) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            return FailedForAll(request.Inputs, ModelStoreReasons.SpecificationInvalid, null, null);
        }

        ModelBundleSpecification manifest;
        ModelArtifactSpecification model;
        try
        {
            manifest = ModelManifestCodec.Parse(request.NativeGateManifest);
            model = GetEnglishModel(manifest);
        }
        catch (ModelManifestException exception)
        {
            return FailedForAll(request.Inputs, exception.ReasonCode, null, null);
        }

        var resolution = await _modelStore.ResolveAsync(manifest, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded || resolution.Lease is null)
        {
            return FailedForAll(request.Inputs, resolution.ReasonCode, null, model.Sha256, model.Revision);
        }

        using (resolution.Lease)
        {
            var tessdataDirectory = Path.Combine(
                @"J:\Models",
                "artifacts",
                "sha256",
                model.Sha256);

            var version = await _processRunner.RunAsync(
                    TesseractProcessStart.Version(ExpectedRuntimePath, timeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!version.Completed || version.TimedOut || version.ExitCode != 0 || string.IsNullOrWhiteSpace(version.EngineVersion))
            {
                return FailedForAll(request.Inputs, ProcessFailureReason(version, "version"), version.EngineVersion, model.Sha256, model.Revision);
            }
            if (!version.EngineVersion.StartsWith("tesseract v5.5.3.20260724", StringComparison.OrdinalIgnoreCase))
            {
                return FailedForAll(request.Inputs, "version-unexpected", version.EngineVersion, model.Sha256, model.Revision);
            }

            var results = new List<TesseractSampleResult>(request.Inputs.Count);
            foreach (var input in request.Inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsLocalAbsolutePath(input.ImagePath))
                {
                    results.Add(new TesseractSampleResult(input.Id, string.Empty, "unsafe-input-path", null, null, null, TimeSpan.Zero));
                    continue;
                }

                var execution = await _processRunner.RunAsync(
                        new TesseractProcessStart(ExpectedRuntimePath, input.ImagePath, tessdataDirectory, timeout, false, request.PageSegmentationMode),
                        cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new TesseractSampleResult(
                    input.Id,
                    execution.Completed && !execution.TimedOut && execution.ExitCode == 0 ? SerializeText(execution.StandardOutput) : string.Empty,
                    execution.Completed && !execution.TimedOut && execution.ExitCode == 0 ? null : ProcessFailureReason(execution, "process"),
                    execution.ExitCode,
                    execution.StandardError,
                    execution.SampledPeakWorkingSetBytes,
                    execution.Elapsed));
            }

            return new TesseractBenchmarkResult(
                results.All(static sample => sample.FailureReason is null),
                results.FirstOrDefault(static sample => sample.FailureReason is not null)?.FailureReason,
                version.EngineVersion,
                model.Sha256,
                model.Revision,
                results);
        }
    }

    private static ModelArtifactSpecification GetEnglishModel(ModelBundleSpecification manifest)
    {
        var models = manifest.Files.Where(static file => string.Equals(file.Filename, ModelFilename, StringComparison.Ordinal)).ToArray();
        if (models.Length != 1 || manifest.Files.Count != 1)
        {
            throw new ModelManifestException(ModelStoreReasons.SpecificationInvalid);
        }

        return models[0];
    }

    private static TesseractBenchmarkResult FailedForAll(
        IReadOnlyList<TesseractBenchmarkInput> inputs,
        string failureReason,
        string? engineVersion,
        string? modelSha256,
        string? modelRevision = null) =>
        new(false, failureReason, engineVersion, modelSha256, modelRevision,
            inputs.Select(input => new TesseractSampleResult(input.Id, string.Empty, failureReason, null, null, null, TimeSpan.Zero)).ToArray());

    private static string ProcessFailureReason(ProcessExecutionResult execution, string prefix) =>
        execution.TimedOut ? $"{prefix}-timeout" : execution.ExitCode is { } exitCode ? $"{prefix}-exit-{exitCode}" : $"{prefix}-failed";

    private static bool IsExpectedRuntime(string runtimePath) =>
        string.Equals(Path.GetFullPath(runtimePath), ExpectedRuntimePath, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedPageSegmentationMode(int pageSegmentationMode) => pageSegmentationMode is 3 or 4 or 6;

    private static bool IsLocalAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        return !Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.IsFile;
    }

    internal static string SerializeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalised.EndsWith('\n') ? normalised.TrimEnd('\n') : normalised;
    }
}

internal sealed class WindowsTesseractProcessRunner : ITesseractProcessRunner
{
    public async ValueTask<ProcessExecutionResult> RunAsync(TesseractProcessStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        var startInfo = new ProcessStartInfo
        {
            FileName = start.RuntimePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (start.IsVersionProbe)
        {
            startInfo.ArgumentList.Add("--version");
        }
        else
        {
            startInfo.ArgumentList.Add(start.ImagePath!);
            startInfo.ArgumentList.Add("stdout");
            startInfo.ArgumentList.Add("--tessdata-dir");
            startInfo.ArgumentList.Add(start.TessdataDirectory!);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("eng");
            startInfo.ArgumentList.Add("--oem");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--psm");
            startInfo.ArgumentList.Add(start.PageSegmentationMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        long? sampledPeakWorkingSetBytes = null;
        using var sampling = new CancellationTokenSource();
        var samplingTask = SampleWorkingSetAsync(process, observed =>
        {
            if (sampledPeakWorkingSetBytes is null || observed > sampledPeakWorkingSetBytes) sampledPeakWorkingSetBytes = observed;
        }, sampling.Token);
        using var timeout = new CancellationTokenSource(start.Timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            stopwatch.Stop();
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            return ProcessExecutionResult.Timeout(await standardError.ConfigureAwait(false)) with { Elapsed = stopwatch.Elapsed, SampledPeakWorkingSetBytes = sampledPeakWorkingSetBytes };
        }
        finally
        {
            sampling.Cancel();
            await samplingTask.ConfigureAwait(false);
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        stopwatch.Stop();
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        var version = start.IsVersionProbe && process.ExitCode == 0 ? FirstLine(output) : null;
        return ProcessExecutionResult.Succeeded(process.ExitCode, output, error, version, stopwatch.Elapsed, sampledPeakWorkingSetBytes);
    }

    private static async Task SampleWorkingSetAsync(Process process, Action<long> observe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return;
                observe(process.WorkingSet64);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static string? FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }
}
