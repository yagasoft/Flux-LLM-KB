using FluxKnowledge.Application.Gpu;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Gpu;

public sealed class GpuModelResidencyContractTests
{
    [Fact]
    public void A_reused_helper_is_bound_to_the_resident_generation_not_a_request_identity()
    {
        var generation = Guid.NewGuid();
        var owner = new GpuInteractiveOwnerIdentity(1, DateTimeOffset.UtcNow, new string('a', 64));
        var helper = new GpuResidentHelperIdentity(generation, 2, DateTimeOffset.UtcNow);
        var fence = new GpuResidentModelFence(generation, "gpu:0", 1, owner, helper);
        fence.Validate();
        Assert.Throws<ArgumentException>(() => (fence with { Version = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (fence with { Helper = helper with { JobId = Guid.NewGuid() } }).Validate());
    }

    [Fact]
    public void The_model_key_includes_the_actual_native_phase_and_exact_settings()
    {
        var model = new GpuResidentModelKey("retrieval", "settings-fp32", "bge-embedding");
        model.Validate();
        Assert.NotEqual(model, model with { SettingsFingerprint = "settings-fp16" });
        Assert.NotEqual(model, model with { ModelKey = "bge-reranker" });
        Assert.Throws<ArgumentException>(() => (model with { ModelKey = "bge-embedding " }).Validate());
    }
}
