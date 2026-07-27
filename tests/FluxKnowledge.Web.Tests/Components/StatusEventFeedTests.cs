using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class StatusEventFeedTests
{
    [Fact]
    public async Task Feed_delivers_a_published_status_invalidation_to_each_subscriber()
    {
        var feed = new StatusEventFeed();
        await using var first = feed.Subscribe();
        await using var second = feed.Subscribe();
        var changed = new StatusChanged(null, "pipeline", DateTimeOffset.UtcNow);

        await feed.PublishAsync(changed, CancellationToken.None);

        Assert.Equal(changed, await first.Reader.ReadAsync());
        Assert.Equal(changed, await second.Reader.ReadAsync());
    }

    [Fact]
    public async Task Connection_up_publishes_a_reconnect_invalidation()
    {
        var occurredAtUtc = DateTimeOffset.Parse("2026-07-27T05:00:00Z");
        var feed = new StatusEventFeed();
        await using var subscription = feed.Subscribe();
        var handler = new StatusEventCircuitHandler(feed, new FixedTimeProvider(occurredAtUtc));

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.True(subscription.Reader.TryRead(out var changed));
        Assert.NotNull(changed);
        Assert.Equal("reconnect", changed.Projection);
        Assert.Equal(occurredAtUtc, changed.OccurredAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
