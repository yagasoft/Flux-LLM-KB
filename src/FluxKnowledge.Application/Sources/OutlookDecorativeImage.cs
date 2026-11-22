using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Excludes confirmed tracking or spacer pixels from Outlook image processing.</summary>
public static class OutlookDecorativeImage
{
    public const string Reason = "outlook-decorative-image";
    public const string RetentionEvidence = "{\"reason\":\"" + Reason + "\"}";

    internal const int MaximumProbeBytes = 64 * 1024;

    internal static bool IsSinglePixel(MediaMetadataParseResult image, ReadOnlySpan<byte> bytes) =>
        image.CompleteRasterMetadata && image.Width == 1 && image.Height == 1 && image.Format switch
        {
            MediaMetadataFormat.Jpeg => bytes.IndexOf("MPF\0"u8) < 0,
            MediaMetadataFormat.Png => bytes.IndexOf("acTL"u8) < 0,
            MediaMetadataFormat.Gif => image.SingleGifFrame && bytes is [.., 0x3b],
            MediaMetadataFormat.Bmp => true,
            _ => false
        };

    internal static bool IsSmallRasterSinglePixel(byte[] bytes, MediaMetadataFormat format, CancellationToken cancellationToken)
    {
        // Larger files proceed to OCR without an extra metadata scan.
        if (bytes.Length > MaximumProbeBytes) return false;
        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            return IsSinglePixel(new MetadataExtractorMediaMetadataParser().Parse(stream, format, cancellationToken), bytes);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or MetadataExtractor.ImageProcessingException)
        {
            return false;
        }
    }

    public static async ValueTask<bool> VerifyRetainedRasterAsync(IRetainedSourceReader reader, SourceRevisionId revisionId,
        string sha256, long byteLength, string extension, bool useGifSignature, CancellationToken cancellationToken)
    {
        if (byteLength is <= 0 or > MaximumProbeBytes) return false;
        try
        {
            await using var retained = await reader.OpenReadAsync(revisionId, MaximumProbeBytes, cancellationToken)
                .ConfigureAwait(false);
            if (retained.SourceRevisionId != revisionId || retained.ByteLength != byteLength ||
                !string.Equals(retained.ContentSha256, sha256, StringComparison.Ordinal)) return false;
            var bytes = new byte[(int)byteLength];
            await retained.Stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (!MediaMetadataSignature.TryDetect(bytes, cancellationToken, out var format)) return false;
            var matches = useGifSignature ? format == MediaMetadataFormat.Gif :
                MediaMetadataSignature.TryGetFormatForExtension(extension, out var expected) && expected == format &&
                format is MediaMetadataFormat.Jpeg or MediaMetadataFormat.Png;
            return matches && IsSmallRasterSinglePixel(bytes, format, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static RetainedProcessorCompletion Skip(string inputSha256, long byteLength)
    {
        var receipt = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Reason}:{inputSha256}:{byteLength}")));
        return new RetainedProcessorCompletion([], receipt, [new(inputSha256, byteLength, "skipped", Reason)]);
    }
}
