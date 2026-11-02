using System.Buffers.Binary;
using System.ComponentModel;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceJournalCursorValidationTests
{
    [Fact]
    public void Only_a_valid_control_read_proves_rejected_history()
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
        Assert.Equal(new long[] { 100, 1000 }, calls);
        Assert.IsNotType<SourceJournalGapException>(WindowsSourceChangeJournalReader.NativeFailure(87));
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
}
