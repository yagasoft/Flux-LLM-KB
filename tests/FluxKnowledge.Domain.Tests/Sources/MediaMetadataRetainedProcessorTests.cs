using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using MetadataExtractor;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class MediaMetadataRetainedProcessorTests
{
    [Fact]
    public async Task Process_writes_the_canonical_manifest_for_a_signature_confirmed_png()
    {
        var bytes = Png();
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        var completion = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        var child = Assert.Single(completion.Members);
        Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"png\",\"container\":\"png\",\"dimensions\":{\"width\":1,\"height\":1},\"duration_ms\":null,\"audio\":null}", writer.Text);
        Assert.Equal("AcceptedUtf8Text", child.Classification);
        Assert.Equal(".json", child.Extension);
        Assert.Equal(1, writer.WriteCount);
    }

    [Fact]
    public async Task Process_uses_typed_jpeg_dimensions_when_the_parser_description_contains_units()
    {
        var bytes = Jpeg(width: 43, height: 42);
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"jpeg\",\"container\":\"jpeg\",\"dimensions\":{\"width\":43,\"height\":42},\"duration_ms\":null,\"audio\":null}", writer.Text);
    }

    [Theory]
    [MemberData(nameof(StructuralImageFixtures))]
    public async Task Process_parses_each_remaining_signature_confirmed_supported_image_with_the_real_metadata_extractor(
        byte[] bytes,
        string expectedFormat,
        int width,
        int height)
    {
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        var dimensions = width > 0 && height > 0
            ? $"{{\"width\":{width},\"height\":{height}}}"
            : "null";
        Assert.Equal($"{{\"schema\":\"media-metadata-v1\",\"format\":\"{expectedFormat}\",\"container\":\"{expectedFormat}\",\"dimensions\":{dimensions},\"duration_ms\":null,\"audio\":null}}", writer.Text);
    }

    [Fact]
    public void Preflight_uses_the_pinned_full_metadata_extractor_assembly_identity()
    {
        var identity = typeof(ImageMetadataReader).Assembly.GetName();
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure());

        Assert.Equal("MetadataExtractor", identity.Name);
        Assert.Equal(new Version(2, 9, 3, 0), identity.Version);
        Assert.True(string.IsNullOrEmpty(identity.CultureName));
        Assert.Equal("b66b5ccaf776c301", Convert.ToHexStringLower(identity.GetPublicKeyToken()!));
        Assert.True(processor.Preflight().IsAvailable);
    }

    [Theory]
    [InlineData("qt  ", "mov")]
    [InlineData("isom", "mp4")]
    [InlineData("M4V ", "m4v")]
    public async Task Process_uses_typed_quicktime_track_dimensions(string majorBrand, string expectedFormat)
    {
        var bytes = MovieWithTrackDimensions(majorBrand, width: 43, height: 42);
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        Assert.Equal($"{{\"schema\":\"media-metadata-v1\",\"format\":\"{expectedFormat}\",\"container\":\"{expectedFormat}\",\"dimensions\":{{\"width\":43,\"height\":42}},\"duration_ms\":2000,\"audio\":null}}", writer.Text);
    }

    [Fact]
    public async Task Process_rejects_a_checksum_mismatch_before_parser_or_writer_access()
    {
        var bytes = Png();
        var parser = new RecordingParser(MediaMetadataParseResult.Png(1, 1));
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure(), parser);

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(
            Claim(bytes, inputSha256: new string('0', 64)), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("retained-artifact-checksum-invalid", error.OutcomeCode);
        Assert.Equal(0, parser.ParseCount);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_rejects_a_declared_input_larger_than_the_retained_media_budget()
    {
        var bytes = Png();
        var parser = new RecordingParser(MediaMetadataParseResult.Png(1, 1));
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), parser);
        var retained = new RetainedSourceBytes(Claim(bytes).SourceRevisionId, bytes, Hash(bytes), MediaMetadataRetainedProcessor.MaximumInputBytes + 1);

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), retained, CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-input-too-large", error.OutcomeCode);
        Assert.Equal(0, parser.ParseCount);
    }

    [Fact]
    public void ValidateSignature_rejects_a_recognised_media_extension_with_a_different_container()
    {
        var matches = MediaMetadataRetainedProcessor.HasMatchingSupportedSignature("photo.png", new byte[] { 0xff, 0xd8, 0xff, 0xe0 }, out var outcomeCode);

        Assert.False(matches);
        Assert.Equal("media-metadata-signature-mismatch", outcomeCode);
    }

    [Fact]
    public void ValidateSignature_reports_a_signature_confirmed_recognised_but_unsupported_media_format()
    {
        var matches = MediaMetadataRetainedProcessor.HasMatchingSupportedSignature("photo.avif", IsoBmff("avif"), out var outcomeCode);

        Assert.False(matches);
        Assert.Equal("media-metadata-format-unsupported", outcomeCode);
    }

    [Theory]
    [MemberData(nameof(RecognisedUnsupportedAudioFixtures))]
    public void ValidateSignature_reports_signature_confirmed_aac_and_wma_as_unsupported(string extension, byte[] bytes)
    {
        Assert.True(MediaMetadataSignature.IsRecognisedUnsupportedMediaSignature(bytes));
        Assert.False(MediaMetadataRetainedProcessor.HasMatchingSupportedSignature($"audio.{extension}", bytes, out var outcomeCode));
        Assert.Equal("media-metadata-format-unsupported", outcomeCode);
    }

    [Fact]
    public void ValidateSignature_preserves_signature_mismatch_for_an_unsupported_media_extension_with_non_media_bytes()
    {
        var matches = MediaMetadataRetainedProcessor.HasMatchingSupportedSignature("photo.avif", Png(), out var outcomeCode);

        Assert.False(matches);
        Assert.Equal("media-metadata-signature-mismatch", outcomeCode);
    }

    [Fact]
    public async Task Process_blocks_when_parser_preflight_is_unavailable()
    {
        var bytes = Png();
        var parser = new RecordingParser(MediaMetadataParseResult.Png(1, 1), new MediaMetadataParserPreflight(false, "media-metadata-parser-unavailable"));
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), parser);

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-unavailable", error.OutcomeCode);
        Assert.Equal(0, parser.ParseCount);
    }

    [Fact]
    public async Task Process_maps_an_unexpected_non_cancellation_preflight_exception_to_parser_unavailable()
    {
        var bytes = Png();
        var parser = new UnexpectedPreflightParser();
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), parser);

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-unavailable", error.OutcomeCode);
        Assert.Equal(0, parser.ParseCount);
    }

    [Fact]
    public async Task Process_maps_an_unsignalled_preflight_cancellation_to_parser_unavailable()
    {
        var bytes = Png();
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), new UnsignalledCancellationPreflightParser());

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-unavailable", error.OutcomeCode);
    }

    [Fact]
    public void Preflight_preserves_a_caller_signalled_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), new UnsignalledCancellationPreflightParser());

        Assert.Throws<OperationCanceledException>(() => processor.Preflight(cancellation.Token));
    }

    [Fact]
    public async Task Process_stops_a_parser_that_exceeds_the_cumulative_read_budget()
    {
        var bytes = Png();
        var parser = new RecordingParser(MediaMetadataParseResult.Png(1, 1), readUntilBudgetExceeded: true);
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), parser);

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-read-limit", error.OutcomeCode);
    }

    [Fact]
    public async Task Process_maps_a_parser_failure_without_writing_a_manifest()
    {
        var bytes = Png();
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure(), new ThrowingParser());

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-failed", error.OutcomeCode);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_maps_an_unexpected_non_cancellation_parser_exception_without_writing_a_manifest()
    {
        var bytes = Png();
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure(), new UnexpectedParser());

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-failed", error.OutcomeCode);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_maps_an_unsignalled_parser_cancellation_without_writing_a_manifest()
    {
        var bytes = Png();
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure(), new UnsignalledCancellationParser());

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-failed", error.OutcomeCode);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_preserves_a_caller_signalled_parser_cancellation()
    {
        var bytes = Png();
        using var cancellation = new CancellationTokenSource();
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), new SignalledCancellationParser(cancellation));

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Process_enforces_the_production_metadata_directory_limit_before_enumerating_a_real_metadata_fixture()
    {
        var bytes = GifWithImageDirectories(MediaMetadataRetainedProcessor.MaximumMetadataDirectories + 1);
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-failed", error.OutcomeCode);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_maps_a_real_metadata_extractor_failure_for_malformed_signature_confirmed_png()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x00 };
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-parser-failed", error.OutcomeCode);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_writes_only_duration_and_audio_structure_for_a_synthetic_wav()
    {
        var bytes = Wav(sampleRateHz: 8_000, channels: 2, durationMilliseconds: 1_000);
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"wav\",\"container\":\"wav\",\"dimensions\":null,\"duration_ms\":1000,\"audio\":{\"sample_rate_hz\":8000,\"channels\":2}}", writer.Text);
    }

    [Fact]
    public async Task Process_writes_only_duration_structure_for_a_synthetic_mp4()
    {
        var bytes = Mp4(durationMilliseconds: 2_000);
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"mp4\",\"container\":\"mp4\",\"dimensions\":null,\"duration_ms\":2000,\"audio\":null}", writer.Text);
    }

    [Fact]
    public async Task Process_writes_only_audio_structure_from_a_synthetic_mp3_frame()
    {
        var bytes = Mp3();
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure());

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"mp3\",\"container\":\"mp3\",\"dimensions\":null,\"duration_ms\":null,\"audio\":{\"sample_rate_hz\":44100,\"channels\":2}}", writer.Text);
    }

    [Theory]
    [MemberData(nameof(InvalidMp3Signatures))]
    public void TryDetect_rejects_non_mp3_audio_and_truncated_id3_headers(byte[] bytes)
    {
        var detected = MediaMetadataSignature.TryDetect(bytes, out _);

        Assert.False(detected);
        Assert.False(MediaMetadataRetainedProcessor.HasMatchingSupportedSignature("audio.mp3", bytes, out var outcomeCode));
        Assert.Equal("media-metadata-signature-mismatch", outcomeCode);
    }

    [Theory]
    [MemberData(nameof(IncompleteOrNonLayerThreeMp3Signatures))]
    public void TryDetect_rejects_incomplete_and_non_layer_three_mpeg_audio_headers(byte[] bytes)
    {
        Assert.False(MediaMetadataSignature.TryDetect(bytes, out _));
        Assert.False(MediaMetadataRetainedProcessor.HasMatchingSupportedSignature("audio.mp3", bytes, out var outcomeCode));
        Assert.Equal("media-metadata-signature-mismatch", outcomeCode);
    }

    [Fact]
    public void TryDetect_accepts_a_complete_id3_prefixed_layer_three_mp3_frame()
    {
        Assert.True(MediaMetadataSignature.TryDetect(Id3WithCompleteMp3Frame(), out var format));
        Assert.Equal(MediaMetadataFormat.Mp3, format);
    }

    [Fact]
    public async Task Process_rejects_a_canonical_manifest_larger_than_its_output_budget()
    {
        var bytes = Png();
        var parser = new RecordingParser(new MediaMetadataParseResult(
            MediaMetadataFormat.Png, new string('x', 2_000), 1, 1, null, null));
        var processor = new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), parser);

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None).AsTask());

        Assert.Equal("media-metadata-output-limit", error.OutcomeCode);
    }

    [Fact]
    public async Task Process_honours_cancellation_before_parser_or_writer_access()
    {
        var bytes = Png();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var parser = new RecordingParser(MediaMetadataParseResult.Png(1, 1));
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure(), parser);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(Claim(bytes), Retained(bytes), cancellation.Token).AsTask());

        Assert.Equal(0, parser.ParseCount);
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task Process_excludes_exif_gps_and_title_secret_sentinels_from_the_scanned_manifest()
    {
        var bytes = Png();
        var disclosure = new RecordingDisclosure();
        var parser = new RecordingParser(MediaMetadataParseResult.Png(
            1,
            1,
            [
                new MediaMetadataIgnoredField("EXIF", "secret-content-sentinel"),
                new MediaMetadataIgnoredField("GPS", "secret-content-sentinel"),
                new MediaMetadataIgnoredField("Title", "secret-content-sentinel")
            ]));
        var writer = new RecordingWriter();
        var processor = new MediaMetadataRetainedProcessor(writer, disclosure, parser);

        _ = await processor.ProcessAsync(Claim(bytes), Retained(bytes), CancellationToken.None);

        Assert.DoesNotContain("secret-content-sentinel", writer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-content-sentinel", Assert.Single(disclosure.Values), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("heic")]
    public void TryDetect_rejects_unsupported_iso_bmff_brands(string brand)
    {
        var bytes = IsoBmff(brand);

        var detected = MediaMetadataSignature.TryDetect(bytes, out _);

        Assert.False(detected);
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("heic")]
    public void TryDetect_rejects_avif_and_heif_compatible_brands_under_an_isom_major_brand(string compatibleBrand)
    {
        var bytes = IsoBmff("isom", compatibleBrand);

        var detected = MediaMetadataSignature.TryDetect(bytes, out _);

        Assert.False(detected);
    }

    [Fact]
    public void TryDetect_honours_cancellation_while_bounding_a_zero_sized_iso_bmff_brand_scan()
    {
        var bytes = new byte[128 * 1024];
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("isom").CopyTo(bytes, 8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MediaMetadataSignature.TryDetect(bytes, cancellation.Token, out _));
    }

    [Fact]
    public async Task Activation_leaves_media_processing_inert_when_the_explicit_flag_is_disabled()
    {
        var bytes = Png();
        var candidate = new RetainedProcessorPromotionCandidate(
            Guid.NewGuid(),
            new SourceRevisionId(new Guid("77777777-3333-4444-5555-666666666666")),
            Hash(bytes),
            ".png");
        var branches = new MediaActivationBranches(candidate);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new MediaCapabilityStore(), new LocalSourceCapabilityHandlerRegistry([new MediaMetadataCapabilityHandler()])),
            branches,
            new MediaReader(candidate.SourceRevisionId, bytes),
            new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions { MediaMetadataEnabled = false },
            TimeProvider.System,
            mediaMetadataProcessor: new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure()));

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal(0, branches.ReadCount);
        Assert.Equal(0, branches.PromoteCount);
    }

    [Fact]
    public async Task Activation_keeps_signature_confirmed_media_deferred_when_preflight_is_unavailable()
    {
        var bytes = Png();
        var candidate = new RetainedProcessorPromotionCandidate(
            Guid.NewGuid(),
            new SourceRevisionId(new Guid("88888888-3333-4444-5555-666666666666")),
            Hash(bytes),
            ".png");
        var branches = new MediaActivationBranches(candidate);
        var unavailableParser = new RecordingParser(MediaMetadataParseResult.Png(1, 1),
            new MediaMetadataParserPreflight(false, "media-metadata-parser-unavailable"));
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new MediaCapabilityStore(), new LocalSourceCapabilityHandlerRegistry([new MediaMetadataCapabilityHandler()])),
            branches,
            new MediaReader(candidate.SourceRevisionId, bytes),
            new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions { MediaMetadataEnabled = true },
            TimeProvider.System,
            mediaMetadataProcessor: new MediaMetadataRetainedProcessor(new RecordingWriter(), new AllowingDisclosure(), unavailableParser));

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal(1, branches.ReadCount);
        Assert.Equal("media-metadata-parser-unavailable", branches.DeferredOutcomeCode);
        Assert.Equal(0, branches.PromoteCount);
    }

    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Jj7kAAAAASUVORK5CYII=");

    private static byte[] Jpeg(short width, short height) =>
    [
        0xff, 0xd8, 0xff, 0xc0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
        0xff, 0xd9
    ];

    public static IEnumerable<object[]> StructuralImageFixtures()
    {
        yield return [Gif(width: 2, height: 3), "gif", 2, 3];
        yield return [Bmp(width: 2, height: 3), "bmp", 2, 3];
        yield return [Tiff(width: 2, height: 3), "tiff", 2, 3];
        yield return [Webp(width: 2, height: 3), "webp", -1, -1];
    }

    private static byte[] Gif(short width, short height) =>
    [
        ..Encoding.ASCII.GetBytes("GIF89a"),
        (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8),
        0x00, 0x00, 0x00, 0x3b
    ];

    private static byte[] Bmp(int width, int height)
    {
        var bytes = new byte[54];
        Encoding.ASCII.GetBytes("BM").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        return bytes;
    }

    private static byte[] Tiff(int width, int height)
    {
        var bytes = new byte[38];
        Encoding.ASCII.GetBytes("II").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(10, 2), 256);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12, 2), 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), 257);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(24, 2), 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(26, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(30, 4), height);
        return bytes;
    }

    private static byte[] Webp(int width, int height)
    {
        var bytes = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        Encoding.ASCII.GetBytes("WEBPVP8 ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), 10);
        bytes[16] = 0x10;
        bytes[19] = 0x9d;
        bytes[20] = 0x01;
        bytes[21] = 0x2a;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), (short)width);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(24, 2), (short)height);
        return bytes;
    }

    private static byte[] Wav(int sampleRateHz, short channels, int durationMilliseconds)
    {
        var bytesPerSample = 2;
        var byteRate = sampleRateHz * channels * bytesPerSample;
        var dataLength = checked(byteRate * durationMilliseconds / 1_000);
        var bytes = new byte[56 + dataLength];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 48 + dataLength);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22, 2), channels);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), sampleRateHz);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), byteRate);
        BitConverter.TryWriteBytes(bytes.AsSpan(32, 2), (short)(channels * bytesPerSample));
        BitConverter.TryWriteBytes(bytes.AsSpan(34, 2), (short)(bytesPerSample * 8));
        Encoding.ASCII.GetBytes("fact").CopyTo(bytes, 36);
        BitConverter.TryWriteBytes(bytes.AsSpan(40, 4), 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(44, 4), sampleRateHz * durationMilliseconds / 1_000);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 48);
        BitConverter.TryWriteBytes(bytes.AsSpan(52, 4), dataLength);
        return bytes;
    }

    private static byte[] Mp4(int durationMilliseconds)
    {
        var ftyp = IsoBmff("isom");
        var mvhd = new byte[108];
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(0, 4), mvhd.Length);
        Encoding.ASCII.GetBytes("mvhd").CopyTo(mvhd, 4);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(20, 4), 1_000);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(24, 4), durationMilliseconds);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(28, 4), 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(mvhd.AsSpan(32, 2), 0x0100);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(52, 4), 0x00010000);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(68, 4), 0x00010000);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(84, 4), 0x40000000);
        BinaryPrimitives.WriteInt32BigEndian(mvhd.AsSpan(104, 4), 2);
        var moov = new byte[8 + mvhd.Length];
        BinaryPrimitives.WriteInt32BigEndian(moov.AsSpan(0, 4), moov.Length);
        Encoding.ASCII.GetBytes("moov").CopyTo(moov, 4);
        mvhd.CopyTo(moov, 8);
        return ftyp.Concat(moov).ToArray();
    }

    private static byte[] MovieWithTrackDimensions(string majorBrand, int width, int height)
    {
        var movie = Mp4(2_000).Skip(IsoBmff("isom").Length).ToArray();
        var ftyp = IsoBmff(majorBrand);
        var tkhd = new byte[92];
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(0, 4), tkhd.Length);
        Encoding.ASCII.GetBytes("tkhd").CopyTo(tkhd, 4);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(28, 4), 2_000);
        BinaryPrimitives.WriteInt16BigEndian(tkhd.AsSpan(44, 2), 0x0100);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(48, 4), 0x00010000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(64, 4), 0x00010000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(80, 4), 0x40000000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(84, 4), width << 16);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(88, 4), height << 16);
        var trak = new byte[8 + tkhd.Length];
        BinaryPrimitives.WriteInt32BigEndian(trak.AsSpan(0, 4), trak.Length);
        Encoding.ASCII.GetBytes("trak").CopyTo(trak, 4);
        tkhd.CopyTo(trak, 8);
        var moov = movie.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(moov.AsSpan(0, 4), moov.Length + trak.Length);
        return ftyp.Concat(moov).Concat(trak).ToArray();
    }

    private static byte[] Mp3()
    {
        var bytes = new byte[417];
        bytes[0] = 0xff;
        bytes[1] = 0xfb;
        bytes[2] = 0x90;
        bytes[3] = 0x00;
        return bytes;
    }

    public static IEnumerable<object[]> InvalidMp3Signatures()
    {
        yield return [new byte[] { 0xff, 0xf1, 0x50, 0x80 }];
        yield return ["ID3\x04\0\0"u8.ToArray()];
        yield return ["ID3\x04\0\0\0\0\0\0"u8.ToArray()];
        yield return [Id3WithInvalidFlagsAndAudioFrame()];
    }

    public static IEnumerable<object[]> IncompleteOrNonLayerThreeMp3Signatures()
    {
        yield return [new byte[] { 0xff, 0xfb, 0x90, 0x00 }];
        yield return ["ID3\x03\0\0\0\0\0\0"u8.ToArray().Concat(new byte[] { 0xff, 0xfb, 0x90, 0x00 }).ToArray()];
        yield return [MpegHeaderWithBody(0xff)];
        yield return [MpegHeaderWithBody(0xfd)];
    }

    public static IEnumerable<object[]> RecognisedUnsupportedAudioFixtures()
    {
        yield return ["aac", AacAdts()];
        yield return ["wma", AsfHeader()];
    }

    private static byte[] AacAdts() => [0xff, 0xf1, 0x50, 0x80, 0x00, 0xe0, 0xfc];

    private static byte[] AsfHeader() =>
    [
        0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c,
        0x1e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x02
    ];

    private static byte[] Id3WithInvalidFlagsAndAudioFrame() =>
        "ID3\x03\0\x01\0\0\0\0"u8.ToArray().Concat(Mp3()).ToArray();

    private static byte[] Id3WithCompleteMp3Frame() =>
        "ID3\x03\0\0\0\0\0\0"u8.ToArray().Concat(Mp3()).ToArray();

    private static byte[] MpegHeaderWithBody(byte secondHeaderByte)
    {
        var bytes = new byte[417];
        bytes[0] = 0xff;
        bytes[1] = secondHeaderByte;
        bytes[2] = 0x90;
        return bytes;
    }

    private static byte[] GifWithImageDirectories(int count)
    {
        var bytes = new List<byte>(13 + (count * 15) + 1);
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([1, 0, 1, 0, 0, 0, 0]);
        for (var index = 0; index < count; index++)
        {
            bytes.AddRange([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0]);
        }
        bytes.Add(0x3b);
        return bytes.ToArray();
    }

    private static byte[] IsoBmff(string majorBrand, params string[] compatibleBrands)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(4, majorBrand.Length);
        foreach (var compatibleBrand in compatibleBrands)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(4, compatibleBrand.Length);
        }
        var bytes = new byte[16 + (compatibleBrands.Length * 4)];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes(majorBrand).CopyTo(bytes, 8);
        for (var index = 0; index < compatibleBrands.Length; index++)
        {
            Encoding.ASCII.GetBytes(compatibleBrands[index]).CopyTo(bytes, 16 + (index * 4));
        }
        return bytes;
    }

    private static RetainedProcessorClaim Claim(byte[] bytes, string? inputSha256 = null) => new(
        new Guid("11111111-2222-3333-4444-555555555555"),
        new SourceRevisionId(new Guid("22222222-3333-4444-5555-666666666666")),
        "media-test-parent",
        inputSha256 ?? Hash(bytes),
        "media-test-worker",
        1,
        DateTimeOffset.UtcNow.AddMinutes(1));

    private static RetainedSourceBytes Retained(byte[] bytes) => new(
        new SourceRevisionId(new Guid("22222222-3333-4444-5555-666666666666")), bytes, Hash(bytes), bytes.Length);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class RecordingParser(
        MediaMetadataParseResult result,
        MediaMetadataParserPreflight? preflight = null,
        bool readUntilBudgetExceeded = false) : IMediaMetadataParser
    {
        public int ParseCount { get; private set; }
        public MediaMetadataParserPreflight Preflight() => preflight ?? new MediaMetadataParserPreflight(true, null);

        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
        {
            ParseCount++;
            if (readUntilBudgetExceeded)
            {
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    stream.Position = 0;
                    while (stream.Read(buffer) > 0) { }
                }
            }
            return result;
        }
    }

    private sealed class ThrowingParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new InvalidDataException("synthetic parser failure");
    }

    private sealed class UnexpectedPreflightParser : IMediaMetadataParser
    {
        public int ParseCount { get; private set; }
        public MediaMetadataParserPreflight Preflight() => throw new InvalidOperationException("synthetic unexpected preflight failure");
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
        {
            ParseCount++;
            throw new Xunit.Sdk.XunitException("Unexpected preflight failure must not parse.");
        }
    }

    private sealed class UnexpectedParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic unexpected parser failure");
    }

    private sealed class UnsignalledCancellationPreflightParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => throw new OperationCanceledException("synthetic unsignalled preflight cancellation");
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Unavailable parser must not parse.");
    }

    private sealed class UnsignalledCancellationParser : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("synthetic unsignalled parser cancellation");
    }

    private sealed class SignalledCancellationParser(CancellationTokenSource cancellation) : IMediaMetadataParser
    {
        public MediaMetadataParserPreflight Preflight() => new(true, null);
        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class RecordingWriter : IRetainedArtifactWriter
    {
        public string Text { get; private set; } = string.Empty;
        public int WriteCount { get; private set; }

        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId parentSourceRevisionId, Stream content, long maximumByteLength, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            Text = Encoding.UTF8.GetString(bytes);
            WriteCount++;
            return new RetainedArtifactWriteReceipt(Hash(bytes), "sha256\\media\\manifest.json", bytes.Length, true, false);
        }
    }

    private sealed class AllowingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }

    private sealed class RecordingDisclosure : ILocalPrivateContentDisclosure
    {
        public List<string> Values { get; } = [];
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind)
        {
            Values.Add(value);
            return new LocalDisclosureResult(value, false, null);
        }
    }

    private sealed class MediaCapabilityStore : ISourceCapabilityStore
    {
        public ValueTask<RegisteredSourceCapability> RegisterAsync(RegisteredSourceCapability capability, CancellationToken cancellationToken) =>
            ValueTask.FromResult(capability);

        public ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RegisteredSourceCapability?>(null);
    }

    private sealed class MediaReader(SourceRevisionId sourceRevisionId, byte[] bytes) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(sourceRevisionId, requestedRevisionId);
            return ValueTask.FromResult(new RetainedSourceBytes(sourceRevisionId, bytes, Hash(bytes), bytes.Length));
        }

        public ValueTask<FluxKnowledge.Application.Pipeline.Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Media activation must not decode retained bytes as text.");
    }

    private sealed class MediaActivationBranches(RetainedProcessorPromotionCandidate candidate) : IRetainedProcessorBranchStore
    {
        public int ReadCount { get; private set; }
        public int PromoteCount { get; private set; }
        public string? DeferredOutcomeCode { get; private set; }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(
            int maximumCount,
            SourceCapabilityDescriptor capability,
            CancellationToken cancellationToken)
        {
            Assert.Equal(MediaMetadataRetainedProcessor.Capability, capability);
            ReadCount++;
            return ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([candidate]);
        }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);

        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken cancellationToken)
        {
            Assert.Equal(candidate, value);
            Assert.Equal(MediaMetadataRetainedProcessor.Capability, capability);
            PromoteCount++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> DeferPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken)
        {
            Assert.Equal(candidate, value);
            DeferredOutcomeCode = outcomeCode;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]);
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
