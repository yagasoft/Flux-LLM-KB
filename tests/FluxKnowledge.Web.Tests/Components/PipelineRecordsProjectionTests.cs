using FluxKnowledge.Web.Components.Shared;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class PipelineRecordsProjectionTests
{
    [Theory]
    [InlineData(999, "999")]
    [InlineData(1000, "999+")]
    public void Status_count_formats_the_visible_upper_bound(int jobs, string expected)
    {
        Assert.Equal(expected, StatusCount.Format(jobs));
    }
}
