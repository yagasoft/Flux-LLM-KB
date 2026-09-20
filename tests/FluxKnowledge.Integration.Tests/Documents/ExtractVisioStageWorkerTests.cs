using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class ExtractVisioStageWorkerTests
{
    [Fact]
    public async Task Interpreted_result_enters_one_existing_document_pipeline_with_provenance()
    {
        var ports = new Ports();
        var worker = new ExtractVisioStageWorker(ports, ports, ports, ports, TimeProvider.System);
        await worker.ExecuteAsync(ports.Work, default);
        Assert.NotNull(ports.Transition);
        Assert.Equal("public shape text", ports.Transition.Artifact.SearchText);
        Assert.Equal("{\"version\":1,\"pages\":[]}", ports.Transition.Artifact.DocumentMetadataJson);
        Assert.Equal(PipelineStage.Normalise, ports.Transition.NextStage);
        Assert.Equal("visio-extracted", worker.OutcomeCode);
        Assert.Null(ports.Failure);
    }

    [Theory]
    [InlineData("visio-cleanup-unproven")]
    [InlineData("visio-document-extraction-failed")]
    public async Task Cleanup_uncertainty_retains_deletion_fence_but_a_clean_failure_is_terminal(string reason)
    {
        var ports = new Ports { Error = new RetainedProcessorException(reason) };
        var worker = new ExtractVisioStageWorker(ports, ports, ports, ports, TimeProvider.System);
        if (reason == "visio-cleanup-unproven")
        {
            await Assert.ThrowsAsync<RetainedProcessorException>(async () => await worker.ExecuteAsync(ports.Work, default));
            Assert.Null(ports.Failure);
        }
        else
        {
            await worker.ExecuteAsync(ports.Work, default);
            Assert.Equal(reason, ports.Failure?.Reason);
        }
        Assert.Null(ports.Transition);
        Assert.Null(worker.Result);
    }

    [Fact]
    public void Desktop_command_accepts_only_an_exact_Visio_binding()
    {
        string[] args = ["run-visio", "--source-revision", Guid.NewGuid().ToString(), "--expected-input-sha256",
            new string('a', 64), "--expected-processor-fingerprint", VisioDocumentInputProcessor.Capability.ProcessorFingerprint];
        Assert.True(VisioDocumentCommand.TryReadRequest(args, out var request));
        Assert.Equal(args[4], request.ExpectedInputSha256);
        args[6] = VsdxStructuralTextProcessor.Capability.ProcessorFingerprint;
        Assert.False(VisioDocumentCommand.TryReadRequest(args, out _));
        Assert.False(VisioDocumentCommand.TryReadRequest(["run-visio"], out _));
    }

    private sealed class Ports : IRetainedSourceReader, IPipelineStageReader, IVisioDocumentExtractor, IStageTransitionStore
    {
        private readonly SourceRevisionId _inputId = SourceRevisionId.New();
        private readonly string _hash = new('a', 64);
        public Exception? Error { get; init; }
        public StageTransitionRequest? Transition { get; private set; }
        public StageFailureRequest? Failure { get; private set; }
        public StageWorkItem Work { get; } = CreateWork();
        public string? GetUnavailableReason() => null;
        public ValueTask<VisioDocumentResult> ExtractAsync(RetainedSourceBytes retained, CancellationToken cancellationToken) =>
            Error is not null ? ValueTask.FromException<VisioDocumentResult>(Error) : ValueTask.FromResult(new VisioDocumentResult(
                new DocumentExtractionResult("public shape text", true, []), "{\"version\":1,\"pages\":[]}", 1, 0, 10, 1024));
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RetainedSourceBytes(id, [1, 2, 3, 4], _hash, 4));
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PipelineStageSource> ReadStageSourceAsync(PipelineRecordId id, long revision, PipelineStage stage, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PipelineStageSource(id, revision, "opaque", _hash, null, _inputId,
                DocumentProcessingInput.VisioClassification, ".vsdx"));
        public ValueTask<StageTransitionResult> TransitionAsync(StageTransitionRequest request, CancellationToken cancellationToken)
        {
            Transition = request;
            return ValueTask.FromResult(new StageTransitionResult(request.Artifact.Id, null, null, false));
        }
        public ValueTask FailAsync(StageFailureRequest request, CancellationToken cancellationToken)
        {
            Failure = request;
            return ValueTask.CompletedTask;
        }
        private static StageWorkItem CreateWork()
        {
            var id = PipelineRecordId.New();
            var now = DateTimeOffset.UtcNow;
            return new StageWorkItem(new ClaimedDispatchMessage(DispatchMessageId.New(), id, 1, PipelineStage.Extract,
                PipelineOperations.ExtractVisio, 0, "test", now, "desktop", now.AddMinutes(12), 1),
                new ClaimedJob(JobId.New(), id, 1, PipelineStage.Extract, PipelineOperations.ExtractVisio,
                    PublicJobState.WorkerProcessing, now, 1, "desktop", now.AddMinutes(12), 1));
        }
    }
}
