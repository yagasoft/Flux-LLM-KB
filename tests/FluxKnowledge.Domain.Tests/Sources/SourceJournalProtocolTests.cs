using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceJournalProtocolTests
{
    [Fact]
    public async Task Separate_protocol_round_trips_a_read_request_without_repair_or_mutation()
    {
        using var stream = new MemoryStream();
        var request = new SourceJournalPipeProtocol.Request(1, DateTimeOffset.UtcNow.AddSeconds(3).UtcTicks,
            "read", new JournalCursor(@"\\?\Volume{11111111-1111-1111-1111-111111111111}\", ulong.MaxValue, 123), 65536);
        await SourceJournalPipeProtocol.WriteAsync(stream, request, default);
        stream.Position = 0;
        Assert.Equal(request, await SourceJournalPipeProtocol.ReadAsync<SourceJournalPipeProtocol.Request>(stream, default));
        Assert.True(SourceJournalPipeProtocol.IsAllowed(request, request.Cursor.VolumeIdentity));
        Assert.False(SourceJournalPipeProtocol.IsAllowed(request with { Operation = "resize" }, request.Cursor.VolumeIdentity));
        Assert.False(SourceJournalPipeProtocol.IsAllowed(request, @"\\?\Volume{22222222-2222-2222-2222-222222222222}\"));
        Assert.False(SourceJournalPipeProtocol.IsAllowed(request with { MaximumBytes = 1024 * 1024 + 1 }, request.Cursor.VolumeIdentity));
        Assert.False(SourceJournalPipeProtocol.IsAllowed(request with { DeadlineUtcTicks = 1 }, request.Cursor.VolumeIdentity));
    }

    [Fact]
    public async Task Rejects_oversized_frames_before_allocation()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceJournalPipeProtocol.ReadAsync<SourceJournalPipeProtocol.Request>(stream, default));
    }
}
