using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Creates one hidden retained JPEG/PNG input for the existing local document OCR path.</summary>
public sealed class ImageDocumentInputProcessor : ILocalSourceCapabilityHandler
{
    public const long MaximumInputBytes = 64L * 1024 * 1024;

    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("bdc31eae-7628-48ee-942b-59c3ff4c361f"),
        "document-image-ocr-extract",
        DocumentProcessingInput.Jpeg.ParentProcessorVersion,
        ExecutionClass.InProcess,
        DocumentProcessingInput.Jpeg.ParentProcessorFingerprint,
        SourceActivityKind.TextExtraction,
        "ImageDocumentContainer",
        "retained:document-image-ocr-extract");

    public SourceCapabilityDescriptor Descriptor => Capability;

    public static bool IsLikelyImage(RetainedProcessorPromotionCandidate candidate, ReadOnlySpan<byte> bytes) =>
        (candidate.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
         candidate.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) && IsJpeg(bytes) ||
        candidate.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && IsPng(bytes);

    public ValueTask<RetainedProcessorCompletion> ProcessAsync(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        RetainedProcessorOptions options,
        CancellationToken cancellationToken)
    {
        _ = options;
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(retained.ContentSha256, claim.InputSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(retained.Bytes)), claim.InputSha256, StringComparison.Ordinal))
            throw new RetainedProcessorException("retained-artifact-checksum-invalid");
        if (retained.ByteLength > MaximumInputBytes)
            throw new RetainedProcessorException("image-document-input-too-large");
        var extension = IsPng(retained.Bytes) ? ".png" : IsJpeg(retained.Bytes) ? ".jpg" : null;
        if (extension is null)
            throw new RetainedProcessorException("image-document-container-invalid");
        var input = DocumentProcessingInput.CreateImageChild(claim, retained, extension);
        var receipt = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"completed:{input.MemberFingerprint}:{input.ContentSha256}:{input.ByteLength}")));
        return ValueTask.FromResult(new RetainedProcessorCompletion([input], receipt));
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) => bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff });
}

public sealed class ImageDocumentInputCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => ImageDocumentInputProcessor.Capability;
}
