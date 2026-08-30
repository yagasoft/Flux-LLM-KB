using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Integrations.Windows;

/// <summary>Builds the sole allowlisted pure VSS policy; host execution is intentionally unavailable here.</summary>
public static class VssRecoveryPolicy
{
    public static NativeGoLiveVssPolicy CreatePlan(LiveRootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.IsProduction ||
            !string.Equals(layout.Root, LiveRootLayout.CanonicalProductionRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The VSS recovery policy is defined only for the canonical live root.");
        }

        return new NativeGoLiveVssPolicy("I:", 0.10m);
    }
}
