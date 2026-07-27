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
}
