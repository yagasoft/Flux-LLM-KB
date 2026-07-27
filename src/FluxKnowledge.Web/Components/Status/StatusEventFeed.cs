using System.Collections.Concurrent;
using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web.Components.Status;

public sealed class StatusEventFeed : IStatusEventPublisher
{
    private readonly ConcurrentDictionary<Guid, Channel<StatusChanged>> _subscribers = new();

    public StatusEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<StatusChanged>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        if (!_subscribers.TryAdd(id, channel))
        {
            throw new InvalidOperationException("A status event subscriber could not be registered.");
        }

        return new StatusEventSubscription(channel.Reader, () => Unsubscribe(id));
    }

    public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(statusChanged);
        }

        return ValueTask.CompletedTask;
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}

public sealed class StatusEventSubscription(ChannelReader<StatusChanged> reader, Action dispose) : IAsyncDisposable
{
    private Action? _dispose = dispose;

    public ChannelReader<StatusChanged> Reader { get; } = reader;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
