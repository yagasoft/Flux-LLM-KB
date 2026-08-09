using FluxKnowledge.Application.Contracts;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class OperatorEventContractsTests
{
    [Fact]
    public void Event_cursor_rejects_a_different_filter_fingerprint()
    {
        var cursor = OperatorEventCursor.Create(DateTimeOffset.UnixEpoch, 1, "family=scan");

        Assert.Throws<ArgumentException>(() => cursor.ValidateFor("family=source"));
    }

    [Fact]
    public void Event_query_bounds_the_page_size()
    {
        Assert.Equal(200, new OperatorEventQuery(PageSize: 1000).PageSize);
    }
}
