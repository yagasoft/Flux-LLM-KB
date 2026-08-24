namespace FluxKnowledge.Application.Sources;

/// <summary>Identifies only the retained media containers supported by the deterministic metadata branch.</summary>
public static class MediaMetadataSignature
{
    private const int MaximumIsoBmffBrandInspectionBytes = 4 * 1024;
    private static readonly int[] MpegOneLayerThreeBitrateKbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] MpegTwoLayerThreeBitrateKbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] MpegOneSampleRates = [44_100, 48_000, 32_000];
    private static readonly int[] MpegTwoSampleRates = [22_050, 24_000, 16_000];
    private static readonly int[] MpegTwoPointFiveSampleRates = [11_025, 12_000, 8_000];
    public static bool TryGetFormatForExtension(string fileNameOrExtension, out MediaMetadataFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrExtension);
        var extension = fileNameOrExtension.StartsWith(".", StringComparison.Ordinal)
            ? fileNameOrExtension
            : Path.GetExtension(fileNameOrExtension);
        format = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaMetadataFormat.Jpeg,
            ".png" => MediaMetadataFormat.Png,
            ".gif" => MediaMetadataFormat.Gif,
            ".bmp" => MediaMetadataFormat.Bmp,
            ".tif" or ".tiff" => MediaMetadataFormat.Tiff,
            ".webp" => MediaMetadataFormat.Webp,
            ".mp3" => MediaMetadataFormat.Mp3,
            ".wav" => MediaMetadataFormat.Wav,
            ".mov" => MediaMetadataFormat.Mov,
            ".mp4" => MediaMetadataFormat.Mp4,
            ".m4v" => MediaMetadataFormat.M4v,
            _ => default
        };
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRecognisedUnsupportedMediaExtension(string fileNameOrExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrExtension);
        var extension = fileNameOrExtension.StartsWith(".", StringComparison.Ordinal)
            ? fileNameOrExtension
            : Path.GetExtension(fileNameOrExtension);
        return extension.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".heif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".wma", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRecognisedUnsupportedMediaSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return true;
        return IsAacAdtsFrame(bytes) || IsAsfHeader(bytes) || bytes.StartsWith("OggS"u8) || bytes.StartsWith("fLaC"u8) ||
            bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) || bytes.StartsWith("RIFF"u8);
    }

    private static bool IsAacAdtsFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 7 || bytes[0] != 0xff || (bytes[1] & 0xf6) != 0xf0 || ((bytes[2] >> 2) & 0x0f) == 0x0f)
        {
            return false;
        }

        var headerLength = (bytes[1] & 0x01) == 0 ? 9 : 7;
        if (bytes.Length < headerLength) return false;
        var channelConfiguration = ((bytes[2] & 0x01) << 2) | ((bytes[3] >> 6) & 0x03);
        var frameLength = ((bytes[3] & 0x03) << 11) | (bytes[4] << 3) | ((bytes[5] & 0xe0) >> 5);
        return channelConfiguration is > 0 and < 8 && frameLength >= headerLength && frameLength <= bytes.Length;
    }

    private static bool IsAsfHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || !bytes[..16].SequenceEqual(new byte[] { 0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c }) ||
            bytes[28] != 0x01 || bytes[29] != 0x02)
        {
            return false;
        }

        var declaredLength = (ulong)bytes[16] | ((ulong)bytes[17] << 8) | ((ulong)bytes[18] << 16) | ((ulong)bytes[19] << 24) |
                             ((ulong)bytes[20] << 32) | ((ulong)bytes[21] << 40) | ((ulong)bytes[22] << 48) | ((ulong)bytes[23] << 56);
        return declaredLength >= 30 && declaredLength <= (ulong)bytes.Length;
    }

    public static bool TryDetect(ReadOnlySpan<byte> bytes, out MediaMetadataFormat format) =>
        TryDetect(bytes, CancellationToken.None, out format);

    /// <summary>Detects a supported signature while bounding hostile ISO-BMFF brand inspection.</summary>
    public static bool TryDetect(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken, out MediaMetadataFormat format)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            format = MediaMetadataFormat.Jpeg;
            return true;
        }
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            format = MediaMetadataFormat.Png;
            return true;
        }
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            format = MediaMetadataFormat.Gif;
            return true;
        }
        if (bytes.StartsWith("BM"u8))
        {
            format = MediaMetadataFormat.Bmp;
            return true;
        }
        if (bytes.StartsWith(new byte[] { (byte)'I', (byte)'I', 0x2a, 0x00 }) ||
            bytes.StartsWith(new byte[] { (byte)'M', (byte)'M', 0x00, 0x2a }))
        {
            format = MediaMetadataFormat.Tiff;
            return true;
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            format = MediaMetadataFormat.Webp;
            return true;
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            format = MediaMetadataFormat.Wav;
            return true;
        }
        if (HasValidId3WithAudioFrame(bytes) || IsMpegAudioFrame(bytes))
        {
            format = MediaMetadataFormat.Mp3;
            return true;
        }
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            if (!TryReadIsoBmffFileType(bytes, cancellationToken, out var majorBrand, out var compatibleBrandOffset, out var compatibleBrandLimit) ||
                HasExcludedIsoBmffBrand(bytes, majorBrand, compatibleBrandOffset, compatibleBrandLimit, cancellationToken))
            {
                format = default;
                return false;
            }
            var brand = majorBrand;
            if (brand.SequenceEqual("qt  "u8))
            {
                format = MediaMetadataFormat.Mov;
                return true;
            }
            if (brand.SequenceEqual("M4V "u8) || brand.SequenceEqual("M4VH"u8) || brand.SequenceEqual("M4VP"u8))
            {
                format = MediaMetadataFormat.M4v;
                return true;
            }
            if (brand.SequenceEqual("isom"u8) || brand.SequenceEqual("iso2"u8) || brand.SequenceEqual("mp41"u8) ||
                brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("avc1"u8))
            {
                format = MediaMetadataFormat.Mp4;
                return true;
            }
            format = default;
            return false;
        }

        format = default;
        return false;
    }

    private static bool TryReadIsoBmffFileType(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out ReadOnlySpan<byte> majorBrand,
        out int compatibleBrandOffset,
        out int compatibleBrandLimit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        majorBrand = default;
        compatibleBrandOffset = 0;
        compatibleBrandLimit = 0;
        var declaredLength = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var extended = declaredLength == 1;
        var majorBrandOffset = extended ? 16 : 8;
        compatibleBrandOffset = extended ? 24 : 16;
        if (bytes.Length < compatibleBrandOffset)
        {
            return false;
        }

        ulong boxLength = declaredLength;
        if (extended)
        {
            boxLength = ((ulong)bytes[8] << 56) | ((ulong)bytes[9] << 48) | ((ulong)bytes[10] << 40) | ((ulong)bytes[11] << 32) |
                        ((ulong)bytes[12] << 24) | ((ulong)bytes[13] << 16) | ((ulong)bytes[14] << 8) | bytes[15];
        }
        if (declaredLength == 0) boxLength = (ulong)bytes.Length;
        if (boxLength < (ulong)compatibleBrandOffset)
        {
            return false;
        }

        var declaredLimit = boxLength > int.MaxValue ? bytes.Length : (int)boxLength;
        compatibleBrandLimit = Math.Min(bytes.Length, declaredLimit);
        var inspectionLimit = Math.Min(compatibleBrandLimit, compatibleBrandOffset + MaximumIsoBmffBrandInspectionBytes);
        if (compatibleBrandLimit > inspectionLimit)
        {
            return false;
        }

        majorBrand = bytes.Slice(majorBrandOffset, 4);
        compatibleBrandLimit = inspectionLimit;
        return true;
    }

    private static bool HasExcludedIsoBmffBrand(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> majorBrand,
        int compatibleBrandOffset,
        int compatibleBrandLimit,
        CancellationToken cancellationToken)
    {
        if (IsExcludedIsoBmffBrand(majorBrand)) return true;
        for (var offset = compatibleBrandOffset; offset <= compatibleBrandLimit - 4; offset += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var brand = bytes.Slice(offset, 4);
            if (IsExcludedIsoBmffBrand(brand))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsExcludedIsoBmffBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8) ||
        brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8) ||
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
        brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
        brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) ||
        brand.SequenceEqual("hevm"u8) || brand.SequenceEqual("hevs"u8);

    private static bool HasValidId3WithAudioFrame(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith("ID3"u8) || bytes.Length < 14 || bytes[3] is < 2 or > 4 || !HasValidId3Flags(bytes[3], bytes[5]) ||
            (bytes[6] & 0x80) != 0 || (bytes[7] & 0x80) != 0 || (bytes[8] & 0x80) != 0 || (bytes[9] & 0x80) != 0)
        {
            return false;
        }

        var tagLength = (bytes[6] << 21) | (bytes[7] << 14) | (bytes[8] << 7) | bytes[9];
        var hasFooter = bytes[3] == 4 && (bytes[5] & 0x10) != 0;
        var audioFrameOffset = 10L + tagLength + (hasFooter ? 10 : 0);
        return audioFrameOffset <= bytes.Length - 4 && IsMpegAudioFrame(bytes[(int)audioFrameOffset..]);
    }

    private static bool HasValidId3Flags(byte version, byte flags) => version switch
    {
        2 => flags == 0,
        3 => (flags & 0x1f) == 0,
        4 => (flags & 0x0f) == 0,
        _ => false
    };

    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || (bytes[1] & 0xe0) != 0xe0 ||
            (bytes[1] & 0x06) != 0x02 || (bytes[1] & 0x18) == 0x08)
        {
            return false;
        }

        var bitrateIndex = bytes[2] >> 4;
        var sampleRateIndex = (bytes[2] >> 2) & 0x03;
        if (bitrateIndex is 0 or 15 || sampleRateIndex == 3) return false;

        var version = bytes[1] & 0x18;
        var bitrateKbps = MpegLayerThreeBitrateKbps(version, bitrateIndex);
        var sampleRateHz = MpegSampleRateHz(version, sampleRateIndex);
        if (bitrateKbps == 0 || sampleRateHz == 0) return false;

        var coefficient = version == 0x18 ? 144_000 : 72_000;
        var frameLength = (coefficient * bitrateKbps / sampleRateHz) + ((bytes[2] & 0x02) == 0 ? 0 : 1);
        return frameLength >= 4 && frameLength <= bytes.Length;
    }

    private static int MpegLayerThreeBitrateKbps(int version, int index) => version == 0x18
        ? MpegOneLayerThreeBitrateKbps[index]
        : MpegTwoLayerThreeBitrateKbps[index];

    private static int MpegSampleRateHz(int version, int index) => version switch
    {
        0x18 => MpegOneSampleRates[index],
        0x10 => MpegTwoSampleRates[index],
        0x00 => MpegTwoPointFiveSampleRates[index],
        _ => 0
    };
}

public enum MediaMetadataFormat
{
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    Webp,
    Mp3,
    Wav,
    Mov,
    Mp4,
    M4v
}
