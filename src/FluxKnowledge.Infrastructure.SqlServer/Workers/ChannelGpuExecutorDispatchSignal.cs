using System.Threading.Channels;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// A bounded, payload-free local prompt to reread durable executor dispatches.
/// It deliberately coalesces prompts and is not an executor work queue.
/// </summary>
public sealed class ChannelGpuExecutorDispatchSignal : IGpuExecutorDispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async ValueTask WaitAsync(CancellationToken cancellationToken) =>
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public bool TryConsume() => _channel.Reader.TryRead(out _);
}
