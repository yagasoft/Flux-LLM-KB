using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceJournalCursorValidationTests
{
    [Fact]
    public void Repeated_historical_rejection_after_a_valid_control_requires_recovery()
    {
        var original = new IOException("native", new Win32Exception(87));
        var calls = new List<long>();
        var error = Assert.Throws<SourceJournalGapException>(() => WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, default, start =>
        {
            calls.Add(start);
            return start == 100 ? throw original : EmptyPage(1000);
        }));
        Assert.Equal("SourceJournalCursorRejected", error.Message);
        Assert.Same(original, error.InnerException);
        Assert.Equal(new long[] { 100, 1000, 100 }, calls);
        Assert.IsNotType<SourceJournalGapException>(WindowsSourceChangeJournalReader.NativeFailure(87));
    }

    [Fact]
    public void A_transient_rejection_returns_original_history_without_skipping_to_the_control_head()
    {
        var calls = new List<long>();
        var history = HistoryPage();
        var bytes = WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, default, start =>
        {
            calls.Add(start);
            if (calls.Count == 1) throw new IOException("transient", new Win32Exception(87));
            return start == 1000 ? EmptyPage(1000) : history;
        });
        Assert.Same(history, bytes);
        var parsed = UsnJournalParser.Parse(bytes, 100);
        Assert.Equal(172, parsed.NextUsn);
        Assert.Equal("r.txt", Assert.Single(parsed.Changes).Name);
        Assert.Equal(new long[] { 100, 1000, 100 }, calls);
    }

    [Fact]
    public void Healthy_reads_keep_one_native_call()
    {
        var calls = 0;
        var history = HistoryPage();
        Assert.Same(history, WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, default, start =>
        { calls++; Assert.Equal(100, start); return history; }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1181)]
    public void A_different_retry_failure_keeps_its_unavailability_or_explicit_gap_semantics(int nativeCode)
    {
        var retryFailure = WindowsSourceChangeJournalReader.NativeFailure(nativeCode);
        var calls = new List<long>();
        var error = Record.Exception(() => WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, default, start =>
        {
            calls.Add(start);
            if (calls.Count == 1) throw new IOException("first", new Win32Exception(87));
            return start == 1000 ? EmptyPage(1000) : throw retryFailure;
        }));
        Assert.Same(retryFailure, error);
        Assert.Equal(new long[] { 100, 1000, 100 }, calls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("87")]
    [InlineData("5")]
    public void Cancellation_during_the_original_cursor_retry_cannot_authorise_recovery(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<long>();
        Assert.ThrowsAny<OperationCanceledException>(() => WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, cancellation.Token, start =>
        {
            calls.Add(start);
            if (calls.Count == 1) throw new IOException("first", new Win32Exception(87));
            if (start == 1000) return EmptyPage(1000);
            cancellation.Cancel();
            return outcome == "success" ? HistoryPage() : throw new IOException("retry", new Win32Exception(int.Parse(outcome)));
        }));
        Assert.Equal(new long[] { 100, 1000, 100 }, calls);
    }

    [Fact]
    public void Malformed_retry_data_retains_the_existing_record_validation_failure()
    {
        var calls = new List<long>();
        var error = Assert.Throws<SourceJournalGapException>(() => UsnJournalParser.Parse(
            WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, default, start =>
            {
                calls.Add(start);
                if (calls.Count == 1) throw new IOException("first", new Win32Exception(87));
                return start == 1000 ? EmptyPage(1000) : [];
            }), 100));
        Assert.Equal("SourceJournalRecordInvalid", error.Message);
        Assert.Equal(new long[] { 100, 1000, 100 }, calls);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("short")]
    [InlineData("regression")]
    public void Failed_or_malformed_controls_keep_the_original_unavailability(string failure)
    {
        var original = new IOException("original", new Win32Exception(87));
        var calls = 0;
        var error = Assert.Throws<IOException>(() => WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, default, start =>
        {
            calls++;
            if (start == 100) throw original;
            return failure switch { "io" => throw new IOException("control"), "short" => [], _ => EmptyPage(999) };
        }));
        Assert.Same(original, error);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_before_or_during_the_control_cannot_authorise_recovery(bool duringControl)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => WindowsSourceChangeJournalReader.ReadWithCursorValidation(100, 1000, cancellation.Token, start =>
        {
            calls++;
            if (!duringControl || start == 1000) cancellation.Cancel();
            return start == 100 ? throw new IOException("native", new Win32Exception(87)) : EmptyPage(1000);
        }));
        Assert.Equal(duringControl ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(87, 1000)]
    [InlineData(5, 100)]
    [InlineData(1178, 100)]
    public void Head_rejection_and_other_native_failures_do_not_probe(int nativeCode, long startUsn)
    {
        var original = new IOException("native", new Win32Exception(nativeCode));
        var calls = 0;
        Assert.Same(original, Assert.Throws<IOException>(() => WindowsSourceChangeJournalReader.ReadWithCursorValidation(startUsn, 1000, default, _ =>
        { calls++; throw original; })));
        Assert.Equal(1, calls);
    }

    private static byte[] EmptyPage(long next)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, next);
        return bytes;
    }

    private static byte[] HistoryPage()
    {
        var bytes = new byte[80];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 172);
        var record = bytes.AsSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(record, 72);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], 42);
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], 7);
        BinaryPrimitives.WriteInt64LittleEndian(record[24..], 100);
        BinaryPrimitives.WriteUInt32LittleEndian(record[40..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record[56..], 10);
        BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 60);
        Encoding.Unicode.GetBytes("r.txt").CopyTo(record[60..]);
        return bytes;
    }
}
