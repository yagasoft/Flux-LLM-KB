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
            "future-model-key", "future-fingerprint", 256, "idempotency");

        Assert.Equal(GpuPriorityLane.DocumentIndexing, task.PriorityLane);
        Assert.Equal(PublicJobState.GpuQueued, task.InitialParentJobState);
        Assert.Equal(GpuMiniTaskExecutionState.Ready, task.ExecutionState);
        Assert.Null(task.DeferredUntilUtc);
        Assert.Null(task.BatchId);
        Assert.Equal(0, task.AdmissionGeneration);
    }

    [Theory]
    [InlineData(GpuPriorityLane.InteractiveRetrieval, 0)]
    [InlineData(GpuPriorityLane.DocumentIndexing, 1)]
    [InlineData(GpuPriorityLane.ImageOcr, 2)]
    [InlineData(GpuPriorityLane.ImageEnrichment, 3)]
    [InlineData(GpuPriorityLane.VideoOrUnknown, 4)]
    public void Priority_lanes_have_the_approved_numeric_order(
        GpuPriorityLane lane,
        int expectedValue)
    {
        Assert.Equal(expectedValue, (int)lane);
    }

    [Theory]
    [InlineData("", "settings", "idempotency")]
    [InlineData("runtime", "", "idempotency")]
    [InlineData("runtime", "settings", "")]
    [InlineData(" ", "settings", "idempotency")]
    public void Create_rejects_blank_compatibility_or_idempotency_values(
        string modelRuntimeKey,
        string settingsFingerprint,
        string idempotencyKey)
    {
        Assert.ThrowsAny<ArgumentException>(() => GpuMiniTask.Create(
            JobId.New(), 1, GpuPriorityLane.InteractiveRetrieval,
            modelRuntimeKey, settingsFingerprint, 1, idempotencyKey));
    }

    [Theory]
    [InlineData("runtime ", "settings", "idempotency")]
    [InlineData("runtime", "settings ", "idempotency")]
    [InlineData("runtime", "settings", "idempotency ")]
    public void Create_rejects_terminal_whitespace_in_opaque_scheduler_keys(
        string modelRuntimeKey,
        string settingsFingerprint,
        string idempotencyKey)
    {
        Assert.ThrowsAny<ArgumentException>(() => GpuMiniTask.Create(
            JobId.New(), 1, GpuPriorityLane.InteractiveRetrieval,
            modelRuntimeKey, settingsFingerprint, 1, idempotencyKey));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Create_rejects_invalid_numeric_values(long sourceRevision, long estimatedBytes)
    {
        Assert.Throws<DomainInvariantException>(() => GpuMiniTask.Create(
            JobId.New(), sourceRevision, GpuPriorityLane.InteractiveRetrieval,
            "runtime", "settings", estimatedBytes, "idempotency"));
    }
}
