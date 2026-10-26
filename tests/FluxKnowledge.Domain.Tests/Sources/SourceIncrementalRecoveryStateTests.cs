using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceIncrementalRecoveryStateTests
{
    [Fact]
    public void Required_recovery_coalesces_content_epoch_but_updates_and_fences_its_boundary()
    {
        var state = new SourceRootScanStateEntity { BaselineEpoch = 4, RecoveryGeneration = 7, RecoveryRequired = true, Mode = "BaselineRequired", BaselineStartUsn = 100 };
        var prior = new SourceIncrementalPlan(Guid.NewGuid(), 4, true, "Baselining", null, 0, []) { RecoveryGeneration = 7 };
        SqlSourceIncrementalState.InvalidateCoverage(state, "journal-continuity-lost", 200);
        Assert.Equal(4, state.BaselineEpoch);
        Assert.Equal(8, state.RecoveryGeneration);
        Assert.Equal(200, state.BaselineStartUsn);
        Assert.True(state.RecoveryRequired);
        Assert.False(state.NamespaceComplete);
        Assert.Throws<SourceSnapshotChangedException>(() => SqlSourceIncrementalState.RequirePlan(state, prior));
    }

    [Theory]
    [InlineData("Baselining")]
    [InlineData("Fallback")]
    public void New_authority_loss_fences_a_running_baseline_in_either_availability_mode(string mode)
    {
        var state = new SourceRootScanStateEntity { BaselineEpoch = 4, RecoveryGeneration = 7, RecoveryRequired = true, RecoveryInProgress = true, Mode = mode };
        SqlSourceIncrementalState.InvalidateCoverage(state, "ancestor-authority-changed", 300);
        Assert.Equal(5, state.BaselineEpoch);
        Assert.Equal(8, state.RecoveryGeneration);
        Assert.True(state.RecoveryRequired);
        Assert.False(state.RecoveryInProgress);
    }

    [Fact]
    public void Frozen_manifest_requires_a_version_and_cannot_use_another_volumes_barrier()
    {
        var plan = new SourceIncrementalPlan(Guid.NewGuid(), 1, false, "Journal", new("other-volume", 1, 100), 0, []);
        var state = new SourceRootScanStateEntity { BaselineEpoch = 1, VolumeIdentity = "admitted-volume" };
        Assert.Throws<SourceSnapshotChangedException>(() => SqlSourceIncrementalState.RequirePlan(state, plan));
        var json = JsonSerializer.SerializeToNode(plan)!.AsObject();
        json.Remove("Version");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SourceIncrementalPlan>(json.ToJsonString()));
    }
}
