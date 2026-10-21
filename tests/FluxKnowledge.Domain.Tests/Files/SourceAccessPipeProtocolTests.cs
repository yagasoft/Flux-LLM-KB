using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceAccessPipeProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(262145)]
    public async Task Invalid_frame_lengths_are_rejected_before_payload_allocation(int length)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Response>(stream, default));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Version\":2,\"Outcome\":0}")]
    [InlineData("{\"Version\":1,\"Outcome\":0,\"Command\":\"grant-write\"}")]
    public async Task Duplicate_or_unknown_fields_cannot_extend_the_closed_protocol(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream();
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        stream.Write(header); stream.Write(bytes); stream.Position = 0;
        var failure = await Record.ExceptionAsync(() => SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Response>(stream, default));
        Assert.True(failure is InvalidDataException or JsonException);
    }

    [Fact]
    public void Pipe_name_cannot_select_a_remote_endpoint_or_arbitrary_service()
    {
        foreach (var name in new[] { @"\\server\pipe\escape", @"FluxKnowledge.SourceAccess.\escape", "OtherService", "" })
            Assert.Throws<ArgumentException>(() => new WindowsSourceAccessRepairClient(name));
    }

    [Fact]
    public void Cancelled_request_performs_no_connection_or_privileged_request()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var client = new WindowsSourceAccessRepairClient("FluxKnowledge.SourceAccess.absent");
        Assert.Throws<OperationCanceledException>(() => client.TryRepair(new(@"I:\fixture", null, "file.txt", false), cancellation.Token));
    }
}
