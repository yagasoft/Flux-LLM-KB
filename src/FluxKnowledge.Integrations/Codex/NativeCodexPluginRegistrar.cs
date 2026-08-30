namespace FluxKnowledge.Integrations.Codex;

/// <summary>Read-only status surface available to normal Web, MCP and CLI composition.</summary>
public sealed class NativeCodexPluginRegistrar
{
    private readonly CodexRegistrationPaths _paths;
    private readonly NativeCodexPluginManifestWriter _writer;
    private readonly ICodexMarketplaceLifecycle _lifecycle;

    private NativeCodexPluginRegistrar(
        CodexRegistrationPaths paths,
        NativeCodexPluginManifestWriter writer,
        ICodexMarketplaceLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _paths = paths;
        _writer = writer;
        _lifecycle = lifecycle;
    }

    /// <summary>Creates the inert registrar available to normal status/CLI composition.</summary>
    public static NativeCodexPluginRegistrar CreateStatusOnly(
        CodexRegistrationPaths paths,
        NativeCodexPluginManifestWriter writer) =>
        new(paths, writer, UnavailableCodexMarketplaceLifecycle.Instance);

    public async Task<NativeCodexPluginStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var material = await _writer.ValidateAsync(_paths.MarketplaceRoot, cancellationToken).ConfigureAwait(false);
        var lifecycleStatus = await _lifecycle.ObserveAsync(_paths.Identity, cancellationToken).ConfigureAwait(false);
        if (lifecycleStatus.State == CodexMarketplaceLifecycleState.Foreign) return new(NativeCodexPluginHealth.Drift, "foreign-registration");
        if (lifecycleStatus.State == CodexMarketplaceLifecycleState.Unavailable) return new(NativeCodexPluginHealth.Drift, "lifecycle-unavailable");
        if (!material.IsValid && lifecycleStatus.State == CodexMarketplaceLifecycleState.Missing) return new(NativeCodexPluginHealth.Missing, material.Reason);
        if (!material.IsValid || lifecycleStatus.State != CodexMarketplaceLifecycleState.Registered) return new(NativeCodexPluginHealth.Drift, "registration-drift");
        return new(NativeCodexPluginHealth.Healthy, null);
    }

    /// <summary>Normal composition cannot turn this read-only registrar into a mutation path.</summary>
    public async Task<NativeCodexPluginRepair> RepairAsync(CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        return new(false, status, "go-live-authority-required");
    }
}

public enum NativeCodexPluginHealth { Healthy, Missing, Drift }

public sealed record NativeCodexPluginStatus(NativeCodexPluginHealth Health, string? Reason);

public sealed record NativeCodexPluginRepair(bool Changed, NativeCodexPluginStatus Status, string? Reason);
