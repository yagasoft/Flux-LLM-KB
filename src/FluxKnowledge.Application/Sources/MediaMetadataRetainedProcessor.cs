using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using MetadataExtractor;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.Wav;
using MetadataExtractor.Formats.Mpeg;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Bmp;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Exif;
using MetadataDirectory = MetadataExtractor.Directory;

namespace FluxKnowledge.Application.Sources;

/// <summary>Produces one bounded, structural-only JSON manifest from checksum-verified retained media bytes.</summary>
public sealed class MediaMetadataRetainedProcessor(
    IRetainedArtifactWriter artifactWriter,
    ILocalPrivateContentDisclosure disclosure,
    IMediaMetadataParser? parser = null)
{
    public const long MaximumInputBytes = 64L * 1024 * 1024;
    public const long MaximumParserReadBytes = 96L * 1024 * 1024;
    public const int MaximumMetadataDirectories = 128;
    public const int MaximumManifestUtf8Bytes = 1_024;
    private const string ParserUnavailable = "media-metadata-parser-unavailable";
    private readonly IMediaMetadataParser _parser = parser ?? new MetadataExtractorMediaMetadataParser();

    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("9a956b70-7f10-416a-ab0d-1f5b817988f0"),
        "media-metadata",
        "phase-5-media-metadata-v1",
        ExecutionClass.InProcess,
        "phase-5-media-metadata-retained-v1",
        SourceActivityKind.TextExtraction,
        "MediaMetadata",
        "retained:media-metadata-v1");

    public MediaMetadataParserPreflight Preflight() => Preflight(CancellationToken.None);

    public MediaMetadataParserPreflight Preflight(CancellationToken cancellationToken)
    {
        try
        {
            return _parser.Preflight();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new MediaMetadataParserPreflight(false, ParserUnavailable);
        }
    }

    /// <summary>Checks the extension-led promotion fence without reading a source path.</summary>
    public static bool HasMatchingSupportedSignature(string fileNameOrExtension, ReadOnlySpan<byte> bytes, out string? outcomeCode)
    {
        if (!MediaMetadataSignature.TryGetFormatForExtension(fileNameOrExtension, out var expected))
        {
            outcomeCode = MediaMetadataSignature.IsRecognisedUnsupportedMediaExtension(fileNameOrExtension) &&
                MediaMetadataSignature.IsRecognisedUnsupportedMediaSignature(bytes) &&
                !MediaMetadataSignature.TryDetect(bytes, out _)
                ? "media-metadata-format-unsupported"
                : "media-metadata-signature-mismatch";
            return false;
        }
        if (!MediaMetadataSignature.TryDetect(bytes, out var actual) || actual != expected)
        {
            outcomeCode = "media-metadata-signature-mismatch";
            return false;
        }
        outcomeCode = null;
        return true;
    }

    public async ValueTask<RetainedProcessorCompletion> ProcessAsync(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(retained);
        cancellationToken.ThrowIfCancellationRequested();
        if (retained.ByteLength > MaximumInputBytes || retained.Bytes.LongLength > MaximumInputBytes)
            throw new RetainedProcessorException("media-metadata-input-too-large");
        if (retained.SourceRevisionId != claim.SourceRevisionId || retained.ByteLength != retained.Bytes.LongLength ||
            !string.Equals(retained.ContentSha256, claim.InputSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(retained.Bytes)), claim.InputSha256, StringComparison.Ordinal))
        {
            throw new RetainedProcessorException("retained-artifact-checksum-invalid");
        }
        if (!MediaMetadataSignature.TryDetect(retained.Bytes, cancellationToken, out var format))
            throw new RetainedProcessorException("media-metadata-format-unsupported");

        var preflight = Preflight(cancellationToken);
        if (!preflight.IsAvailable)
            throw new RetainedProcessorException(preflight.ReasonCode ?? ParserUnavailable);

        MediaMetadataParseResult parsed;
        try
        {
            await using var input = new MemoryStream(retained.Bytes, writable: false);
            await using var bounded = new BoundedReadStream(input, MaximumParserReadBytes, cancellationToken);
            parsed = _parser.Parse(bounded, format, cancellationToken);
        }
        catch (ParserReadLimitException exception)
        {
            throw new RetainedProcessorException("media-metadata-read-limit", innerException: exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RetainedProcessorException("media-metadata-parser-failed", innerException: exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (parsed.Format != format || !IsSafeContainer(parsed.Container) || parsed.Audio is { SampleRateHz: <= 0 } or { Channels: <= 0 })
            throw new RetainedProcessorException("media-metadata-parser-failed");
        var manifest = CanonicalManifest(parsed, cancellationToken);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        if (manifestBytes.Length > MaximumManifestUtf8Bytes)
            throw new RetainedProcessorException("media-metadata-output-limit");

        LocalDisclosureResult disclosed;
        try
        {
            disclosed = disclosure.Evaluate(manifest, LocalDisclosureKind.RetainedDetail);
        }
        catch (Exception exception)
        {
            throw new RetainedProcessorException("media-metadata-secret-content-withheld", innerException: exception);
        }
        if (disclosed.Withheld || !string.Equals(disclosed.Value, manifest, StringComparison.Ordinal))
            throw new RetainedProcessorException("media-metadata-secret-content-withheld");

        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = Hash($"media-metadata-manifest:{claim.ParentStableIdentity.Length}:{claim.ParentStableIdentity}:{Capability.ProcessorFingerprint}");
        var identity = Hash($"media-metadata-manifest-identity:{claim.ParentStableIdentity.Length}:{claim.ParentStableIdentity}:{fingerprint}");
        await using var manifestStream = new MemoryStream(manifestBytes, writable: false);
        var receipt = await artifactWriter.WriteAsync(claim.SourceRevisionId, manifestStream, MaximumManifestUtf8Bytes, cancellationToken).ConfigureAwait(false);
        if (receipt.ByteLength != manifestBytes.Length || !receipt.IsUtf8Text || receipt.IsNestedArchive)
            throw new RetainedProcessorException("media-metadata-output-invalid");

        var child = new RetainedProcessorDerivedChild(
            fingerprint,
            $"retained-media-metadata:{fingerprint}",
            identity,
            receipt.ContentSha256,
            receipt.StoreRelativePath,
            receipt.ByteLength,
            "AcceptedUtf8Text",
            OriginKind: 3,
            Extension: ".json");
        return new RetainedProcessorCompletion([child], Hash($"{child.MemberFingerprint}:{child.ContentSha256}:{child.ByteLength}"));
    }

    private static string CanonicalManifest(MediaMetadataParseResult parsed, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            json.WriteStartObject();
            json.WriteString("schema", "media-metadata-v1");
            json.WriteString("format", FormatName(parsed.Format));
            json.WriteString("container", parsed.Container);
            json.WritePropertyName("dimensions");
            if (parsed.Width is > 0 && parsed.Height is > 0)
            {
                json.WriteStartObject();
                json.WriteNumber("width", parsed.Width.Value);
                json.WriteNumber("height", parsed.Height.Value);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNullValue();
            }
            json.WritePropertyName("duration_ms");
            if (parsed.DurationMilliseconds is >= 0) json.WriteNumberValue(parsed.DurationMilliseconds.Value); else json.WriteNullValue();
            json.WritePropertyName("audio");
            if (parsed.Audio is { } audio)
            {
                json.WriteStartObject();
                json.WriteNumber("sample_rate_hz", audio.SampleRateHz);
                json.WriteNumber("channels", audio.Channels);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNullValue();
            }
            json.WriteEndObject();
            json.Flush();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool IsSafeContainer(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string FormatName(MediaMetadataFormat format) => format.ToString().ToLowerInvariant();

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class BoundedReadStream(Stream inner, long maximumReadBytes, CancellationToken cancellationToken) : Stream
    {
        private long _readBytes;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, Allow(count));
            _readBytes += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer[..Allow(buffer.Length)]);
            _readBytes += read;
            return read;
        }

        public override int ReadByte()
        {
            cancellationToken.ThrowIfCancellationRequested();
            Allow(1);
            var value = inner.ReadByte();
            if (value >= 0) _readBytes++;
            return value;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ignored = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await inner.ReadAsync(buffer[..Allow(buffer.Length)], cancellationToken).ConfigureAwait(false);
            _readBytes += read;
            return read;
        }

        private int Allow(int requested)
        {
            var remaining = maximumReadBytes - _readBytes;
            if (remaining <= 0) throw new ParserReadLimitException();
            return checked((int)Math.Min(requested, remaining));
        }
    }

    private sealed class ParserReadLimitException : IOException;
}

/// <summary>Small local seam for deterministic preflight and hostile-stream tests; it is not a registered parser service.</summary>
public interface IMediaMetadataParser
{
    MediaMetadataParserPreflight Preflight();
    MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken);
}

public sealed record MediaMetadataParserPreflight(bool IsAvailable, string? ReasonCode);

public sealed record MediaMetadataAudioFacts(int SampleRateHz, int Channels);

/// <summary>Only structural fields reach the manifest; ignored fields intentionally never participate in serialisation.</summary>
public sealed record MediaMetadataParseResult(
    MediaMetadataFormat Format,
    string Container,
    int? Width,
    int? Height,
    long? DurationMilliseconds,
    MediaMetadataAudioFacts? Audio,
    IReadOnlyList<MediaMetadataIgnoredField>? IgnoredFields = null)
{
    public static MediaMetadataParseResult Png(int width, int height, IReadOnlyList<MediaMetadataIgnoredField>? ignoredFields = null) =>
        new(MediaMetadataFormat.Png, "png", width, height, null, null, ignoredFields);
}

public sealed record MediaMetadataIgnoredField(string FieldName, string Value);

internal sealed class MetadataExtractorMediaMetadataParser : IMediaMetadataParser
{
    private static readonly byte[] PreflightPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Jj7kAAAAASUVORK5CYII=");

    public MediaMetadataParserPreflight Preflight()
    {
        try
        {
            var assembly = typeof(ImageMetadataReader).Assembly.GetName();
            if (!string.Equals(assembly.Name, "MetadataExtractor", StringComparison.Ordinal) ||
                assembly.Version != new Version(2, 9, 3, 0) ||
                !string.IsNullOrEmpty(assembly.CultureName) ||
                !assembly.GetPublicKeyToken().AsSpan().SequenceEqual(Convert.FromHexString("B66B5CCAF776C301")))
                return new MediaMetadataParserPreflight(false, "media-metadata-parser-unavailable");
            using var input = new MemoryStream(PreflightPng, writable: false);
            var directory = ImageMetadataReader.ReadMetadata(input, "preflight.png").OfType<PngDirectory>().SingleOrDefault();
            if (directory?.GetObject(PngDirectory.TagImageWidth) is not int width || width != 1 ||
                directory.GetObject(PngDirectory.TagImageHeight) is not int height || height != 1)
            {
                return new MediaMetadataParserPreflight(false, "media-metadata-parser-unavailable");
            }
            return new MediaMetadataParserPreflight(true, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException or FileNotFoundException or TypeLoadException or BadImageFormatException)
        {
            return new MediaMetadataParserPreflight(false, "media-metadata-parser-unavailable");
        }
    }

    public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = ImageMetadataReader.ReadMetadata(stream, FileName(format));
        cancellationToken.ThrowIfCancellationRequested();
        if (directories.Count > MediaMetadataRetainedProcessor.MaximumMetadataDirectories)
            throw new InvalidDataException("Metadata directory limit exceeded.");
        var (width, height) = Dimensions(directories, format, cancellationToken);
        return new MediaMetadataParseResult(format, Container(format), width, height,
            DurationMilliseconds(directories, format), Audio(directories, format));
    }

    private static MediaMetadataAudioFacts? Audio(IReadOnlyList<MetadataDirectory> directories, MediaMetadataFormat format)
    {
        if (format == MediaMetadataFormat.Mp3)
        {
            var mp3 = directories.OfType<Mp3Directory>().FirstOrDefault();
            var sampleRateHz = PositiveInt(mp3, Mp3Directory.TagFrequency);
            var channels = Mp3Channels(mp3);
            return sampleRateHz is int rate && channels is int count
                ? new MediaMetadataAudioFacts(rate, count)
                : null;
        }

        if (format != MediaMetadataFormat.Wav)
        {
            return null;
        }

        var wav = directories.OfType<WavFormatDirectory>().FirstOrDefault();
        var wavSampleRateHz = PositiveInt(wav, WavFormatDirectory.TagSamplesPerSec);
        var wavChannels = PositiveInt(wav, WavFormatDirectory.TagChannels);
        return wavSampleRateHz is int wavRate && wavChannels is int wavChannelCount
            ? new MediaMetadataAudioFacts(wavRate, wavChannelCount)
            : null;
    }

    private static (int? Width, int? Height) Dimensions(
        IReadOnlyList<MetadataDirectory> directories,
        MediaMetadataFormat format,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return format switch
        {
            MediaMetadataFormat.Jpeg => Dimensions(directories.OfType<JpegDirectory>().FirstOrDefault(), JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
            MediaMetadataFormat.Png => Dimensions(directories.OfType<PngDirectory>().FirstOrDefault(), PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
            MediaMetadataFormat.Gif => Dimensions(directories.OfType<GifHeaderDirectory>().FirstOrDefault(), GifHeaderDirectory.TagImageWidth, GifHeaderDirectory.TagImageHeight),
            MediaMetadataFormat.Bmp => Dimensions(directories.OfType<BmpHeaderDirectory>().FirstOrDefault(), BmpHeaderDirectory.TagImageWidth, BmpHeaderDirectory.TagImageHeight),
            MediaMetadataFormat.Webp => Dimensions(directories.OfType<WebPDirectory>().FirstOrDefault(), WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight),
            MediaMetadataFormat.Tiff => Dimensions(directories.OfType<ExifIfd0Directory>().FirstOrDefault(), ExifDirectoryBase.TagImageWidth, ExifDirectoryBase.TagImageHeight),
            MediaMetadataFormat.Mov or MediaMetadataFormat.Mp4 or MediaMetadataFormat.M4v => QuickTimeDimensions(directories, cancellationToken),
            _ => (null, null)
        };
    }

    private static (int? Width, int? Height) QuickTimeDimensions(IReadOnlyList<MetadataDirectory> directories, CancellationToken cancellationToken)
    {
        foreach (var track in directories.OfType<QuickTimeTrackHeaderDirectory>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var width = PositiveFixedPointInt(track, QuickTimeTrackHeaderDirectory.TagWidth);
            var height = PositiveFixedPointInt(track, QuickTimeTrackHeaderDirectory.TagHeight);
            if (width is > 0 && height is > 0) return (width, height);
        }
        return (null, null);
    }

    private static (int? Width, int? Height) Dimensions(MetadataDirectory? directory, int widthTag, int heightTag) =>
        (PositiveInt(directory, widthTag), PositiveInt(directory, heightTag));

    private static int? PositiveFixedPointInt(MetadataDirectory? directory, int tag)
    {
        if (directory?.GetObject(tag) is not IConvertible value) return null;
        try
        {
            var number = value.ToDouble(CultureInfo.InvariantCulture);
            if (number <= 0 || number > int.MaxValue) return null;
            return number >= 65_536 ? checked((int)(number / 65_536d)) : checked((int)number);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static int? Mp3Channels(Mp3Directory? directory) =>
        (directory?.GetObject(Mp3Directory.TagMode) as string) switch
        {
            "Mono" => 1,
            "Stereo" or "Joint Stereo" or "Dual Channel" => 2,
            _ => null
        };

    private static long? DurationMilliseconds(IReadOnlyList<MetadataDirectory> directories, MediaMetadataFormat format)
    {
        if (format == MediaMetadataFormat.Wav)
        {
            var wav = directories.OfType<WavFormatDirectory>().FirstOrDefault();
            var fact = directories.OfType<WavFactDirectory>().FirstOrDefault();
            var sampleRateHz = PositiveLong(wav, WavFormatDirectory.TagSamplesPerSec);
            var samples = PositiveLong(fact, WavFactDirectory.TagSampleLength);
            return TryScaleToMilliseconds(samples, sampleRateHz);
        }

        if (format is MediaMetadataFormat.Mov or MediaMetadataFormat.Mp4 or MediaMetadataFormat.M4v)
        {
            var header = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            var timeScale = PositiveLong(header, QuickTimeMovieHeaderDirectory.TagTimeScale);
            if (header?.GetObject(QuickTimeMovieHeaderDirectory.TagDuration) is TimeSpan durationValue && durationValue >= TimeSpan.Zero)
            {
                return checked((long)durationValue.TotalMilliseconds);
            }
            var duration = PositiveLong(header, QuickTimeMovieHeaderDirectory.TagDuration);
            return TryScaleToMilliseconds(duration, timeScale);
        }

        return null;
    }

    private static int? PositiveInt(MetadataDirectory? directory, int tag) =>
        PositiveLong(directory, tag) is long value && value <= int.MaxValue ? (int)value : null;

    private static long? PositiveLong(MetadataDirectory? directory, int tag)
    {
        if (directory?.GetObject(tag) is not IConvertible value)
        {
            return null;
        }

        try
        {
            var number = value.ToInt64(CultureInfo.InvariantCulture);
            return number > 0 ? number : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static long? TryScaleToMilliseconds(long? duration, long? timeScale)
    {
        if (duration is not long units || timeScale is not long scale)
        {
            return null;
        }

        try
        {
            return checked(units * 1_000 / scale);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string Container(MediaMetadataFormat format) => format.ToString().ToLowerInvariant();

    private static string FileName(MediaMetadataFormat format) => format == MediaMetadataFormat.Jpeg
        ? "retained.jpg"
        : "retained." + format.ToString().ToLowerInvariant();
}
