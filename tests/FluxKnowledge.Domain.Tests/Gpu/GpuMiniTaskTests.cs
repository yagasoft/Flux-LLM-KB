using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Gpu;

public sealed class GpuMiniTaskTests
{
    [Fact]
    public void Priority_lane_is_part_of_the_durable_contract_without_activating_gpu_work()
    {
        var task = GpuMiniTask.Create(
            JobId.New(), 4, GpuPriorityLane.DocumentIndexing,
            "future-model-key", "future-fingerprint", 256, 16_384, "idempotency");

        Assert.Equal(GpuPriorityLane.DocumentIndexing, task.PriorityLane);
        Assert.Equal(PublicJobState.GpuQueued, task.InitialParentJobState);
    }
}
