using FluxKnowledge.Integrations.Documents;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class PaddleOcrVlmFailureResultTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("FluxOcrFailure_").FullName;

    [Theory]
    [InlineData(103, "document-ocr-runtime-exit-00000067")]
    [InlineData(-1073741819, "document-ocr-runtime-exit-c0000005")]
    public void Early_child_failure_retains_a_sanitised_exit_status(int exitCode, string expected) =>
        Assert.Equal(expected, PaddleOcrVlmProcessRunner.RuntimeExitReason(exitCode));

    [Theory]
    [InlineData("{\"succeeded\":false,\"reasonCode\":\"document-ocr-model-initialization-failed\",\"pages\":[]}", "document-ocr-model-initialization-failed")]
    [InlineData("{\"succeeded\":false,\"reasonCode\":\"document-ocr-dependency-unavailable\",\"pages\":[]}", "document-ocr-dependency-unavailable")]
    [InlineData("{\"succeeded\":false,\"reasonCode\":\"document-ocr-private-source-text\",\"pages\":[]}", "document-ocr-runtime-failed")]
    [InlineData("{\"succeeded\":true,\"reasonCode\":\"document-ocr-recognition-failed\",\"pages\":[]}", "document-ocr-runtime-failed")]
    [InlineData("{malformed", "document-ocr-runtime-failed")]
    public async Task Failed_child_exposes_only_known_sanitised_failure_codes(string json, string expected)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "output.json"), json);
        Assert.Equal(expected, await PaddleOcrVlmProcessRunner.ReadFailureReasonAsync(_root, Path.Combine(_root, "output.json"), default));
    }

    [Fact]
    public async Task Oversized_or_redirected_failure_output_remains_a_generic_runtime_failure()
    {
        var output = Path.Combine(_root, "output.json");
        await File.WriteAllTextAsync(output, new string('x', 8192));
        Assert.Equal("document-ocr-runtime-failed", await PaddleOcrVlmProcessRunner.ReadFailureReasonAsync(_root, output, default));
        File.Delete(output);
        var target = Path.Combine(_root, "untrusted.json");
        await File.WriteAllTextAsync(target, "{\"succeeded\":false,\"reasonCode\":\"document-ocr-recognition-failed\",\"pages\":[]}");
        File.CreateSymbolicLink(output, target);
        Assert.Equal("document-ocr-runtime-failed", await PaddleOcrVlmProcessRunner.ReadFailureReasonAsync(_root, output, default));
        Assert.Equal("document-ocr-runtime-failed", await PaddleOcrVlmProcessRunner.ReadFailureReasonAsync(_root, target, default));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
