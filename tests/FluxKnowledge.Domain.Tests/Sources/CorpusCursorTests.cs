using FluxKnowledge.Application.Contracts;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class CorpusCursorTests
{
    [Fact]
    public void Cursor_rejects_a_different_filter_fingerprint()
    {
        var cursor = CorpusCursor.Create(DateTimeOffset.UnixEpoch, Guid.NewGuid(), "root=a");

        Assert.Throws<ArgumentException>(() => cursor.ValidateFor("root=b"));
    }

    [Fact]
    public void Query_uses_a_bounded_default_page_size()
    {
        Assert.Equal(50, new CorpusQuery().PageSize);
        Assert.Equal(200, new CorpusQuery(PageSize: 1000).PageSize);
    }
}
