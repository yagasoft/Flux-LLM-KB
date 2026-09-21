using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// The sole derived-source contract allowed to retain a document container by
/// reference for a document Extract worker. It is deliberately not UTF-8 text.
/// </summary>
public static class DocumentProcessingInput
{
    public const string Classification = "DocumentProcessingInput";
    public const string PdfClassification = "PdfDocumentProcessingInput";
    public const string VisioClassification = "VisioDocumentProcessingInput";
    public const string VisioProcessorVersion = "phase-6-vsdx-visio-document-v2";
    public const string VisioProcessorFingerprint = "phase-6-vsdx-visio-input-v2";
    public const string VsdxProcessorVersion = "phase-6-vsdx-document-v1";
    public const string VsdxProcessorFingerprint = "phase-6-vsdx-document-input-v1";
    public const string VsdxOutputContract = "pipeline:extract-document-vsdx";
    public const string PdfProcessorVersion = "phase-6-pdf-document-v1";
    public const string PdfProcessorFingerprint = "phase-6-pdf-document-input-v1";
    public const string PdfOutputContract = "pipeline:extract-document-pdf";
    public const string ImageClassification = "ImageDocumentProcessingInput";
    public const string ImageProcessorVersion = "phase-6-image-document-v1";
    public const string ImageProcessorFingerprint = "phase-6-image-document-input-v1";
    public const string ImageOutputContract = "pipeline:extract-document-image";

    public static readonly DocumentProcessingContract Vsdx = new(
        Classification,
        ".vsdx",
        VsdxProcessorVersion,
        VsdxProcessorFingerprint,
        "phase-6-vsdx-structural-v1",
        "phase-6-vsdx-retained-structural-v1");

    public static readonly DocumentProcessingContract Pdf = new(
        PdfClassification,
        ".pdf",
        PdfProcessorVersion,
        PdfProcessorFingerprint,
        "phase-6-pdf-structural-v1",
        "phase-6-pdf-retained-structural-v1");

    public static readonly DocumentProcessingContract Visio = new(
        VisioClassification, ".vsdx", VisioProcessorVersion, VisioProcessorFingerprint,
        "phase-6-vsdx-visio-v2", "phase-6-vsdx-retained-visio-v2");

    public static readonly DocumentProcessingContract Jpeg = new(
        ImageClassification, ".jpg", ImageProcessorVersion, ImageProcessorFingerprint,
        "phase-6-image-ocr-v1", "phase-6-image-retained-ocr-v1");

    public static readonly DocumentProcessingContract Png = Jpeg with { Extension = ".png" };

    public static RetainedProcessorDerivedChild CreateVisioChild(RetainedProcessorClaim claim, RetainedSourceBytes retained) =>
        CreateChild(claim, retained, Visio);

    public static RetainedProcessorDerivedChild CreateVsdxChild(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(retained);

        return CreateChild(claim, retained, Vsdx);
    }

    public static RetainedProcessorDerivedChild CreatePdfChild(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained)
    {
        return CreateChild(claim, retained, Pdf);
    }

    public static RetainedProcessorDerivedChild CreateImageChild(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        string extension) => CreateChild(
            claim,
            retained,
            string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ? Png : Jpeg);

    public static bool TryGetContract(
        string classification,
        string extension,
        out DocumentProcessingContract contract)
    {
        if (string.Equals(classification, Visio.Classification, StringComparison.Ordinal) &&
            string.Equals(extension, Visio.Extension, StringComparison.OrdinalIgnoreCase))
        {
            contract = Visio;
            return true;
        }

        if (string.Equals(classification, Vsdx.Classification, StringComparison.Ordinal) &&
            string.Equals(extension, Vsdx.Extension, StringComparison.OrdinalIgnoreCase))
        {
            contract = Vsdx;
            return true;
        }

        if (string.Equals(classification, Pdf.Classification, StringComparison.Ordinal) &&
            string.Equals(extension, Pdf.Extension, StringComparison.OrdinalIgnoreCase))
        {
            contract = Pdf;
            return true;
        }


        if (string.Equals(classification, ImageClassification, StringComparison.Ordinal) &&
            (string.Equals(extension, Jpeg.Extension, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, Png.Extension, StringComparison.OrdinalIgnoreCase)))
        {
            contract = string.Equals(extension, Png.Extension, StringComparison.OrdinalIgnoreCase) ? Png : Jpeg;
            return true;
        }

        contract = null!;
        return false;
    }

    private static RetainedProcessorDerivedChild CreateChild(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        DocumentProcessingContract contract)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(retained);
        var memberFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"document-input:{claim.ParentStableIdentity.Length}:{claim.ParentStableIdentity}:{contract.ProcessorFingerprint}")));
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"document-input-identity:{claim.ParentStableIdentity.Length}:{claim.ParentStableIdentity}:{memberFingerprint}")));
        return new RetainedProcessorDerivedChild(
            memberFingerprint,
            $"retained-document-input:{memberFingerprint}",
            identity,
            retained.ContentSha256,
            string.Empty,
            retained.ByteLength,
            contract.Classification,
            OriginKind: 2,
            Extension: contract.Extension);
    }
}

public sealed record DocumentProcessingContract(
    string Classification,
    string Extension,
    string ProcessorVersion,
    string ProcessorFingerprint,
    string ParentProcessorVersion,
    string ParentProcessorFingerprint);
