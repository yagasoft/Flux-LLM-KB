using System.Threading.Channels;
using FluxKnowledge.Application.Workers;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class ChannelOutboxWakeSignal : IOutboxWakeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Notify()
    {
        _channel.Writer.TryWrite(true);
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
