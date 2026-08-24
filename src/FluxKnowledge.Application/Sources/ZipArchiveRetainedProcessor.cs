using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

public sealed class RetainedProcessorOptions
{
    public const string ConfigurationSectionName = "RetainedProcessors";
    public const int MaximumAutomaticReplayBatchSize = 16;
    public bool ArchiveZipExpandEnabled { get; init; }
    public bool ArchiveTarExpandEnabled { get; init; }
    public bool OoxmlDocumentStructuralExtractEnabled { get; init; }
    public bool CsharpCodeEnabled { get; init; } = true;
    public bool MediaMetadataEnabled { get; init; }
    public int AutomaticReplayBatchSize { get; init; } = MaximumAutomaticReplayBatchSize;
    public long MaximumCompressedInputBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumEntryCount { get; init; } = 256;
    public long MaximumExpandedBytes { get; init; } = 128L * 1024 * 1024;
    public long MaximumMemberBytes { get; init; } = 16L * 1024 * 1024;
    public int MaximumLogicalPathLength { get; init; } = 512;
    public int MaximumCompressionRatio { get; init; } = 100;
}

/// <summary>Processes only checksum-verified retained ZIP bytes into content-addressed child artifacts.</summary>
public sealed class ZipArchiveRetainedProcessor(IRetainedArtifactWriter artifactWriter) : ILocalSourceCapabilityHandler
{
    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("b4a06e5d-6f01-4f73-9722-79b6df4e85c3"),
        "archive-zip-expand",
        "phase-5-zip-v1",
        ExecutionClass.InProcess,
        "phase-5-zip-retained-archive-v1",
        SourceActivityKind.ArchiveExpansion,
        "ArchiveZip",
        "retained:archive-zip-expand");

    public SourceCapabilityDescriptor Descriptor => Capability;

    public async ValueTask<RetainedProcessorCompletion> ProcessAsync(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        RetainedProcessorOptions options,
        CancellationToken cancellationToken)
    {
        if (retained.ByteLength > options.MaximumCompressedInputBytes || retained.Bytes.Length > options.MaximumCompressedInputBytes)
        {
            throw new RetainedProcessorException("archive-input-too-large");
        }
        if (!string.Equals(retained.ContentSha256, claim.InputSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(retained.Bytes)), claim.InputSha256, StringComparison.Ordinal))
        {
            throw new RetainedProcessorException("retained-artifact-checksum-invalid");
        }
        if (!IsZipSignature(retained.Bytes))
        {
            throw new RetainedProcessorException("archive-signature-invalid");
        }

        try
        {
            var centralEntries = ValidateSafePackageMetadata(retained.Bytes, options.MaximumEntryCount);
            using var input = new MemoryStream(retained.Bytes, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > options.MaximumEntryCount)
            {
                throw new RetainedProcessorException("archive-entry-count-limit");
            }

            long expandedTotal = 0;
            var preparedMembers = new List<(ZipArchiveEntry Entry, ArchiveMemberIdentity Identity)>();
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            var blockedMembers = new List<RetainedProcessorMemberOutcome>();
            if (centralEntries.Count != archive.Entries.Count)
            {
                throw new RetainedProcessorException("archive-entry-unsupported");
            }
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.Entries[index];
                var centralEntry = centralEntries[index];
                try
                {
                    var path = ValidateEntryPath(IsDirectory(entry) ? entry.FullName.TrimEnd('/', '\\') : entry.FullName, options.MaximumLogicalPathLength);
                    if (IsDirectory(entry)) continue;
                    ValidateEntry(entry, options, centralEntry);
                    expandedTotal = checked(expandedTotal + entry.Length);
                    if (expandedTotal > options.MaximumExpandedBytes) throw new RetainedProcessorException("archive-expanded-total-limit");
                    var identity = ArchiveMemberIdentity.Create(claim.ParentStableIdentity, path);
                    if (!fingerprints.Add(identity.MemberFingerprint)) throw new RetainedProcessorException("archive-member-identity-conflict");
                    await using var preflightStream = entry.Open();
                    if (IsZipSignature(await ReadPrefixAsync(preflightStream, cancellationToken).ConfigureAwait(false)))
                    {
                        throw new RetainedProcessorException("nested-archive-depth-limit");
                    }
                    preparedMembers.Add((entry, identity));
                }
                catch (RetainedProcessorException exception)
                {
                    blockedMembers.Add(new RetainedProcessorMemberOutcome(
                        ComputeUnsafeMemberFingerprint(claim.ParentStableIdentity, index, entry.FullName),
                        Math.Max(0, entry.Length), "blocked", exception.OutcomeCode));
                }
            }
            if (blockedMembers.Count > 0)
            {
                throw new RetainedProcessorException(blockedMembers[0].ReasonCode, blockedMembers);
            }

            var members = new List<RetainedProcessorDerivedChild>();
            foreach (var (entry, identity) in preparedMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var memberStream = entry.Open();
                var receipt = await artifactWriter.WriteAsync(
                    claim.SourceRevisionId,
                    memberStream,
                    options.MaximumMemberBytes,
                    cancellationToken).ConfigureAwait(false);
                if (receipt.ByteLength != entry.Length)
                {
                    throw new RetainedProcessorException("archive-member-size-invalid");
                }
                if (receipt.IsNestedArchive)
                {
                    throw new RetainedProcessorException("nested-archive-depth-limit");
                }
                if (!receipt.IsUtf8Text)
                {
                    throw new RetainedProcessorException("archive-member-not-utf8");
                }
                members.Add(RetainedProcessorDerivedChild.ArchiveMember(identity, receipt.ContentSha256, receipt.StoreRelativePath,
                    receipt.ByteLength, "AcceptedUtf8Text"));
            }
            var receiptFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("|", members.OrderBy(member => member.MemberFingerprint, StringComparer.Ordinal)
                    .Select(member => $"{member.MemberFingerprint}:{member.ContentSha256}:{member.ByteLength}")))));
            return new RetainedProcessorCompletion(members, receiptFingerprint);
        }
        catch (InvalidDataException exception)
        {
            throw new RetainedProcessorException("archive-entry-unsupported", innerException: exception);
        }
    }

    public static bool IsZipSignature(ReadOnlySpan<byte> value) => value.Length >= 4 &&
        ((value[0] == (byte)'P' && value[1] == (byte)'K' && value[2] == 3 && value[3] == 4) ||
         (value[0] == (byte)'P' && value[1] == (byte)'K' && value[2] == 5 && value[3] == 6));

    /// <summary>Shared central-directory fence for retained container processors.</summary>
    public static void ValidateSafeCentralDirectory(ReadOnlySpan<byte> bytes, int maximumEntries)
    {
        _ = ValidateSafePackageMetadata(bytes, maximumEntries);
    }

    private static IReadOnlyList<CentralDirectoryEntry> ValidateSafePackageMetadata(ReadOnlySpan<byte> bytes, int maximumEntries)
    {
        var entries = ReadCentralDirectory(bytes);
        if (entries.Count > maximumEntries) throw new RetainedProcessorException("archive-entry-count-limit");
        foreach (var entry in entries)
        {
            ValidateCentralEntry(entry);
            ValidateLocalHeader(bytes, entry);
        }
        return entries;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
        entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static string ValidateEntryPath(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.IndexOf('\0') >= 0 ||
            value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("\\", StringComparison.Ordinal) || value.Contains(':') || value.Contains('\\'))
        {
            throw new RetainedProcessorException("archive-entry-path-invalid");
        }
        var segments = value.Split("/", StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new RetainedProcessorException("archive-entry-path-invalid");
        }
        return string.Join("/", segments);
    }

    private static void ValidateEntry(ZipArchiveEntry entry, RetainedProcessorOptions options, CentralDirectoryEntry centralEntry)
    {
        if (entry.Length > options.MaximumMemberBytes)
        {
            throw new RetainedProcessorException("archive-member-size-limit");
        }
        if (entry.CompressedLength == 0 && entry.Length > 0 ||
            entry.CompressedLength > 0 && entry.Length > checked(entry.CompressedLength * options.MaximumCompressionRatio))
        {
            throw new RetainedProcessorException("archive-compression-ratio-limit");
        }
        if (centralEntry.IsSymbolicLink || centralEntry.IsWindowsReparsePoint)
        {
            throw new RetainedProcessorException("archive-entry-link-invalid");
        }
    }

    private static void ValidateCentralEntry(CentralDirectoryEntry entry)
    {
        if (entry.CompressionMethod is not 0 and not 8)
        {
            throw new RetainedProcessorException("archive-entry-compression-unsupported");
        }
        ValidateGeneralPurposeBitFlag(entry.GeneralPurposeBitFlag, entry.CompressionMethod);
        if (entry.IsSymbolicLink || entry.IsWindowsReparsePoint)
        {
            throw new RetainedProcessorException("archive-entry-link-invalid");
        }
    }

    private static void ValidateLocalHeader(ReadOnlySpan<byte> bytes, CentralDirectoryEntry centralEntry)
    {
        const uint localFileHeaderSignature = 0x04034b50;
        var localHeaderOffset = (long)centralEntry.LocalHeaderOffset;
        if (bytes.Length < 30 || localHeaderOffset > bytes.Length - 30)
            throw new RetainedProcessorException("archive-entry-unsupported");
        var offset = (int)localHeaderOffset;
        if (ReadUInt32(bytes, offset) != localFileHeaderSignature) throw new RetainedProcessorException("archive-entry-unsupported");

        var flags = ReadUInt16(bytes, offset + 6);
        var method = ReadUInt16(bytes, offset + 8);
        if (method is not 0 and not 8) throw new RetainedProcessorException("archive-entry-compression-unsupported");
        ValidateGeneralPurposeBitFlag(flags, method);
        if (flags != centralEntry.GeneralPurposeBitFlag || method != centralEntry.CompressionMethod)
            throw new RetainedProcessorException("archive-entry-unsupported");

        var fileNameLength = ReadUInt16(bytes, offset + 26);
        var extraLength = ReadUInt16(bytes, offset + 28);
        var recordLength = checked(30 + fileNameLength + extraLength);
        if (recordLength > bytes.Length - offset || !bytes.Slice(offset + 30, fileNameLength).SequenceEqual(centralEntry.FileName))
            throw new RetainedProcessorException("archive-entry-unsupported");

        var localCrc32 = ReadUInt32(bytes, offset + 14);
        var localCompressedSize = ReadUInt32(bytes, offset + 18);
        var localUncompressedSize = ReadUInt32(bytes, offset + 22);
        if ((flags & 0x0008) == 0)
        {
            if (localCrc32 != centralEntry.Crc32 || localCompressedSize != centralEntry.CompressedSize || localUncompressedSize != centralEntry.UncompressedSize)
                throw new RetainedProcessorException("archive-entry-unsupported");
            return;
        }

        if (localCrc32 != 0 && localCrc32 != centralEntry.Crc32 ||
            localCompressedSize != 0 && localCompressedSize != centralEntry.CompressedSize ||
            localUncompressedSize != 0 && localUncompressedSize != centralEntry.UncompressedSize)
        {
            throw new RetainedProcessorException("archive-entry-unsupported");
        }
    }

    private static void ValidateGeneralPurposeBitFlag(ushort flags, ushort compressionMethod)
    {
        // Data descriptors and UTF-8 names are safe; both DEFLATE option bits are valid only for DEFLATE.
        const ushort basePermittedFlags = 0x0008 | 0x0800;
        const ushort deflateOptionFlags = 0x0006;
        var permittedFlags = compressionMethod == 8 ? (ushort)(basePermittedFlags | deflateOptionFlags) : basePermittedFlags;
        if ((flags & 0x0001) != 0) throw new RetainedProcessorException("archive-entry-encrypted");
        if ((flags & ~permittedFlags) != 0) throw new RetainedProcessorException("archive-entry-unsupported");
    }

    private static IReadOnlyList<CentralDirectoryEntry> ReadCentralDirectory(ReadOnlySpan<byte> bytes)
    {
        const uint endOfCentralDirectorySignature = 0x06054b50;
        const uint centralDirectorySignature = 0x02014b50;
        var minimumOffset = Math.Max(0, bytes.Length - 65_557);
        var endOffset = -1;
        for (var offset = bytes.Length - 22; offset >= minimumOffset; offset--)
        {
            if (ReadUInt32(bytes, offset) == endOfCentralDirectorySignature) { endOffset = offset; break; }
        }
        if (endOffset < 0 || endOffset + 22 > bytes.Length)
        {
            throw new RetainedProcessorException("archive-entry-unsupported");
        }

        var diskNumber = ReadUInt16(bytes, endOffset + 4);
        var centralDirectoryDisk = ReadUInt16(bytes, endOffset + 6);
        var entriesOnDisk = ReadUInt16(bytes, endOffset + 8);
        var totalEntries = ReadUInt16(bytes, endOffset + 10);
        var centralSize = ReadUInt32(bytes, endOffset + 12);
        var centralOffset = ReadUInt32(bytes, endOffset + 16);
        if (diskNumber != 0 || centralDirectoryDisk != 0 || entriesOnDisk != totalEntries || totalEntries == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue ||
            centralOffset > bytes.Length || centralSize > bytes.Length - centralOffset)
        {
            throw new RetainedProcessorException("archive-entry-unsupported");
        }

        var entries = new List<CentralDirectoryEntry>(totalEntries);
        var entryOffset = checked((int)centralOffset);
        var end = checked(entryOffset + (int)centralSize);
        for (var index = 0; index < totalEntries; index++)
        {
            if (entryOffset > end - 46 || ReadUInt32(bytes, entryOffset) != centralDirectorySignature)
            {
                throw new RetainedProcessorException("archive-entry-unsupported");
            }
            var fileNameLength = ReadUInt16(bytes, entryOffset + 28);
            var extraLength = ReadUInt16(bytes, entryOffset + 30);
            var commentLength = ReadUInt16(bytes, entryOffset + 32);
            var recordLength = checked(46 + fileNameLength + extraLength + commentLength);
            if (recordLength > end - entryOffset)
            {
                throw new RetainedProcessorException("archive-entry-unsupported");
            }
            var externalAttributes = ReadUInt32(bytes, entryOffset + 38);
            entries.Add(new CentralDirectoryEntry(
                ReadUInt16(bytes, entryOffset + 8),
                ReadUInt16(bytes, entryOffset + 10),
                ReadUInt32(bytes, entryOffset + 16),
                ReadUInt32(bytes, entryOffset + 20),
                ReadUInt32(bytes, entryOffset + 24),
                bytes.Slice(entryOffset + 46, fileNameLength).ToArray(),
                ReadUInt32(bytes, entryOffset + 42),
                ((externalAttributes >> 16) & 0xF000) == 0xA000,
                (externalAttributes & 0x00000400) != 0));
            entryOffset += recordLength;
        }
        if (entryOffset != end)
        {
            throw new RetainedProcessorException("archive-entry-unsupported");
        }
        return entries;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        offset >= 0 && offset <= bytes.Length - 2 ? BitConverter.ToUInt16(bytes[offset..]) : throw new RetainedProcessorException("archive-entry-unsupported");

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        offset >= 0 && offset <= bytes.Length - 4 ? BitConverter.ToUInt32(bytes[offset..]) : throw new RetainedProcessorException("archive-entry-unsupported");

    private static async ValueTask<byte[]> ReadPrefixAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var offset = 0;
        while (offset < prefix.Length)
        {
            var read = await stream.ReadAsync(prefix.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        return prefix[..offset];
    }

    private static string ComputeUnsafeMemberFingerprint(string parentStableIdentity, int centralDirectoryOrdinal, string rawEntryName) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"archive-member-outcome:{parentStableIdentity.Length}:{parentStableIdentity}:{centralDirectoryOrdinal}:{rawEntryName.Length}:{rawEntryName}")));

    private sealed record CentralDirectoryEntry(
        ushort GeneralPurposeBitFlag,
        ushort CompressionMethod,
        uint Crc32,
        uint CompressedSize,
        uint UncompressedSize,
        byte[] FileName,
        uint LocalHeaderOffset,
        bool IsSymbolicLink,
        bool IsWindowsReparsePoint);
}

/// <summary>Publishes the ZIP processor descriptor without resolving its scoped retained-artifact writer.</summary>
public sealed class ZipArchiveRetainedCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => ZipArchiveRetainedProcessor.Capability;
}

public sealed class RetainedProcessorException(
    string outcomeCode,
    IReadOnlyList<RetainedProcessorMemberOutcome>? memberOutcomes = null,
    Exception? innerException = null)
    : IOException(outcomeCode, innerException)
{
    public string OutcomeCode { get; } = outcomeCode;
    public IReadOnlyList<RetainedProcessorMemberOutcome> MemberOutcomes { get; } = memberOutcomes ?? [];
}
