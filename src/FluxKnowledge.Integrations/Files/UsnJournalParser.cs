using System.Buffers.Binary;
using System.Text;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

internal static class UsnJournalParser
{
    internal const int MaximumPageBytes = 1024 * 1024;
    // All documented reasons through TRANSACTED_CHANGE, INTEGRITY_CHANGE, plus CLOSE.
    private const uint KnownReasons = 0x80ffff77;
    private static readonly Encoding Names = new UnicodeEncoding(false, false, true);

    internal static JournalRecords Parse(ReadOnlySpan<byte> bytes, long startUsn)
    {
        if (bytes.Length < 8 || bytes.Length > MaximumPageBytes) throw Invalid();
        var next = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (startUsn < 0 || next < startUsn) throw Invalid();
        var result = new List<JournalChange>();
        var offset = 8;
        while (offset < bytes.Length)
        {
            var record = bytes[offset..];
            if (record.Length < 8) throw Invalid();
            var length = BinaryPrimitives.ReadInt32LittleEndian(record);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var shift = version switch { 2 => 0, 3 => 16, _ => throw Invalid() };
            var header = 60 + shift;
            if (length < header || length > record.Length || (length & 7) != 0) throw Invalid();
            record = record[..length];
            var usn = BinaryPrimitives.ReadInt64LittleEndian(record[(24 + shift)..]);
            var reason = BinaryPrimitives.ReadUInt32LittleEndian(record[(40 + shift)..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[(56 + shift)..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(58 + shift)..]);
            if (usn < startUsn || usn >= next || (reason & ~KnownReasons) != 0 || nameOffset < header ||
                nameLength == 0 || (nameLength & 1) != 0 || (nameOffset & 1) != 0 || nameLength > length - nameOffset)
                throw Invalid();
            string name;
            try { name = Names.GetString(record.Slice(nameOffset, nameLength)); }
            catch (DecoderFallbackException) { throw Invalid(); }
            if (name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0) throw Invalid();
            var file = Identity(record.Slice(8, version == 2 ? 8 : 16));
            var parent = Identity(record.Slice(version == 2 ? 16 : 24, version == 2 ? 8 : 16));
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[(52 + shift)..]);
            result.Add(new(usn, file, parent, name, reason, (attributes & 0x10) != 0));
            offset += length;
        }
        return new(next, result);
    }

    private static string Identity(ReadOnlySpan<byte> bytes) => bytes.Length == 8 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) == 0
        ? BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString("X16")
        : BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]).ToString("X16") + BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString("X16");
    private static SourceJournalGapException Invalid() => new("SourceJournalRecordInvalid");
}
