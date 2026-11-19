using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class RetainedSourceReadLeaseTests
{
    [Fact]
    public async Task Opening_retained_content_observes_metadata_without_reading_the_payload()
    {
        var bytes = "retained source"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var input = new CountingStream(bytes);
        await using var lease = await RetainedSourceReadLease.OpenAsync(SourceRevisionId.New(), input,
            digest, bytes.Length, 1024, default);
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(digest, lease.ContentSha256);
        var actual = new byte[bytes.Length];
        await lease.Stream.ReadExactlyAsync(actual);
        Assert.Equal(bytes, actual);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(1025)]
    public async Task Invalid_retained_lengths_are_rejected_without_a_payload_read(long recordedLength)
    {
        var input = new CountingStream("source"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => RetainedSourceReadLease.OpenAsync(
            SourceRevisionId.New(), input, new string('a', 64), recordedLength, 1024, default).AsTask());
        Assert.Equal(0, input.ReadCalls);
        Assert.False(input.CanRead);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int ReadCalls;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
