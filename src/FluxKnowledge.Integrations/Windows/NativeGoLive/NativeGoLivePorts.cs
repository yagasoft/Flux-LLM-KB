using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

/// <summary>Explicit, typed acknowledgements for the separately authorised native clean-slate operation.</summary>
public sealed record NativeGoLiveRequest(
    NativeGoLivePlan Plan,
    bool IsPlanOnly,
    bool ConfirmCleanSlate,
    bool ConfirmConfigureVss,
    bool ConfirmDestroySql,
    bool ConfirmRegisterCodex,
    string? MergedMainRoot = null,
    string? MergedMainPayloadSha256 = null,
    NativeGoLivePayloadManifest? MergedMainPayloadManifest = null)
{
    public static NativeGoLiveRequest PlanOnly(NativeGoLivePlan plan) =>
        new(plan, true, false, false, false, false);
}

/// <summary>Host boundary for the native go-live state machine. This assembly supplies no live implementation.</summary>
public interface INativeGoLiveHost
{
    ValueTask<INativeGoLiveLease> AcquireLeaseAsync(NativeGoLiveRequest request, CancellationToken cancellationToken);
    /// <summary>
    /// Admits only an absent root/catalogue, or observes and wipes all existing deployment state
    /// under this already-confirmed invocation.
    /// </summary>
    ValueTask AdmitAndWipeAsync(NativeGoLiveRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NativeGoLiveContractException("go-live-one-shot-admission-not-supported"));
    /// <summary>Replaces app-owned host prerequisites after all confirmations and before admission can wipe root or SQL state.</summary>
    ValueTask PrepareHostPrerequisitesAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NativeGoLiveContractException("go-live-host-prerequisites-not-supported"));
    /// <summary>One-shot preflight after admission has proved the root and catalogue absent.</summary>
    ValueTask VerifyOneShotPreflightAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NativeGoLiveContractException("go-live-one-shot-preflight-not-supported"));
    ValueTask<bool> StopPoolAsync(CancellationToken cancellationToken);
    ValueTask RestorePoolAsync(CancellationToken cancellationToken);
    ValueTask ConfigureVssAsync(NativeGoLiveVssPolicy policy, CancellationToken cancellationToken);
    /// <summary>Creates the fixed empty hierarchy after one-shot admission removed the old root.</summary>
    ValueTask CreateEmptyRootAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NativeGoLiveContractException("go-live-one-shot-root-creation-not-supported"));
    ValueTask ProvisionEmptyCatalogueAsync(NativeGoLiveSqlIdentity sql, CancellationToken cancellationToken);
    ValueTask PublishAndStartAsync(NativeGoLivePlan plan, CancellationToken cancellationToken);
    ValueTask ValidateAsync(NativeGoLivePlan plan, CancellationToken cancellationToken);
    ValueTask RegisterMarketplaceAsync(NativeGoLiveCodexIdentity codex, CancellationToken cancellationToken);
}

public interface INativeGoLiveLease : IAsyncDisposable { }

/// <summary>Closed live-validation contract that must succeed before marketplace registration.</summary>
public static class NativeGoLiveLoopbackContract
{
    public const string BaseUri = "http://127.0.0.1:5137";
    public const string NativeProofHeader = "X-FluxKnowledge-Native-Proof";
    public const string NativeProofValue = "native-go-live-v1";
    public static IReadOnlyList<string> RequiredMcpTools { get; } = Array.AsReadOnly(
    [
        "knowledge.search",
        "knowledge.write",
        "knowledge.graph",
        "code.query",
        "code.write",
        "corpus.query",
        "corpus.write",
        "operations.status",
        "operations.audit"
    ]);
}

/// <summary>Raised by a host when another executor already holds the stable go-live lease.</summary>
public sealed class NativeGoLiveLeaseUnavailableException : Exception
{
    public NativeGoLiveLeaseUnavailableException() : base("The native go-live lease is unavailable.") { }
}

/// <summary>
/// Carries the last proved pool state when IIS changed the pool but its final observation failed.
/// The executor uses this evidence to restore a pool that was running before the attempted stop.
/// </summary>
public sealed class NativeGoLivePoolStopException : Exception
{
    public NativeGoLivePoolStopException(bool wasRunning, string reason)
        : base(reason)
    {
        WasRunning = wasRunning;
    }

    public bool WasRunning { get; }
}
