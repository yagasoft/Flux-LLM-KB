using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SharedGpuAdmissionGateTests
{
    private static readonly GpuWorkloadPolicy Policy = new([
        new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
        new("synthetic-retrieval", "fixed-search-settings", GpuWorkloadKind.Retrieval)]);

    [Fact]
    public async Task Ocr_and_both_retrieval_lanes_use_the_existing_physical_slot()
    {
        var gate = new SharedGpuAdmissionGate(Policy, "retrieval-background", 12L * 1024 * 1024 * 1024);
        var owner = Guid.NewGuid();
        var interactive = new GpuBatchCandidate(GpuPriorityLane.InteractiveRetrieval, "synthetic-retrieval", "fixed-search-settings", 1, 1024, $"retrieval-gpu:{owner:N}");
        var foreground = await gate.DecideAsync(interactive, CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, foreground.Disposition);
        Assert.Equal(interactive.RequiredExecutorKey, foreground.ExecutorKey);
        Assert.Equal(PaddleOcrVlmRuntimeContract.CapacitySlotKey, foreground.CapacitySlotKey);
        var background = await gate.DecideAsync(interactive with { PriorityLane = GpuPriorityLane.DocumentIndexing, RequiredExecutorKey = null, ItemCount = 4 }, CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, background.Disposition);
        Assert.Equal("retrieval-background", background.ExecutorKey);
        Assert.Equal(foreground.CapacitySlotKey, background.CapacitySlotKey);
        var ocr = await gate.DecideAsync(new(GpuPriorityLane.DocumentIndexing, PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
            PaddleOcrVlmRuntimeContract.SettingsFingerprint, 1, PaddleOcrVlmRuntimeContract.EstimatedDocumentBytes), CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, ocr.Disposition);
        Assert.Equal(PaddleOcrVlmRuntimeContract.ExecutorKey, ocr.ExecutorKey);
        Assert.Equal(foreground.CapacitySlotKey, ocr.CapacitySlotKey);
        Assert.Equal(foreground.OwnerKey, ocr.OwnerKey);
    }

    [Fact]
    public async Task Unknown_profiles_wrong_owner_family_and_oversized_batches_fail_closed()
    {
        var gate = new SharedGpuAdmissionGate(Policy, "retrieval-background", 4096);
        var valid = new GpuBatchCandidate(GpuPriorityLane.InteractiveRetrieval, "synthetic-retrieval", "fixed-search-settings", 1, 1024, $"retrieval-gpu:{Guid.NewGuid():N}");
        var invalid = new[] {
            valid with { SettingsFingerprint = "unknown" }, valid with { ModelRuntimeKey = "unknown" },
            valid with { RequiredExecutorKey = null }, valid with { RequiredExecutorKey = "retrieval-gpu:not-an-instance" },
            valid with { RequiredExecutorKey = $"retrieval-gpu:{Guid.Empty:N}" }, valid with { ItemCount = 2 },
            valid with { EstimatedBytes = 4097 }, valid with { EstimatedBytes = 0 },
            valid with { PriorityLane = GpuPriorityLane.DocumentIndexing },
            valid with { PriorityLane = GpuPriorityLane.DocumentIndexing, RequiredExecutorKey = null, ItemCount = 5 },
            valid with { ModelRuntimeKey = PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
                SettingsFingerprint = PaddleOcrVlmRuntimeContract.SettingsFingerprint } };
        foreach (var candidate in invalid)
            Assert.Equal(GpuAdmissionDisposition.Busy, (await gate.DecideAsync(candidate, CancellationToken.None)).Disposition);
    }
}
