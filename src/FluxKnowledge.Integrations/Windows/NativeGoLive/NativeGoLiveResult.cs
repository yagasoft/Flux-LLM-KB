namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

public sealed record NativeGoLiveResult(bool Succeeded, string? ReasonCode, string? DiagnosticDetail = null)
{
    internal static NativeGoLiveResult Completed() => new(true, null);
    internal static NativeGoLiveResult Refused(string reasonCode, string? diagnosticDetail = null) =>
        new(false, reasonCode, diagnosticDetail);
}
