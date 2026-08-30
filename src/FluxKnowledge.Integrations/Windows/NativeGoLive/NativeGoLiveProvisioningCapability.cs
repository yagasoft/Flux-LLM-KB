namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

/// <summary>
/// Opaque capability constructed only by the private native go-live host after one-shot closeout
/// capability consumption.
/// </summary>
public sealed class NativeGoLiveProvisioningCapability
{
    internal NativeGoLiveProvisioningCapability(NativeGoLiveCloseoutCapability closeout)
    {
        ArgumentNullException.ThrowIfNull(closeout);
        if (!closeout.IsConsumedForExecution)
        {
            throw new InvalidOperationException("go-live-closeout-capability-not-consumed");
        }

        _closeout = closeout;
    }

    private NativeGoLiveProvisioningCapability(bool isolatedClaimed) => _isolatedClaimed = isolatedClaimed;

    private readonly bool _isolatedClaimed;
    private readonly NativeGoLiveCloseoutCapability? _closeout;

    internal static NativeGoLiveProvisioningCapability CreateForIsolatedTests(bool claimed = true) => new(claimed);

    /// <summary>
    /// Verifies that the one-shot closeout capability still permits the provisioner construction.
    /// </summary>
    public void EnsureClaimed()
    {
        if (_closeout is not null && _closeout.IsConsumedForExecution)
        {
            return;
        }
        if (!_isolatedClaimed)
        {
            throw new InvalidOperationException("go-live-closeout-capability-not-consumed");
        }
    }
}
