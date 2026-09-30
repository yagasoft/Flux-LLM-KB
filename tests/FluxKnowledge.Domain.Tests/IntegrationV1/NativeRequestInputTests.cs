using System.Text;
using FluxKnowledge.Application.IntegrationV1;
using Xunit;

namespace FluxKnowledge.Domain.Tests.IntegrationV1;

[Collection("Native ingress pressure")]
public sealed class NativeRequestInputTests
{
    [Fact]
    public async Task Resource_pressure_refuses_input_and_releases_its_reservation()
    {
        await using var input = new MemoryStream("{\"action\":\"root_create\"}"u8.ToArray());
        var failure = await Assert.ThrowsAsync<NativeOperationException>(() => NativeRequestInput.ReadJsonAsync(input, CancellationToken.None, () => 0));
        Assert.Equal("resource-pressure", failure.ReasonCode);
        input.Position = 0;
        using var document = await NativeRequestInput.ReadJsonAsync(input, CancellationToken.None);
        Assert.Equal("root_create", document.RootElement.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("{\"action\":1,\"action\":2}")]
    [InlineData("{\"payload\":{\"path\":1,\"path\":2}}")]
    public async Task Duplicate_command_properties_are_refused_before_dispatch(string json)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<NativeOperationException>(() => NativeRequestInput.ReadJsonAsync(input, CancellationToken.None));
    }

    [Fact]
    public void Competing_input_reservations_refuse_pressure_until_the_other_request_releases()
    {
        using var first = new NativeRequestInput.Reservation(() => 1024);
        using var second = new NativeRequestInput.Reservation(() => 1024);
        first.AddInputBytes(16);
        Assert.Equal("resource-pressure", Assert.Throws<NativeOperationException>(() => second.AddInputBytes(17)).ReasonCode);
        first.Dispose(); second.AddInputBytes(17);
    }

    [Fact]
    public async Task Larger_escaped_multibyte_input_is_complete_and_a_malformed_suffix_is_refused()
    {
        var value = string.Concat(Enumerable.Repeat("\u0800\"\\", 20000));
        var json = System.Text.Json.JsonSerializer.Serialize(new { value });
        await using var valid = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var document = await NativeRequestInput.ReadJsonAsync(valid, default);
        Assert.Equal(value, document.RootElement.GetProperty("value").GetString());
        await using var malformed = new MemoryStream(Encoding.UTF8.GetBytes(json + " trailing-invalid-json"));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => NativeRequestInput.ReadJsonAsync(malformed, default));
    }

    [Fact]
    public async Task Cancellation_interrupts_slow_input_and_releases_ingress_resources()
    {
        using var stop = new CancellationTokenSource();
        await using var stream = new SlowStream();
        var read = NativeRequestInput.ReadJsonAsync(stream, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        using var reservation = new NativeRequestInput.Reservation(() => 1024); reservation.AddInputBytes(32);
    }

    private sealed class SlowStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
}

[CollectionDefinition("Native ingress pressure", DisableParallelization = true)]
public sealed class NativeIngressPressureCollection;
