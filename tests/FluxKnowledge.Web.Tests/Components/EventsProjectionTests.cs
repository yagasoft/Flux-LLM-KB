using FluxKnowledge.Application.Contracts;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class EventsProjectionTests
{
    [Fact]
    public void Event_query_rejects_a_cursor_created_for_other_filters()
    {
        var cursor = OperatorEventCursor.Create(DateTimeOffset.UnixEpoch, 1, new OperatorEventQuery().CanonicalFilter);
        Assert.Throws<ArgumentException>(() => new OperatorEventQuery(new OperatorEventFilters(Family: "watch"), Cursor: cursor));
    }
}
