using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;

namespace TesseractTextBaseline;

internal static class RunnerSelfTests
{
    public static async Task RunAsync()
    {
        await RefusedGateStartsNoProcessAsync();
        await SuccessfulProcessRetainsModelLeaseUntilExitAsync();
        await RequestedPageSegmentationModeIsForwardedAsync();
        await UnsupportedPageSegmentationModeRefusesBeforeProcessStartAsync();
        await FailedAndTimedOutProcessesAreReportedAsync();
        await ShortLivedChildDoesNotReadMemoryAfterExitAsync();
    }

    private static async Task RefusedGateStartsNoProcessAsync()
    {
        using var files = new TestVerificationFiles(missingArtifact: true);
        var process = new RecordingProcessRunner();
        var runner = new TesseractBenchmarkRunner(new LocalModelStore(() => files), process);
        var result = await runner.RunAsync(TestRequest(), CancellationToken.None);

        Assert(!result.Succeeded && result.FailureReason == ModelStoreReasons.ArtifactMissing, "A missing model must refuse the run.");
        Assert(process.Calls == 0, "A refused model gate must start zero processes.");
    }

    private static async Task SuccessfulProcessRetainsModelLeaseUntilExitAsync()
    {
        using var files = new TestVerificationFiles(missingArtifact: false);
        var process = new RecordingProcessRunner { BeforeCompletion = () => Assert(!files.Disposed, "The verified model lease must survive until the owned process completes.") };
        var runner = new TesseractBenchmarkRunner(new LocalModelStore(() => files), process);
        var result = await runner.RunAsync(TestRequest(), CancellationToken.None);

        Assert(result.Succeeded, "A successful fake process must complete the run.");
        Assert(result.Samples.Single().PageText == "recognised", "Only CRLF and terminal newlines may change during text serialization.");
        Assert(Program.CreateCandidateName(result) == $"tesseract-eng-sha256-{result.ModelSha256}", "Candidate identity must derive from the verified manifest hash, not a best/fast label.");
        Assert(files.Disposed, "The verified model lease must be released after process completion.");
    }

    private static async Task RequestedPageSegmentationModeIsForwardedAsync()
    {
        using var files = new TestVerificationFiles(missingArtifact: false);
        var process = new RecordingProcessRunner();
        var runner = new TesseractBenchmarkRunner(new LocalModelStore(() => files), process);

        var result = await runner.RunAsync(TestRequest(pageSegmentationMode: 6), CancellationToken.None);

        Assert(result.Succeeded, "A supported requested page segmentation mode must complete normally.");
        Assert(process.OcrStarts.Single().PageSegmentationMode == 6, "The requested page segmentation mode must reach the owned OCR process.");
    }

    private static async Task UnsupportedPageSegmentationModeRefusesBeforeProcessStartAsync()
    {
        using var files = new TestVerificationFiles(missingArtifact: false);
        var process = new RecordingProcessRunner();
        var runner = new TesseractBenchmarkRunner(new LocalModelStore(() => files), process);

        var result = await runner.RunAsync(TestRequest(pageSegmentationMode: 11), CancellationToken.None);

        Assert(!result.Succeeded && result.FailureReason == ModelStoreReasons.SpecificationInvalid, "An unsupported page segmentation mode must be refused.");
        Assert(process.Calls == 0, "An unsupported page segmentation mode must start zero processes.");
    }

    private static async Task FailedAndTimedOutProcessesAreReportedAsync()
    {
        using var failedFiles = new TestVerificationFiles(missingArtifact: false);
        var failedRunner = new TesseractBenchmarkRunner(
            new LocalModelStore(() => failedFiles),
            new RecordingProcessRunner { OcrResult = ProcessExecutionResult.Failed(7, "simulated failure") });
        var failed = await failedRunner.RunAsync(TestRequest(), CancellationToken.None);
        Assert(failed.Samples.Single().FailureReason == "process-exit-7", "A failed process must be reported per sample.");

        using var timeoutFiles = new TestVerificationFiles(missingArtifact: false);
        var timeoutRunner = new TesseractBenchmarkRunner(
            new LocalModelStore(() => timeoutFiles),
            new RecordingProcessRunner { OcrResult = ProcessExecutionResult.Timeout("simulated timeout") });
        var timedOut = await timeoutRunner.RunAsync(TestRequest(), CancellationToken.None);
        Assert(timedOut.Samples.Single().FailureReason == "process-timeout", "A timed-out process must be reported per sample.");
    }

    private static async Task ShortLivedChildDoesNotReadMemoryAfterExitAsync()
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\\Windows\\System32\\cmd.exe";
        var result = await new WindowsTesseractProcessRunner().RunAsync(
            TesseractProcessStart.Version(commandInterpreter, TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert(result.Completed, "A short-lived child must return a process result instead of reading memory after it exits.");
    }

    private static TesseractBenchmarkRequest TestRequest(int pageSegmentationMode = 3) => new(
        TesseractBenchmarkRunner.ExpectedRuntimePath,
        "C:\\truth\\truth.json",
        "C:\\output",
        Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"files\":[{\"revision\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"filename\":\"eng.traineddata\",\"sha256\":\"" + Convert.ToHexString(SHA256.HashData("eng"u8)).ToLowerInvariant() + "\",\"byteLength\":3}]}"),
        [new TesseractBenchmarkInput("en-01", "C:\\fixtures\\en-01.png")],
        PageSegmentationMode: pageSegmentationMode);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingProcessRunner : ITesseractProcessRunner
    {
        public int Calls { get; private set; }
        public List<TesseractProcessStart> OcrStarts { get; } = [];
        public Action? BeforeCompletion { get; init; }
        public ProcessExecutionResult VersionResult { get; init; } = ProcessExecutionResult.Succeeded(0, "tesseract v5.5.3.20260724\\n", "", "tesseract v5.5.3.20260724", TimeSpan.FromMilliseconds(4), 1234);
        public ProcessExecutionResult? OcrResult { get; init; }

        public ValueTask<ProcessExecutionResult> RunAsync(TesseractProcessStart start, CancellationToken cancellationToken)
        {
            Calls++;
            if (!start.IsVersionProbe) OcrStarts.Add(start);
            BeforeCompletion?.Invoke();
            return ValueTask.FromResult(start.IsVersionProbe ? VersionResult : OcrResult ?? ProcessExecutionResult.Succeeded(0, "recognised\r\n", "", null, TimeSpan.FromMilliseconds(4), 1234));
        }
    }

    private sealed class TestVerificationFiles : IModelVerificationFiles
    {
        private readonly byte[] _content = "eng"u8.ToArray();
        private readonly bool _missingArtifact;

        public TestVerificationFiles(bool missingArtifact) => _missingArtifact = missingArtifact;

        public bool Disposed { get; private set; }

        public ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(ModelArtifactSpecification specification, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_missingArtifact
                ? ModelVerificationFileOpenResult.Refused(ModelStoreReasons.ArtifactMissing)
                : ModelVerificationFileOpenResult.Found(new TestVerificationFile(_content)));

        public ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(ModelVerificationReceipt receipt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ModelReceiptPersistenceResult.Published("test-receipt.json"));

        public void Dispose() => Disposed = true;
    }

    private sealed class TestVerificationFile(byte[] content) : IModelVerificationFile
    {
        public long ByteLength => content.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var available = Math.Max(0, content.Length - checked((int)offset));
            var copied = Math.Min(available, buffer.Length);
            content.AsSpan((int)offset, copied).CopyTo(buffer.Span);
            return ValueTask.FromResult(copied);
        }

        public void Dispose() { }
    }
}
