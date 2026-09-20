using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.Inference;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Pipeline;

public sealed class StageWorkerTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");

    [Fact]
    public async Task Extract_fails_the_claim_with_the_required_reason_when_bytes_changed()
    {
        var transitions = new RecordingTransitionStore();
        var worker = new ExtractUtf8StageWorker(
            new StubSourceReader(new string('b', 64), "changed"),
            new StubPipelineReader(new string('a', 64), inputText: null),
            CreateTransitionService(transitions),
            new FixedTimeProvider());
        var work = CreateWork(PipelineStage.Extract, PipelineOperations.ExtractUtf8);

        await worker.ExecuteAsync(work, CancellationToken.None);

        var failure = Assert.Single(transitions.Failures);
        Assert.Equal(ExtractUtf8StageWorker.ChangedSourceReason, failure.Reason);
        Assert.Empty(transitions.Transitions);
    }

    [Fact]
    public async Task Extract_uses_retained_bytes_when_the_discovered_path_has_been_renamed()
    {
        var transitions = new RecordingTransitionStore();
        var retainedRevisionId = SourceRevisionId.New();
        var worker = new ExtractUtf8StageWorker(
            new ThrowingSourceReader(),
            new RetainedSourceReader(retainedRevisionId, new string('a', 64), "retained text"),
            new StubPipelineReader(new string('a', 64), inputText: null, retainedRevisionId),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(CreateWork(PipelineStage.Extract, PipelineOperations.ExtractUtf8), CancellationToken.None);

        var transition = Assert.Single(transitions.Transitions);
        Assert.Equal("retained text", transition.Artifact.SearchText);
        Assert.Empty(transitions.Failures);
    }

    [Fact]
    public async Task Extract_fails_terminally_when_the_retained_artifact_checksum_is_invalid()
    {
        var transitions = new RecordingTransitionStore();
        var retainedRevisionId = SourceRevisionId.New();
        var worker = new ExtractUtf8StageWorker(
            new ThrowingSourceReader(),
            new ThrowingRetainedSourceReader(retainedRevisionId),
            new StubPipelineReader(new string('a', 64), inputText: null, retainedRevisionId),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(CreateWork(PipelineStage.Extract, PipelineOperations.ExtractUtf8), CancellationToken.None);

        var failure = Assert.Single(transitions.Failures);
        Assert.Equal(ExtractUtf8StageWorker.InvalidRetainedSourceReason, failure.Reason);
        Assert.Empty(transitions.Transitions);
    }

    [Fact]
    public async Task Document_extract_routes_native_pdf_text_to_normalisation_without_a_model_provider()
    {
        var transitions = new RecordingTransitionStore();
        var retainedRevisionId = SourceRevisionId.New();
        var pdf = new StubPdfDocumentExtractor(new DocumentExtractionResult(
            "native PDF text",
            IsComplete: true,
            [],
            [new DocumentExtractedPage(0, "native PDF text", RequiresOcr: false)]));
        var ocr = new RecordingDocumentOcrHandoff(DocumentOcrHandoffResult.Refused("pdf-ocr-required"));
        var worker = new ExtractDocumentStageWorker(
            new RetainedSourceReader(retainedRevisionId, new string('a', 64), "pdf bytes"),
            new StubPipelineReader(
                new string('a', 64),
                inputText: null,
                retainedRevisionId,
                DocumentProcessingInput.Pdf.Classification,
                DocumentProcessingInput.Pdf.Extension),
            new VsdxDocumentExtractor(),
            pdf,
            ocr,
            new EmptyDocumentOcrResultReader(),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(CreateWork(PipelineStage.Extract, PipelineOperations.ExtractDocument), CancellationToken.None);

        var transition = Assert.Single(transitions.Transitions);
        Assert.Equal("native PDF text", transition.Artifact.SearchText);
        Assert.Equal(PipelineStage.Normalise, transition.NextStage);
        Assert.Equal(0, Assert.Single(DocumentOcrProvenance.Parse(
            Assert.IsType<string>(transition.Artifact.DocumentMetadataJson)).Pages).PageIndex);
        Assert.Equal(1, pdf.Calls);
        Assert.Empty(transitions.Failures);
    }

    [Fact]
    public async Task Document_extract_keeps_a_blank_pdf_out_of_the_pipeline_until_ocr_is_available()
    {
        var transitions = new RecordingTransitionStore();
        var retainedRevisionId = SourceRevisionId.New();
        var pdf = new StubPdfDocumentExtractor(new DocumentExtractionResult(string.Empty, IsComplete: false, ["pdf-ocr-required"]));
        var ocr = new RecordingDocumentOcrHandoff(DocumentOcrHandoffResult.Refused("pdf-ocr-required"));
        var worker = new ExtractDocumentStageWorker(
            new RetainedSourceReader(retainedRevisionId, new string('a', 64), "pdf bytes"),
            new StubPipelineReader(
                new string('a', 64),
                inputText: null,
                retainedRevisionId,
                DocumentProcessingInput.Pdf.Classification,
                DocumentProcessingInput.Pdf.Extension),
            new VsdxDocumentExtractor(),
            pdf,
            ocr,
            new EmptyDocumentOcrResultReader(),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(CreateWork(PipelineStage.Extract, PipelineOperations.ExtractDocument), CancellationToken.None);

        var failure = Assert.Single(transitions.Failures);
        Assert.Equal("pdf-ocr-required", failure.Reason);
        Assert.Equal(1, pdf.Calls);
        Assert.Empty(transitions.Transitions);
    }

    [Fact]
    public async Task Document_extract_hands_only_uncovered_pdf_pages_to_ocr_without_emitting_an_extract_artifact()
    {
        var transitions = new RecordingTransitionStore();
        var retainedRevisionId = SourceRevisionId.New();
        var pdf = new StubPdfDocumentExtractor(new DocumentExtractionResult(
            "native first page",
            IsComplete: false,
            ["pdf-ocr-required"],
            [
                new DocumentExtractedPage(0, "native first page", RequiresOcr: false),
                new DocumentExtractedPage(1, string.Empty, RequiresOcr: true)
            ]));
        var ocr = new RecordingDocumentOcrHandoff(DocumentOcrHandoffResult.Queued(Guid.NewGuid()));
        var worker = new ExtractDocumentStageWorker(
            new RetainedSourceReader(retainedRevisionId, new string('a', 64), "pdf bytes"),
            new StubPipelineReader(
                new string('a', 64),
                inputText: null,
                retainedRevisionId,
                DocumentProcessingInput.Pdf.Classification,
                DocumentProcessingInput.Pdf.Extension),
            new VsdxDocumentExtractor(),
            pdf,
            ocr,
            new EmptyDocumentOcrResultReader(),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(CreateWork(PipelineStage.Extract, PipelineOperations.ExtractDocument), CancellationToken.None);

        var request = Assert.Single(ocr.Requests);
        Assert.Equal(retainedRevisionId, request.RetainedSourceRevisionId);
        Assert.Equal(new string('a', 64), request.ContentSha256);
        Assert.Equal([1], request.PageIndexes);
        Assert.Empty(transitions.Transitions);
        Assert.Empty(transitions.Failures);
    }

    [Fact]
    public async Task Normalise_uses_form_kc_and_lf_then_queues_canonical_index()
    {
        var transitions = new RecordingTransitionStore();
        var worker = new NormaliseTextStageWorker(
            new StubPipelineReader(
                new string('a', 64),
                "cafe\u0301\r\nline\r"),
            CreateTransitionService(transitions),
            new FixedTimeProvider());
        var work = CreateWork(PipelineStage.Normalise, PipelineOperations.NormaliseText);

        await worker.ExecuteAsync(work, CancellationToken.None);

        var transition = Assert.Single(transitions.Transitions);
        Assert.Equal("café\nline\n", transition.Artifact.SearchText);
        Assert.Equal(PipelineStage.CanonicalIndex, transition.NextStage);
        Assert.Equal(PipelineOperations.CanonicalIndex, transition.NextOperation);
    }

    [Fact]
    public async Task Normalise_preserves_document_page_provenance_after_text_normalisation()
    {
        var extracted = DocumentOcrProvenance.Merge(
            new DocumentExtractionResult(
                "cafe\u0301",
                IsComplete: false,
                ["pdf-ocr-required"],
                [
                    new DocumentExtractedPage(0, "cafe\u0301", RequiresOcr: false),
                    new DocumentExtractedPage(1, string.Empty, RequiresOcr: true)
                ]),
            new DocumentOcrExecutionResult(
                true,
                "document-ocr-complete",
                [new DocumentOcrPageResult(1, 0, [new DocumentOcrBlock("text", 1, 1, 10, 10, "second")])]));
        var transitions = new RecordingTransitionStore();
        var worker = new NormaliseTextStageWorker(
            new StubPipelineReader(
                new string('a', 64),
                extracted.Text,
                inputDocumentMetadataJson: extracted.MetadataJson),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(CreateWork(PipelineStage.Normalise, PipelineOperations.NormaliseText), CancellationToken.None);

        var transition = Assert.Single(transitions.Transitions);
        Assert.Equal("café\n\nsecond", transition.Artifact.SearchText);
        var provenance = DocumentOcrProvenance.Parse(Assert.IsType<string>(transition.Artifact.DocumentMetadataJson));
        Assert.Collection(
            provenance.Pages.OrderBy(static page => page.PageIndex),
            page => Assert.Equal(4, page.Length),
            page => Assert.Equal(6, page.StartOffset));
    }

    [Fact]
    public async Task Embed_preserves_chunk_identity_separately_from_vector_payload_integrity()
    {
        const string chunkText = "restart the native worker";
        var chunkContentHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(chunkText)));
        var transitions = new RecordingTransitionStore();
        var indexStore = new StubIndexGenerationStore(
            [new CanonicalTextChunk(41, 0, 0, chunkText.Length, chunkText, chunkContentHash)]);
        var worker = new EmbedStageWorker(
            indexStore,
            new DeterministicTokenHashEmbeddingProvider(),
            CreateTransitionService(transitions),
            new FixedTimeProvider());

        await worker.ExecuteAsync(
            CreateWork(PipelineStage.Embed, PipelineOperations.Embed),
            CancellationToken.None);

        var transition = Assert.Single(transitions.Transitions);
        Assert.NotNull(transition.IndexingOutput);
        var vector = Assert.Single(transition.IndexingOutput.Vectors!);

        Assert.Equal(chunkContentHash, vector.TextChunkContentHash);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(vector.Values)),
            vector.PayloadChecksum);
        Assert.NotEqual(vector.TextChunkContentHash, vector.PayloadChecksum);
    }

    [Fact]
    public async Task Status_is_not_published_when_the_sql_transition_does_not_commit()
    {
        var publisher = new RecordingStatusPublisher();
        var service = new StageTransitionService(
            new ThrowingTransitionStore(),
            publisher,
            new RecordingWakeSignal(),
            new FixedTimeProvider());
        var work = CreateWork(PipelineStage.Extract, PipelineOperations.ExtractUtf8);
        var request = new StageTransitionRequest(
            work.DispatchMessage,
            work.Job,
            new StageArtifact(
                Guid.NewGuid(),
                PipelineStage.Extract,
                new string('a', 64),
                "text/plain",
                "text",
                Now),
            PipelineStage.Normalise,
            PipelineOperations.NormaliseText,
            "test");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.TransitionAsync(request, CancellationToken.None));

        Assert.Empty(publisher.Events);
    }

    private static StageWorkItem CreateWork(PipelineStage stage, string operation)
    {
        var recordId = PipelineRecordId.New();
        return new StageWorkItem(
            new ClaimedDispatchMessage(
                DispatchMessageId.New(),
                recordId,
                1,
                stage,
                operation,
                0,
                $"{recordId.Value:N}:1:{stage}:0",
                Now,
                "dispatcher",
                Now.AddMinutes(1),
                1),
            new ClaimedJob(
                JobId.New(),
                recordId,
                1,
                stage,
                operation,
                PublicJobState.WorkerProcessing,
                Now,
                1,
                "worker",
                Now.AddMinutes(1),
                1));
    }

    private static StageTransitionService CreateTransitionService(
        RecordingTransitionStore store) =>
        new(
            store,
            new RecordingStatusPublisher(),
            new RecordingWakeSignal(),
            new FixedTimeProvider());

    private sealed class StubSourceReader(string contentHash, string text)
        : IUtf8FileSourceReader
    {
        public ValueTask<Utf8FileSource> ReadAsync(
            string suppliedPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new Utf8FileSource(suppliedPath, [], text, contentHash));
    }

    private sealed class StubPipelineReader(
        string registeredHash,
        string? inputText,
        SourceRevisionId? retainedSourceRevisionId = null,
        string? retainedSourceClassification = null,
        string? retainedSourceExtension = null,
        string? inputDocumentMetadataJson = null)
        : IPipelineStageReader
    {
        public ValueTask<PipelineStageSource> ReadStageSourceAsync(
            PipelineRecordId pipelineRecordId,
            long sourceRevision,
            PipelineStage stage,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new PipelineStageSource(
                    pipelineRecordId,
                    sourceRevision,
                    "C:\\ingress\\a.txt",
                    registeredHash,
                    inputText,
                    retainedSourceRevisionId,
                    retainedSourceClassification,
                    retainedSourceExtension,
                    inputDocumentMetadataJson));
    }

    private sealed class StubPdfDocumentExtractor(DocumentExtractionResult result) : IPdfDocumentExtractor
    {
        public int Calls { get; private set; }

        public ValueTask<DocumentExtractionResult> ExtractAsync(
            RetainedSourceBytes retained,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingDocumentOcrHandoff(DocumentOcrHandoffResult result) : IDocumentOcrHandoff
    {
        public List<DocumentOcrHandoffRequest> Requests { get; } = [];

        public ValueTask<DocumentOcrHandoffResult> HandoffAsync(
            DocumentOcrHandoffRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingSourceReader : IUtf8FileSourceReader
    {
        public ValueTask<Utf8FileSource> ReadAsync(string suppliedPath, CancellationToken cancellationToken) =>
            ValueTask.FromException<Utf8FileSource>(new FileNotFoundException("The original source path was renamed."));
    }

    private sealed class RetainedSourceReader(SourceRevisionId revisionId, string contentHash, string text)
        : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(revisionId, sourceRevisionId);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            return ValueTask.FromResult(new RetainedSourceBytes(sourceRevisionId, bytes, contentHash, bytes.Length));
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(revisionId, sourceRevisionId);
            return ValueTask.FromResult(new Utf8FileSource("retained", [], text, contentHash));
        }
    }

    private sealed class ThrowingRetainedSourceReader(SourceRevisionId revisionId) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(revisionId, sourceRevisionId);
            return ValueTask.FromException<RetainedSourceBytes>(new InvalidDataException("checksum mismatch"));
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(revisionId, sourceRevisionId);
            return ValueTask.FromException<Utf8FileSource>(new InvalidDataException("checksum mismatch"));
        }
    }

    private sealed class StubIndexGenerationStore(
        IReadOnlyList<CanonicalTextChunk> chunks) : IIndexGenerationStore
    {
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(
            PipelineRecordId pipelineRecordId,
            long sourceRevision,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(chunks);

        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(
            Guid indexGenerationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(
            Guid indexGenerationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Guid?> GetActiveGenerationIdAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask UpdateGenerationMetadataAsync(
            IndexGenerationDescriptor generation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTransitionStore : IStageTransitionStore
    {
        public List<StageTransitionRequest> Transitions { get; } = [];
        public List<StageFailureRequest> Failures { get; } = [];

        public ValueTask<StageTransitionResult> TransitionAsync(
            StageTransitionRequest request,
            CancellationToken cancellationToken)
        {
            Transitions.Add(request);
            return ValueTask.FromResult(
                new StageTransitionResult(
                    request.Artifact.Id,
                    request.NextStage is null ? null : JobId.New(),
                    request.NextStage is null ? null : DispatchMessageId.New(),
                    false));
        }

        public ValueTask FailAsync(
            StageFailureRequest request,
            CancellationToken cancellationToken)
        {
            Failures.Add(request);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingTransitionStore : IStageTransitionStore
    {
        public ValueTask<StageTransitionResult> TransitionAsync(
            StageTransitionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<StageTransitionResult>(
                new InvalidOperationException("not committed"));

        public ValueTask FailAsync(
            StageFailureRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingStatusPublisher : IStatusEventPublisher
    {
        public List<StatusChanged> Events { get; } = [];

        public ValueTask PublishAsync(
            StatusChanged statusChanged,
            CancellationToken cancellationToken)
        {
            Events.Add(statusChanged);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWakeSignal : IOutboxWakeSignal
    {
        public void Notify()
        {
        }

        public ValueTask WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
