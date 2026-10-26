using System.Buffers.Binary;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceJournalReaderTests
{
    [Theory]
    [InlineData(1181, true)]
    [InlineData(1178, false)]
    [InlineData(1179, false)]
    public void Native_deleted_history_requires_recovery_while_unavailable_journals_retain_the_cursor(int nativeError, bool continuityLost)
    {
        var error = WindowsSourceChangeJournalReader.NativeFailure(nativeError);
        Assert.Equal(continuityLost, error is SourceJournalGapException);
    }

    [Fact]
    public void Unsupported_record_or_reason_reaches_the_typed_continuity_recovery_path()
    {
        Assert.IsType<SourceJournalGapException>(Record.Exception(() => UsnJournalParser.Parse(Page(4, 76, "a", 1), 100)));
        Assert.IsType<SourceJournalGapException>(Record.Exception(() => UsnJournalParser.Parse(Page(2, 60, "a", 0x40000000), 100)));
    }

    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x2u)]
    [InlineData(0x4u)]
    [InlineData(0x10u)]
    [InlineData(0x20u)]
    [InlineData(0x40u)]
    public void Every_data_mutation_reason_requires_capture(uint reason)
    {
        Assert.True(Assert.Single(UsnJournalParser.Parse(Page(2, 60, "a", reason), 100).Changes).ContentChanged);
    }

    [Fact]
    public void Parent_metadata_open_uses_the_documented_Win32_no_recall_flags()
    {
        // OpenFileById/Win32 flags, not NtCreateFile's differently numbered FILE_OPEN_NO_RECALL.
        var flags = WindowsSourceChangeJournalReader.ParentMetadataOpenFlags;
        Assert.NotEqual(0u, flags & 0x00100000u); // FILE_FLAG_OPEN_NO_RECALL
        Assert.NotEqual(0u, flags & 0x00200000u); // FILE_FLAG_OPEN_REPARSE_POINT
        Assert.NotEqual(0u, flags & 0x02000000u); // FILE_FLAG_BACKUP_SEMANTICS for a directory
        Assert.Equal(0u, flags & ~0x02300000u);
    }

    [Theory]
    [InlineData(2, 60)]
    [InlineData(3, 76)]
    public void Reads_bounded_records_and_continuation_without_requiring_close(ushort version, int header)
    {
        var bytes = Page(version, header, "report.docx", 0x1);
        var result = UsnJournalParser.Parse(bytes, 100);
        Assert.Equal(500, result.NextUsn);
        var change = Assert.Single(result.Changes);
        Assert.Equal("report.docx", change.Name);
        Assert.Equal("000000000000002A", change.FileId);
        Assert.Equal("0000000000000007", change.ParentId);
        Assert.Equal(200, change.Usn);
        Assert.True(change.ContentChanged);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public void Refuses_unknown_record_layouts(ushort version) =>
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(Page(version, 76, "a", 1), 100));

    [Fact]
    public void Refuses_truncated_lengths_and_invalid_names()
    {
        var bytes = Page(2, 60, "safe", 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4096);
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(bytes, 100));
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(Page(2, 60, "../escape", 1), 100));
        bytes = Page(2, 60, "safe", 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + 56), 3);
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(bytes, 100));
    }

    [Fact]
    public void Refuses_regression_oversized_pages_and_unclassified_reasons()
    {
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(Page(2, 60, "a", 1), 501));
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(new byte[1024 * 1024 + 1], 0));
        Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(Page(2, 60, "a", 0x40000000), 100));
    }

    [Fact]
    public void Metadata_change_does_not_claim_a_new_content_hash()
    {
        var change = Assert.Single(UsnJournalParser.Parse(Page(2, 60, "a", 0x8000), 100).Changes);
        Assert.False(change.ContentChanged);
    }

    private static byte[] Page(ushort version, int header, string name, uint reason)
    {
        var text = Encoding.Unicode.GetBytes(name);
        var length = (header + text.Length + 7) & ~7;
        var bytes = new byte[8 + length];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 500);
        var record = bytes.AsSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(record, length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], version);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], 42);
        BinaryPrimitives.WriteUInt64LittleEndian(record[(version == 2 ? 16 : 24)..], 7);
        var shift = version == 2 ? 0 : 16;
        BinaryPrimitives.WriteInt64LittleEndian(record[(24 + shift)..], 200);
        BinaryPrimitives.WriteUInt32LittleEndian(record[(40 + shift)..], reason);
        BinaryPrimitives.WriteUInt16LittleEndian(record[(56 + shift)..], (ushort)text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[(58 + shift)..], (ushort)header);
        text.CopyTo(record[header..]);
        return bytes;
    }
}
