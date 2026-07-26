using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Jobs;

public sealed class PublicJobStateTests
{
    [Fact]
    public void Public_states_and_wire_values_are_exactly_the_six_permanent_values()
    {
        Assert.Equal(
            new[]
            {
                PublicJobState.WorkerQueued,
                PublicJobState.WorkerProcessing,
                PublicJobState.GpuQueued,
                PublicJobState.GpuProcessing,
                PublicJobState.Completed,
                PublicJobState.Failed
            },
            Enum.GetValues<PublicJobState>());
        Assert.Equal(
            new[] { "worker queued", "worker processing", "gpu queued", "gpu processing", "completed", "failed" },
            PublicJobStateExtensions.AllWireValues);
    }

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
        var processing = Job
            .CreateGpuQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Embed, "embed")
            .ClaimGpu("gpu-worker", DateTimeOffset.Parse("2026-07-26T09:00:00Z"));
        var returned = processing.ReturnForCapacity(DateTimeOffset.Parse("2026-07-26T10:00:00Z"));

        Assert.Equal(PublicJobState.GpuQueued, returned.PublicState);
        Assert.Equal(DateTimeOffset.Parse("2026-07-26T10:00:00Z"), returned.DueAtUtc);
        Assert.DoesNotContain(returned.PublicState.ToWireValue(), new[] { "retrying", "blocked", "parked" });
    }

    [Fact]
    public void Capacity_return_rejects_an_already_queued_job()
    {
        var queued = Job.CreateGpuQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Embed, "embed");

        Assert.Throws<DomainInvariantException>(
            () => queued.ReturnForCapacity(DateTimeOffset.Parse("2026-07-26T10:00:00Z")));
    }

    [Fact]
    public void Capacity_return_keeps_a_worker_job_in_the_worker_queue_family()
    {
        var processing = Job
            .CreateQueued(JobId.New(), PipelineRecordId.New(), PipelineStage.Extract, "extract")
            .ClaimWorker("worker", DateTimeOffset.Parse("2026-07-26T09:00:00Z"));

        var returned = processing.ReturnForCapacity(DateTimeOffset.Parse("2026-07-26T10:00:00Z"));

        Assert.Equal(PublicJobState.WorkerQueued, returned.PublicState);
    }
}
