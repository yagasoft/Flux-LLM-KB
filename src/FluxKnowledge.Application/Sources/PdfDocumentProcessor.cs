using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>
/// Creates one hidden retained PDF input for the dedicated document worker.
/// It verifies only immutable identity and the PDF signature here; parsing and
/// licensing happen later in the document worker, never on an archive member.
/// </summary>
public sealed class PdfDocumentProcessor(IRetainedArtifactWriter artifactWriter) : ILocalSourceCapabilityHandler
{
    public const long MaximumInputBytes = 64L * 1024 * 1024;

    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("05e7f8e7-0efb-4a39-a7d1-e7af0583f924"),
        "document-pdf-structural-extract",
        DocumentProcessingInput.Pdf.ParentProcessorVersion,
        ExecutionClass.InProcess,
        DocumentProcessingInput.Pdf.ParentProcessorFingerprint,
        SourceActivityKind.TextExtraction,
        "PdfDocumentContainer",
        "retained:document-pdf-structural-extract");

    public SourceCapabilityDescriptor Descriptor => Capability;

    public static bool IsLikelyPdf(RetainedProcessorPromotionCandidate candidate, ReadOnlySpan<byte> bytes) =>
        candidate.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && bytes.StartsWith("%PDF-"u8);

    public ValueTask<RetainedProcessorCompletion> ProcessAsync(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        RetainedProcessorOptions options,
        CancellationToken cancellationToken)
    {
        _ = artifactWriter;
        _ = options;
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(retained.ContentSha256, claim.InputSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(retained.Bytes)), claim.InputSha256, StringComparison.Ordinal))
        {
            throw new RetainedProcessorException("retained-artifact-checksum-invalid");
        }
        if (retained.ByteLength > MaximumInputBytes)
        {
            throw new RetainedProcessorException("pdf-document-input-too-large");
        }
        if (!retained.Bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new RetainedProcessorException("pdf-document-container-invalid");
        }

        var input = DocumentProcessingInput.CreatePdfChild(claim, retained);
        var receiptFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"completed:{input.MemberFingerprint}:{input.ContentSha256}:{input.ByteLength}")));
        return ValueTask.FromResult(new RetainedProcessorCompletion([input], receiptFingerprint));
    }
}

public sealed class PdfDocumentCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => PdfDocumentProcessor.Capability;
}
