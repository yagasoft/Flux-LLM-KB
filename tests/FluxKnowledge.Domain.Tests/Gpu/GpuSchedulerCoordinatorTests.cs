using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Gpu;

public sealed class GpuSchedulerCoordinatorTests
{
    [Fact]
    public async Task Default_no_gpu_gate_returns_busy_without_a_reservation()
    {
        var decision = await new NoGpuAdmissionGate().DecideAsync(
            new GpuBatchCandidate(GpuPriorityLane.InteractiveRetrieval, "runtime", "settings", 1, 1),
            CancellationToken.None);

        Assert.Equal(GpuAdmissionDisposition.Busy, decision.Disposition);
        Assert.Null(decision.CapacitySlotKey);
        Assert.Null(decision.OwnerKey);
        Assert.Null(decision.RetryAfter);
    }
}
