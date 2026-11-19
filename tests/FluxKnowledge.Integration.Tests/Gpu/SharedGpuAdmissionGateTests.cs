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
    public async Task Media_transcription_requires_the_exact_opted_in_runtime_and_one_background_item()
    {
        var runtime = new MediaTranscriptionGpuRuntime("local-asr-bundle-v1", "language-routing-v1",
            "owned-media-transcription", 3072);
        var gate = new SharedGpuAdmissionGate(new GpuWorkloadPolicy([.. Policy.Profiles, runtime.WorkloadProfile]),
            "retrieval-background", 4096, runtime);
        var candidate = new GpuBatchCandidate(GpuPriorityLane.VideoOrUnknown, runtime.ModelRuntimeKey,
            runtime.SettingsFingerprint, 1, runtime.EstimatedBytes);
        var admitted = await gate.DecideAsync(candidate, CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, admitted.Disposition);
        Assert.Equal("owned-media-transcription", admitted.ExecutorKey);
        Assert.Equal(PaddleOcrVlmRuntimeContract.CapacitySlotKey, admitted.CapacitySlotKey);
        Assert.Equal(PaddleOcrVlmRuntimeContract.CapacityOwnerKey, admitted.OwnerKey);

        var disabled = new SharedGpuAdmissionGate(Policy, "retrieval-background", 4096);
        Assert.Equal(GpuAdmissionDisposition.Busy, (await disabled.DecideAsync(candidate, CancellationToken.None)).Disposition);
        var invalid = new[]
        {
            candidate with { ModelRuntimeKey = "different-model" },
            candidate with { SettingsFingerprint = "different-routing" },
            candidate with { PriorityLane = GpuPriorityLane.InteractiveRetrieval },
            candidate with { PriorityLane = GpuPriorityLane.DocumentIndexing },
            candidate with { ItemCount = 2 },
            candidate with { EstimatedBytes = 1 },
            candidate with { EstimatedBytes = 3073 },
            candidate with { RequiredExecutorKey = runtime.ExecutorKey }
        };
        foreach (var value in invalid)
            Assert.Equal(GpuAdmissionDisposition.Busy, (await gate.DecideAsync(value, CancellationToken.None)).Disposition);
    }

    [Fact]
    public void Media_transcription_configuration_refuses_inconsistent_or_colliding_ownership()
    {
        var runtime = new MediaTranscriptionGpuRuntime("local-asr-bundle-v1", "language-routing-v1",
            "owned-media-transcription", 3072);
        var policy = new GpuWorkloadPolicy([.. Policy.Profiles, runtime.WorkloadProfile]);
        Assert.Throws<ArgumentException>(() => new SharedGpuAdmissionGate(policy, "retrieval-background", 4096));
        Assert.Throws<ArgumentException>(() => new SharedGpuAdmissionGate(Policy, "retrieval-background", 4096, runtime));
        var otherRuntime = new MediaTranscriptionGpuRuntime(runtime.ModelRuntimeKey, "wrong-settings", runtime.ExecutorKey, runtime.EstimatedBytes);
        Assert.Throws<ArgumentException>(() => new SharedGpuAdmissionGate(policy, "retrieval-background", 4096, otherRuntime));
        foreach (var reserved in new[] { PaddleOcrVlmRuntimeContract.ExecutorKey, "retrieval-background", $"retrieval-gpu:{Guid.NewGuid():N}" })
        {
            var collision = new MediaTranscriptionGpuRuntime(runtime.ModelRuntimeKey, runtime.SettingsFingerprint, reserved, runtime.EstimatedBytes);
            Assert.Throws<ArgumentException>(() => new SharedGpuAdmissionGate(policy, "retrieval-background", 4096, collision));
        }
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
