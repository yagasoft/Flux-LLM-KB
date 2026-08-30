namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

public sealed record NativeGoLiveResult(bool Succeeded, string? ReasonCode)
{
    internal static NativeGoLiveResult Completed() => new(true, null);
    internal static NativeGoLiveResult Refused(string reasonCode) => new(false, reasonCode);
}
