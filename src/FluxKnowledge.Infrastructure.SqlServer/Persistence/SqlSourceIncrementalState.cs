using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlSourceIncrementalState
{
    internal static bool RequiresBaseline(SourceRootScanStateEntity state) =>
        state.RecoveryRequired || state.Mode is "BaselineRequired" or "Baselining";

    internal static void InvalidateCoverage(SourceRootScanStateEntity state, string reason, long startingUsn)
    {
        if (state.BaselineEpoch == 0 || !RequiresBaseline(state) || state.RecoveryInProgress || state.Mode == "Baselining")
            state.BaselineEpoch++;
        // Even a coalesced required baseline has a new authority boundary. A saved manifest
        // cannot certify observations spanning it merely because the content epoch stayed put.
        state.RecoveryGeneration++;
        state.RecoveryRequired = true;
        state.RecoveryInProgress = false;
        state.NamespaceComplete = false;
        state.NamespaceGeneration++;
        state.Mode = "BaselineRequired";
        state.Reason = reason;
        state.BaselineStartUsn = startingUsn;
        state.UnresolvedThroughUsn = null;
    }

    internal static void RequirePlan(SourceRootScanStateEntity state, SourceIncrementalPlan plan)
    {
        if (plan.Version != 1 || state.BaselineEpoch != plan.BaselineEpoch || state.RecoveryGeneration != plan.RecoveryGeneration ||
            plan.Cursor is { } cursor && !cursor.VolumeIdentity.Equals(state.VolumeIdentity, StringComparison.OrdinalIgnoreCase))
            throw new SourceSnapshotChangedException("SourceScanEpochChanged");
    }
}
