using System.Text;

namespace FluxKnowledge.Application.Sources;

public enum SourceClassification
{
    AcceptedUtf8Text,
    DeferredCapability,
    DeferredPolicy,
    Unknown
}

public sealed record SourceClassificationResult(
    SourceClassification Classification,
    string? Text,
    string? Reason)
{
    public bool IsAccepted => Classification == SourceClassification.AcceptedUtf8Text;
}

/// <summary>Classifies local bytes before any extension-based text processing occurs.</summary>
public static class SourceClassifier
{
    public const long MaximumAcceptedTextBytes = 16L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".fs", ".vb", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".c", ".h", ".cpp", ".go", ".rs", ".php", ".rb", ".sh", ".ps1", ".sql"
    };

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static SourceClassificationResult Classify(
        string fileName,
        ReadOnlySpan<byte> bytes,
        long declaredByteLength,
        bool hasFullBoundedBuffer = true,
        long maximumAcceptedTextBytes = MaximumAcceptedTextBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (declaredByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredByteLength));
        }

        if (maximumAcceptedTextBytes <= 0 || maximumAcceptedTextBytes > MaximumAcceptedTextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAcceptedTextBytes));
        }

        var extension = Path.GetExtension(fileName);
        if (HasBinarySignature(bytes))
        {
            if (TextExtensions.Contains(extension))
            {
                return new SourceClassificationResult(SourceClassification.Unknown, null, "File signature conflicts with its text extension.");
            }
            return DeferredCapability(BinarySignatureReason(extension, bytes));
        }

        if (HasBinaryControlBytes(bytes))
        {
            return DeferredPolicy("File bytes contain binary control values and are not accepted as text.");
        }

        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (declaredByteLength > maximumAcceptedTextBytes)
            {
                return DeferredPolicy("File exceeds the effective UTF-8 text ingestion limit.");
            }
            if (!hasFullBoundedBuffer || bytes.Length != declaredByteLength)
            {
                return DeferredPolicy("Read byte count does not match the discovered file size.");
            }
            try
            {
                return new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, StrictUtf8.GetString(RemoveUtf8Bom(bytes)), null);
            }
            catch (DecoderFallbackException)
            {
                return DeferredPolicy("File bytes are not valid UTF-8.");
            }
        }

        if (CodeExtensions.Contains(extension))
        {
            return DeferredPolicy("Source code ingestion is not enabled for this root.");
        }

        if (!TextExtensions.Contains(extension))
        {
            return new SourceClassificationResult(SourceClassification.Unknown, null, "File extension is unknown to local source ingestion.");
        }

        if (declaredByteLength > maximumAcceptedTextBytes)
        {
            return DeferredPolicy("File exceeds the effective UTF-8 text ingestion limit.");
        }

        if (!hasFullBoundedBuffer || bytes.Length != declaredByteLength)
        {
            return DeferredPolicy("Read byte count does not match the discovered file size.");
        }

        try
        {
            var text = StrictUtf8.GetString(RemoveUtf8Bom(bytes));
            return new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, text, null);
        }
        catch (DecoderFallbackException)
        {
            return DeferredPolicy("File bytes are not valid UTF-8.");
        }
    }

    private static SourceClassificationResult DeferredCapability(string reason) =>
        new(SourceClassification.DeferredCapability, null, reason);

    private static SourceClassificationResult DeferredPolicy(string reason) =>
        new(SourceClassification.DeferredPolicy, null, reason);

    private static string BinarySignatureReason(string extension, ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8)) return "pdf-parser-unavailable";
        if (bytes.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
        {
            return extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase)
                ? "legacy-office-binary-parser-unavailable"
                : "compound-binary-parser-unavailable";
        }
        if (ZipArchiveRetainedProcessor.IsZipSignature(bytes))
        {
            return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
                ? "ooxml-structural-extraction-pending"
                : "archive-zip-expansion-pending";
        }
        if (TarArchiveRetainedProcessor.IsTarSignature(bytes)) return "archive-tar-expansion-pending";
        if (MediaMetadataSignature.IsRecognisedUnsupportedMediaSignature(bytes))
            return "media-metadata-format-unsupported";
        if (MediaMetadataSignature.TryDetect(bytes, out _) ||
            bytes.StartsWith("GIF87a"u8) ||
            bytes.StartsWith("GIF89a"u8) ||
            bytes.StartsWith("RIFF"u8) ||
            bytes.StartsWith("ID3"u8) ||
            bytes.StartsWith("OggS"u8) ||
            bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))
            return "media-metadata-extraction-pending";
        return "binary-format-parser-unavailable";
    }

    private static ReadOnlySpan<byte> RemoveUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes[3..] : bytes;

    private static bool HasBinarySignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("%PDF-"u8) ||
        bytes.StartsWith(new byte[] { 0x50, 0x4b, 0x03, 0x04 }) ||
        TarArchiveRetainedProcessor.IsTarSignature(bytes) ||
        bytes.StartsWith(new byte[] { 0x1f, 0x8b }) ||
        bytes.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }) ||
        bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 }) ||
        bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ||
        bytes.StartsWith("GIF87a"u8) ||
        bytes.StartsWith("GIF89a"u8) ||
        bytes.StartsWith("RIFF"u8) ||
        bytes.StartsWith("ID3"u8) ||
        bytes.StartsWith("OggS"u8) ||
        bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) ||
        MediaMetadataSignature.IsRecognisedUnsupportedMediaSignature(bytes) ||
        MediaMetadataSignature.TryDetect(bytes, out _);

    private static bool HasBinaryControlBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == 0 || value is < 0x08 or > 0x0d and < 0x20)
            {
                return true;
            }
        }

        return false;
    }
}
