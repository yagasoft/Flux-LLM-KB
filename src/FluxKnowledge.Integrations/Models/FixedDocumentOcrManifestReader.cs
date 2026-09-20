using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Integrations.Models;

public sealed class FixedDocumentOcrManifestReader
{
    public const string ManifestDirectory = @"J:\Models\manifests\document-ocr-20260919";

    private readonly Func<string, int, CancellationToken, Task<byte[]>> _readLocalManifestAsync;

    private FixedDocumentOcrManifestReader(
        Func<string, int, CancellationToken, Task<byte[]>> readLocalManifestAsync)
    {
        _readLocalManifestAsync = readLocalManifestAsync ?? throw new ArgumentNullException(nameof(readLocalManifestAsync));
    }

    public static FixedDocumentOcrManifestReader OpenProduction() => new(WindowsModelVerificationFiles.ReadLocalManifestAsync);

    internal FixedDocumentOcrManifestReader(
        Func<string, int, CancellationToken, Task<byte[]>> readLocalManifestAsync,
        bool testOnly = true)
        : this(readLocalManifestAsync)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(true, testOnly);
    }

    public async ValueTask<ModelBundleSpecification> ReadAsync(
        DocumentOcrModelRole role,
        CancellationToken cancellationToken)
    {
        var bytes = await _readLocalManifestAsync(
                Path.Combine(ManifestDirectory, FilenameFor(role)),
                ModelManifestCodec.MaximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return ModelManifestCodec.Parse(bytes);
    }

    private static string FilenameFor(DocumentOcrModelRole role) => role switch
    {
        DocumentOcrModelRole.TextDetection => "text-detection.json",
        DocumentOcrModelRole.EnglishRecognition => "english-recognition.json",
        DocumentOcrModelRole.ArabicRecognition => "arabic-recognition.json",
        DocumentOcrModelRole.PageOrientation => "page-orientation.json",
        DocumentOcrModelRole.LineOrientation => "line-orientation.json",
        DocumentOcrModelRole.Layout => "layout.json",
        DocumentOcrModelRole.Tables => "tables.json",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown document OCR model role.")
    };
}
