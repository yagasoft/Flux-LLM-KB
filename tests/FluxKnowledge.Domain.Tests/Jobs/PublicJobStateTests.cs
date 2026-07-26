using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Jobs;

public sealed class PublicJobStateTests
{
    [Fact]
    public void Pending_is_a_derived_name_for_worker_queued_only()
    {
        var queued = Job.CreateQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Extract, "extract");

        Assert.Equal(PublicJobState.WorkerQueued, queued.PublicState);
        Assert.True(queued.IsPending);
        Assert.Equal("worker queued", queued.PublicState.ToWireValue());
        Assert.DoesNotContain("pending", PublicJobStateExtensions.AllWireValues);
    }

    [Fact]
    public void Capacity_return_keeps_the_job_in_its_existing_queue_family()
    {
        var job = Job.CreateGpuQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Embed, "embed");
        var returned = job.ReturnForCapacity(DateTimeOffset.Parse("2026-07-26T10:00:00Z"));

        Assert.Equal(PublicJobState.GpuQueued, returned.PublicState);
        Assert.Equal(DateTimeOffset.Parse("2026-07-26T10:00:00Z"), returned.DueAtUtc);
        Assert.DoesNotContain(returned.PublicState.ToWireValue(), new[] { "retrying", "blocked", "parked" });
    }
}
