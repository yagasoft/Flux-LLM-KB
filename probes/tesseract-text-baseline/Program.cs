using System.Text;
using System.Text.Json;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;

namespace TesseractTextBaseline;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "self-test")
            {
                await RunnerSelfTests.RunAsync();
                Console.WriteLine("Self-tests passed. No Tesseract runtime or model was loaded.");
                return 0;
            }

            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            if (args[0] != "run") throw new ArgumentException($"Unknown command '{args[0]}'.");
            var runtime = RequiredArgument(args, "--runtime");
            var manifestPath = RequiredArgument(args, "--manifest");
            var truthPath = RequiredArgument(args, "--truth");
            var outputDirectory = Path.GetFullPath(RequiredArgument(args, "--output"));
            var timeout = OptionalArgument(args, "--timeout-seconds") is { } seconds
                ? TimeSpan.FromSeconds(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture))
                : (TimeSpan?)null;
            var pageSegmentationMode = OptionalArgument(args, "--psm") is { } psm
                ? int.Parse(psm, System.Globalization.CultureInfo.InvariantCulture)
                : 3;

            var manifest = await WindowsModelVerificationFiles.ReadLocalManifestAsync(manifestPath, 1024 * 1024, CancellationToken.None).ConfigureAwait(false);
            var inputs = ReadImageCatalogue(truthPath);
            var runner = new TesseractBenchmarkRunner(
                new LocalModelStore(WindowsModelVerificationFiles.OpenProduction),
                new WindowsTesseractProcessRunner());
            var result = await runner.RunAsync(new TesseractBenchmarkRequest(runtime, truthPath, outputDirectory, manifest, inputs, timeout, pageSegmentationMode), CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(outputDirectory);
            var resultsPath = Path.Combine(outputDirectory, "candidate-results.json");
            await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(ToCandidateOutput(result, pageSegmentationMode), Json) + Environment.NewLine, new UTF8Encoding(false)).ConfigureAwait(false);
            Console.WriteLine($"Candidate results: {resultsPath}");
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"Tesseract baseline failed: {exception.Message}");
            return 2;
        }
    }

    private const string Usage = """
Tesseract English text-baseline runner. It performs no download, installation, fallback or OCR post-correction.

run --runtime J:\Models\runtimes\tesseract\5.5.3.20260724\tesseract.exe --manifest <native-gate.json> --truth <truth.json> --output <external-directory> [--psm <3|4|6>] [--timeout-seconds <1..600>]
    The truth file supplies only sample id and imagePath. Expected text, order and table values are not used.
    Candidate output declares block order and tables unsupported; plain Tesseract is a text baseline only.
self-test
    Uses fake model files and processes only; it never opens a real model or runtime.
""";

    private static string RequiredArgument(string[] args, string name) =>
        OptionalArgument(args, name) ?? throw new ArgumentException($"Required argument {name} was not provided.");

    private static string? OptionalArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IReadOnlyList<TesseractBenchmarkInput> ReadImageCatalogue(string truthPath)
    {
        var absoluteTruth = Path.GetFullPath(truthPath);
        var truthDirectory = Path.GetDirectoryName(absoluteTruth) ?? throw new InvalidDataException("The truth file has no directory.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(absoluteTruth));
        if (!document.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The truth file does not contain a samples array.");
        }

        var result = new List<TesseractBenchmarkInput>();
        foreach (var sample in samples.EnumerateArray())
        {
            var id = RequiredString(sample, "id");
            var relativeImagePath = RequiredString(sample, "imagePath");
            if (Path.IsPathRooted(relativeImagePath) || Uri.TryCreate(relativeImagePath, UriKind.Absolute, out _))
            {
                throw new InvalidDataException("The truth imagePath must be a relative local path.");
            }

            var imagePath = Path.GetFullPath(Path.Combine(truthDirectory, relativeImagePath));
            if (!imagePath.StartsWith(truthDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The truth imagePath escapes the truth directory.");
            }

            result.Add(new TesseractBenchmarkInput(id, imagePath));
        }

        return result;
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"A truth sample is missing {name}.");

    private static object ToCandidateOutput(TesseractBenchmarkResult result, int pageSegmentationMode) => new
    {
        candidateName = $"{CreateCandidateName(result)}-psm-{pageSegmentationMode}",
        provenance = new { engine = "tesseract", version = result.EngineVersion ?? string.Empty, runId = Guid.NewGuid().ToString("D") },
        samples = result.Samples.Select(sample => new
        {
            id = sample.Id,
            pageText = sample.PageText,
            blockOrder = Array.Empty<string>(),
            tables = Array.Empty<object>(),
            execution = new
            {
                failureReason = sample.FailureReason,
                exitCode = sample.ExitCode,
                standardError = sample.StandardError,
                elapsedMilliseconds = sample.Elapsed.TotalMilliseconds,
                sampledPeakWorkingSetBytes = sample.SampledPeakWorkingSetBytes
            }
        }),
        capabilities = new { blockOrder = "unsupported", tables = "unsupported", orientationCorrection = "unsupported", pageSegmentationMode },
        runMetadata = new { succeeded = result.Succeeded, failureReason = result.FailureReason, modelSha256 = result.ModelSha256, modelRevision = result.ModelRevision, engineVersion = result.EngineVersion }
    };

    internal static string CreateCandidateName(TesseractBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ModelSha256 is { Length: 64 } hash
            ? $"tesseract-eng-sha256-{hash}"
            : "tesseract-eng-unverified-model";
    }
}
