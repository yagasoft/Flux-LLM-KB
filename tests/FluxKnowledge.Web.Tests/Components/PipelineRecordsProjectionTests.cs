using FluxKnowledge.Web.Components.Status;
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

    [Fact]
    public void Pipeline_projection_names_the_job_due_time_truthfully()
    {
        var dueAtUtc = DateTimeOffset.Parse("2026-07-27T05:00:00Z");
        var projection = new PipelineRecordProjection(
            Guid.NewGuid(),
            "C:/ingress/known.txt",
            1,
            "Extract",
            "WorkerQueued",
            "0123456789ab",
            DateTimeOffset.UnixEpoch,
            dueAtUtc);

        Assert.Equal(dueAtUtc, projection.DueAtUtc);
    }
}
