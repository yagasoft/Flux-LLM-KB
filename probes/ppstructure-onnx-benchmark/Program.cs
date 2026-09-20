using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace PpStructureOnnxBenchmark;

internal static class Program
{
    private const string RuntimeRoot = @"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312";
    private const string BundleRoot = RuntimeRoot + @"\bundles";
    private const string Python = RuntimeRoot + @"\Scripts\python.exe";
    private const string TruthSha256 = "284d8890f86b81a7d73ccaf46656704aceb1b79e0e8cf9aa1c65095670361cfb";
    private const string AssessmentRoot = @"E:\Temp\flux-ocr-assessment-20260919";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "self-test")
        {
            await SelfTestAsync().ConfigureAwait(false);
            Console.WriteLine("Self-tests passed. No provider import or model load occurred.");
            return 0;
        }
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("PPStructureV3 synthetic screening benchmark; not a release gate.\nrun --truth <truth.json> --output <E:\\Temp assessment output> [--timeout-seconds <1..3600>]");
            return 0;
        }
        try
        {
            if (args[0] != "run") throw new ArgumentException("Unknown command.");
            var truth = Required(args, "--truth");
            var output = Path.GetFullPath(Required(args, "--output"));
            var timeout = TimeSpan.FromSeconds(int.TryParse(Optional(args, "--timeout-seconds"), out var seconds) ? seconds : 1800);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
            EnsureAssessmentPath(output);
            var samples = ReadSamples(truth);
            Directory.CreateDirectory(output);
            var bundle = BundleManifest.Load();
            var modelStore = new LocalModelStore(() => new ProbeVerificationFiles(bundle.Paths));
            var resolution = await modelStore.ResolveAsync(bundle.Specification, CancellationToken.None).ConfigureAwait(false);
            if (!resolution.Succeeded || resolution.Lease is null) throw new InvalidOperationException("model-gate-refused:" + resolution.ReasonCode);
            using (resolution.Lease)
            {
                var configPath = Path.Combine(output, "ppstructure-input.json");
                var resultPath = Path.Combine(output, "ppstructure-raw-results.json");
                await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { runtimeRoot = RuntimeRoot, bundleRoot = BundleRoot, outputPath = resultPath, modelDirs = bundle.RoleDirectories, samples }, Json) + Environment.NewLine, new UTF8Encoding(false)).ConfigureAwait(false);
                var execution = await RunChildAsync(configPath, timeout).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(output, "ppstructure-process.json"), JsonSerializer.Serialize(execution, Json) + Environment.NewLine, new UTF8Encoding(false)).ConfigureAwait(false);
                Console.WriteLine($"Raw provider results: {resultPath}");
                return execution.ExitCode == 0 ? 0 : 1;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("PPStructure probe failed: " + exception.Message);
            return 2;
        }
    }

    private static async Task SelfTestAsync()
    {
        var missing = new ModelBundleSpecification(1, [new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "x.onnx", new string('a', 64), 1)]);
        var result = await new LocalModelStore(() => new RefusingFiles()).ResolveAsync(missing, CancellationToken.None).ConfigureAwait(false);
        if (result.Succeeded || result.ReasonCode != ModelStoreReasons.ArtifactMissing) throw new InvalidOperationException("missing-artifact-must-refuse-before-child");
        if (!string.Equals(Path.GetFullPath(BundleRoot), BundleRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("bundle-root-must-be-fixed");
    }

    private static async Task<ChildExecution> RunChildAsync(string configPath, TimeSpan timeout)
    {
        var driver = Path.Combine(AppContext.BaseDirectory, "ppstructure_driver.py");
        if (!File.Exists(Python) || !File.Exists(driver)) throw new FileNotFoundException("Approved Python runtime or probe driver is unavailable.");
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = Python, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(driver);
        process.StartInfo.ArgumentList.Add(configPath);
        var started = Stopwatch.StartNew(); process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
        started.Stop();
        long? sampledPeakWorkingSetBytes = null;
        try { sampledPeakWorkingSetBytes = process.PeakWorkingSet64; }
        catch (InvalidOperationException) { }
        return new ChildExecution(process.ExitCode, cancellation.IsCancellationRequested, started.Elapsed.TotalMilliseconds, sampledPeakWorkingSetBytes, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static IReadOnlyList<object> ReadSamples(string truthPath)
    {
        var full = Path.GetFullPath(truthPath); if (!full.StartsWith(AssessmentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("truth-path-outside-assessment");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant(), TruthSha256, StringComparison.Ordinal)) throw new InvalidDataException("truth-sha256-mismatch");
        using var document = JsonDocument.Parse(File.ReadAllBytes(full)); var root = Path.GetDirectoryName(full)!;
        return document.RootElement.GetProperty("samples").EnumerateArray().Select(sample => new { id = sample.GetProperty("id").GetString()!, imagePath = Path.GetFullPath(Path.Combine(root, sample.GetProperty("imagePath").GetString()!)) }).Cast<object>().ToArray();
    }

    private static void EnsureAssessmentPath(string path) { if (!path.StartsWith(AssessmentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("output-path-outside-assessment"); }
    private static string Required(string[] args, string name) => Optional(args, name) ?? throw new ArgumentException("Missing " + name);
    private static string? Optional(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }

    private sealed class RefusingFiles : IModelVerificationFiles
    {
        public ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(ModelArtifactSpecification specification, CancellationToken cancellationToken) => ValueTask.FromResult(ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing));
        public ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(ModelVerificationReceipt receipt, CancellationToken cancellationToken) => ValueTask.FromResult(ModelReceiptPersistenceResult.Published("self-test"));
        public void Dispose() { }
    }

    private sealed record ChildExecution(int ExitCode, bool TimedOut, double ElapsedMilliseconds, long? SampledPeakWorkingSetBytes, string StandardOutput, string StandardError);
}
