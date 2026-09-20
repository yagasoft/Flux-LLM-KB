using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

public sealed class DocumentReprocessCommandTests
{
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
