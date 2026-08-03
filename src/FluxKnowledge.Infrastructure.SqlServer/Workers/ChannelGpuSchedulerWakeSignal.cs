using System.Threading.Channels;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class ChannelGpuSchedulerWakeSignal : IGpuSchedulerWakeSignal
{
    private readonly object _sync = new();
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private GpuSchedulerWakeReason _pendingReasons;

    public void Notify(GpuSchedulerWakeReason reason)
    {
        if (reason == 0)
        {
            return;
        }

        lock (_sync)
        {
            _pendingReasons |= reason;
            _channel.Writer.TryWrite(true);
        }
    }

    public async ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken)
    {
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var reasons = _pendingReasons;
            _pendingReasons = 0;
            return reasons;
        }
    }
}
