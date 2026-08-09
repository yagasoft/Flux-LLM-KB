using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class CorpusProjectionTests
{
    [Fact]
    public void Corpus_query_rejects_a_cursor_created_for_other_filters()
    {
        var cursor = CorpusCursor.Create(DateTimeOffset.UnixEpoch, Guid.NewGuid(), new CorpusQuery().CanonicalFilter);
        Assert.Throws<ArgumentException>(() => new CorpusQuery(new CorpusFilters(SourceKind: "local file"), Cursor: cursor));
    }

    [Theory]
    [InlineData("not-a-stage", null)]
    [InlineData(null, "not-a-state")]
    public void Corpus_query_rejects_unknown_status_filters(string? pipelineStatus, string? activityStatus)
    {
        Assert.Throws<ArgumentException>(() => new CorpusQuery(new CorpusFilters(PipelineStatus: pipelineStatus, SourceActivityStatus: activityStatus)));
    }
}
