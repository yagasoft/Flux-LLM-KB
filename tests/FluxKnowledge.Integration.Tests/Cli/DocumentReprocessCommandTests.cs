using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

public sealed class DocumentReprocessCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_document_command_forwards_the_failed_job_and_expected_root_revision(bool pdf)
    {
        var revisionId = Guid.NewGuid(); var failedJob = Guid.NewGuid();
        var executor = new RecordingExecutor(new(Guid.NewGuid(), true, false, true, true, "completed"));
        var fingerprint = pdf ? PdfDocumentProcessor.Capability.ProcessorFingerprint : ImageDocumentInputProcessor.Capability.ProcessorFingerprint;
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await DocumentReprocessCommand.ExecuteAsync(
            ["reprocess", "--source-revision", revisionId.ToString("D"), "--expected-input-sha256", new string('a', 64),
             "--expected-processor-fingerprint", fingerprint,
             "--failed-extract-job", failedJob.ToString("D"), "--expected-root-configuration-revision", "2"], executor, output, error);
        Assert.Equal(0, exit);
        Assert.Equal(new DocumentReprocessRequest(new(revisionId), new string('a', 64), fingerprint, failedJob, 2), executor.Request);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("zero-job")]
    [InlineData("invalid-job")]
    [InlineData("zero-revision")]
    [InlineData("negative-revision")]
    [InlineData("invalid-revision")]
    [InlineData("wrong-flag")]
    [InlineData("vsdx-with-exact-binding")]
    public async Task Invalid_exact_image_binding_never_calls_the_executor(string variation)
    {
        var executor = new RecordingExecutor(new(Guid.NewGuid(), true, false, true, true, "completed"));
        using var output = new StringWriter(); using var error = new StringWriter();
        string[] args = ["reprocess", "--source-revision", Guid.NewGuid().ToString("D"), "--expected-input-sha256", new string('a', 64),
            "--expected-processor-fingerprint", variation == "vsdx-with-exact-binding" ? VsdxStructuralTextProcessor.Capability.ProcessorFingerprint : ImageDocumentInputProcessor.Capability.ProcessorFingerprint];
        if (variation != "missing-binding") args = [..args, variation == "wrong-flag" ? "--failed-job" : "--failed-extract-job",
            variation switch { "zero-job" => Guid.Empty.ToString("D"), "invalid-job" => "invalid", _ => Guid.NewGuid().ToString("D") },
            "--expected-root-configuration-revision", variation switch { "zero-revision" => "0", "negative-revision" => "-1", "invalid-revision" => "invalid", _ => "2" }];
        Assert.Equal(2, await DocumentReprocessCommand.ExecuteAsync(args, executor, output, error));
        Assert.Null(executor.Request);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Existing_seven_argument_pdf_command_remains_unchanged()
    {
        var revisionId = SourceRevisionId.New();
        var executor = new RecordingExecutor(new(Guid.NewGuid(), true, false, true, true, "completed"));
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await DocumentReprocessCommand.ExecuteAsync(
            ["reprocess", "--source-revision", revisionId.Value.ToString("D"), "--expected-input-sha256", new string('a', 64),
             "--expected-processor-fingerprint", PdfDocumentProcessor.Capability.ProcessorFingerprint], executor, output, error));
        Assert.Equal(new DocumentReprocessRequest(revisionId, new string('a', 64), PdfDocumentProcessor.Capability.ProcessorFingerprint), executor.Request);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Exact_vsdx_command_forwards_only_the_named_immutable_binding()
    {
        var revisionId = SourceRevisionId.New();
        var hash = new string('a', 64);
        var executor = new RecordingExecutor(new DocumentReprocessCommandResult(
            Guid.NewGuid(), Created: true, WasReplay: false, Claimed: true, Completed: true, OutcomeCode: "completed"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DocumentReprocessCommand.ExecuteAsync(
            ["reprocess", "--source-revision", revisionId.Value.ToString("D"), "--expected-input-sha256", hash,
                "--expected-processor-fingerprint", VsdxStructuralTextProcessor.Capability.ProcessorFingerprint],
            executor,
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(new DocumentReprocessRequest(revisionId, hash, VsdxStructuralTextProcessor.Capability.ProcessorFingerprint), executor.Request);
        Assert.Empty(error.ToString());
        using var result = JsonDocument.Parse(output.ToString());
        Assert.True(result.RootElement.GetProperty("completed").GetBoolean());
        Assert.DoesNotContain("retained", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_document_reprocess_command_never_calls_the_executor()
    {
        var executor = new RecordingExecutor(new DocumentReprocessCommandResult(
            Guid.NewGuid(), Created: true, WasReplay: false, Claimed: true, Completed: true, OutcomeCode: "completed"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DocumentReprocessCommand.ExecuteAsync(
            ["reprocess", "--source-revision", Guid.NewGuid().ToString("D")],
            executor,
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Null(executor.Request);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingExecutor(DocumentReprocessCommandResult result) : IDocumentReprocessExecutor
    {
        public DocumentReprocessRequest? Request { get; private set; }

        public ValueTask<DocumentReprocessCommandResult> ExecuteAsync(
            DocumentReprocessRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(result);
        }
    }
}
