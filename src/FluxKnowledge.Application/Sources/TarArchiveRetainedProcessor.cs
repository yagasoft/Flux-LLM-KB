using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Processes only checksum-verified retained ustar/GNU TAR bytes into content-addressed child artifacts.</summary>
public sealed class TarArchiveRetainedProcessor(IRetainedArtifactWriter artifactWriter) : ILocalSourceCapabilityHandler
{
    private const int MaximumTarCompressionRatio = 100;
    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("3d8e4b4e-8d16-45c7-aa02-c4e546ba997d"),
        "archive-tar-expand",
        "phase-5-tar-v1",
        ExecutionClass.InProcess,
        "phase-5-tar-retained-archive-v1",
        SourceActivityKind.ArchiveExpansion,
        "ArchiveTar",
        "retained:archive-tar-expand");

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
        if (!IsTarSignature(retained.Bytes))
        {
            throw new RetainedProcessorException("archive-signature-invalid");
        }

        try
        {
            ValidateRawHeaderNames(claim.ParentStableIdentity, retained.Bytes);
            var prepared = Preflight(claim, retained.Bytes, options, cancellationToken);
            var members = new List<RetainedProcessorDerivedChild>(prepared.Count);
            using var input = new MemoryStream(retained.Bytes, writable: false);
            using var reader = new TarReader(input, leaveOpen: false);
            var ordinal = 0;
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType == TarEntryType.Directory) continue;
                if (ordinal >= prepared.Count || !string.Equals(entry.Name, prepared[ordinal].Name, StringComparison.Ordinal))
                {
                    throw new RetainedProcessorException("archive-entry-unsupported");
                }
                var stream = entry.DataStream ?? throw new RetainedProcessorException("archive-entry-unsupported");
                var receipt = await artifactWriter.WriteAsync(claim.SourceRevisionId, stream, options.MaximumMemberBytes, cancellationToken).ConfigureAwait(false);
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
                members.Add(RetainedProcessorDerivedChild.ArchiveMember(prepared[ordinal].Identity, receipt.ContentSha256, receipt.StoreRelativePath,
                    receipt.ByteLength, "AcceptedUtf8Text"));
                ordinal++;
            }
            if (ordinal != prepared.Count)
            {
                throw new RetainedProcessorException("archive-entry-unsupported");
            }
            var receiptFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("|", members.OrderBy(member => member.MemberFingerprint, StringComparer.Ordinal)
                    .Select(member => $"{member.MemberFingerprint}:{member.ContentSha256}:{member.ByteLength}")))));
            return new RetainedProcessorCompletion(members, receiptFingerprint);
        }
        catch (RetainedProcessorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or FormatException or EndOfStreamException)
        {
            throw new RetainedProcessorException("archive-entry-unsupported",
                [new RetainedProcessorMemberOutcome(ComputeOpaqueMemberFingerprint(claim.ParentStableIdentity, 0, 0), 0, "blocked", "archive-entry-unsupported")], exception);
        }
    }

    public static bool IsTarSignature(ReadOnlySpan<byte> value) => value.Length >= 512 &&
        value.Slice(257, 5).SequenceEqual("ustar"u8) && value[262] is 0 or (byte)' ';

    private static void ValidateRawHeaderNames(string parentStableIdentity, ReadOnlySpan<byte> bytes)
    {
        var offset = 0L;
        var ordinal = 0;
        while (offset <= bytes.Length - 512)
        {
            var header = bytes.Slice(checked((int)offset), 512);
            if (header.IndexOfAnyExcept((byte)0) < 0) return;
            if (HasEmbeddedNul(header[..100]) || HasEmbeddedNul(header.Slice(345, 155)))
            {
                throw new RetainedProcessorException("archive-entry-path-invalid",
                    [new RetainedProcessorMemberOutcome(ComputeOpaqueMemberFingerprint(parentStableIdentity, ordinal, offset), 0, "blocked", "archive-entry-path-invalid")]);
            }
            var length = ParseOctalLength(header.Slice(124, 12));
            var blockLength = checked(((length + 511) / 512) * 512);
            offset = checked(offset + 512 + blockLength);
            ordinal++;
        }
    }

    private static bool HasEmbeddedNul(ReadOnlySpan<byte> field)
    {
        var firstNul = field.IndexOf((byte)0);
        return firstNul >= 0 && field[(firstNul + 1)..].IndexOfAnyExcept((byte)0) >= 0;
    }

    private static long ParseOctalLength(ReadOnlySpan<byte> field)
    {
        var value = 0L;
        foreach (var character in field)
        {
            if (character is 0 or (byte)' ') break;
            if (character is < (byte)'0' or > (byte)'7') throw new InvalidDataException("The TAR size field is invalid.");
            value = checked(value * 8 + character - (byte)'0');
        }
        return value;
    }

    private static List<PreparedMember> Preflight(
        RetainedProcessorClaim claim,
        byte[] bytes,
        RetainedProcessorOptions options,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new TarReader(input, leaveOpen: false);
        var count = 0;
        var expandedTotal = 0L;
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new List<RetainedProcessorMemberOutcome>();
        var prepared = new List<PreparedMember>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > options.MaximumEntryCount)
            {
                throw new RetainedProcessorException("archive-entry-count-limit");
            }
            try
            {
                var path = ValidateEntryPath(entry.Name, options.MaximumLogicalPathLength);
                ValidateEntry(entry, options, bytes.Length);
                if (entry.EntryType == TarEntryType.Directory) continue;
                expandedTotal = checked(expandedTotal + entry.Length);
                if (expandedTotal > options.MaximumExpandedBytes) throw new RetainedProcessorException("archive-expanded-total-limit");
                var identity = ArchiveMemberIdentity.Create(claim.ParentStableIdentity, path);
                if (!fingerprints.Add(identity.MemberFingerprint)) throw new RetainedProcessorException("archive-member-identity-conflict");
                var data = entry.DataStream ?? throw new RetainedProcessorException("archive-entry-unsupported");
                if (IsTarSignature(ReadPrefix(data, cancellationToken))) throw new RetainedProcessorException("nested-archive-depth-limit");
                prepared.Add(new PreparedMember(entry.Name, identity));
            }
            catch (RetainedProcessorException exception)
            {
                blocked.Add(new RetainedProcessorMemberOutcome(
                    ComputeUnsafeMemberFingerprint(claim.ParentStableIdentity, count - 1, entry.Name), Math.Max(0, entry.Length), "blocked", exception.OutcomeCode));
            }
        }
        if (blocked.Count > 0)
        {
            throw new RetainedProcessorException(blocked[0].ReasonCode, blocked);
        }
        return prepared;
    }

    private static void ValidateEntry(TarEntry entry, RetainedProcessorOptions options, int inputLength)
    {
        if (entry.EntryType == TarEntryType.Directory) return;
        if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink or TarEntryType.CharacterDevice or TarEntryType.BlockDevice or TarEntryType.Fifo)
        {
            throw new RetainedProcessorException("archive-entry-link-invalid");
        }
        if (entry.EntryType != TarEntryType.RegularFile)
        {
            throw new RetainedProcessorException("archive-entry-unsupported");
        }
        if (entry.Length < 0 || entry.Length > options.MaximumMemberBytes)
        {
            throw new RetainedProcessorException("archive-member-size-limit");
        }
        if (inputLength == 0 || entry.Length > checked((long)inputLength * Math.Min(options.MaximumCompressionRatio, MaximumTarCompressionRatio)))
        {
            throw new RetainedProcessorException("archive-compression-ratio-limit");
        }
    }

    private static string ValidateEntryPath(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.IndexOf('\0') >= 0 ||
            value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("\\", StringComparison.Ordinal) || value.Contains(':') || value.Contains('\\'))
        {
            throw new RetainedProcessorException("archive-entry-path-invalid");
        }
        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new RetainedProcessorException("archive-entry-path-invalid");
        }
        return string.Join('/', segments);
    }

    private static byte[] ReadPrefix(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[512];
        var offset = 0;
        while (offset < prefix.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(prefix, offset, prefix.Length - offset);
            if (read == 0) break;
            offset += read;
        }
        return prefix[..offset];
    }

    private static string ComputeUnsafeMemberFingerprint(string parentStableIdentity, int ordinal, string rawEntryName) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"archive-member-outcome:{parentStableIdentity.Length}:{parentStableIdentity}:{ordinal}:{rawEntryName.Length}:{rawEntryName}")));

    private static string ComputeOpaqueMemberFingerprint(string parentStableIdentity, int ordinal, long offset) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"archive-member-opaque-outcome:{parentStableIdentity.Length}:{parentStableIdentity}:{ordinal}:{offset}")));

    private sealed record PreparedMember(string Name, ArchiveMemberIdentity Identity);
}

/// <summary>Publishes the TAR processor descriptor without resolving its scoped retained-artifact writer.</summary>
public sealed class TarArchiveRetainedCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => TarArchiveRetainedProcessor.Capability;
}
