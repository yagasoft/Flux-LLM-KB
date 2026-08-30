using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Integrations.Codex;

/// <summary>Exact app-owned local marketplace layout; no Codex configuration path is accepted here.</summary>
public sealed record CodexRegistrationPaths(string MarketplaceRoot)
{
    public const string MarketplaceName = "fluxknowledge";
    public const string PluginName = "fluxknowledge";

    public static string ProductionMarketplaceRoot => LiveRootLayout.Production.CodexPluginRoot;

    public static CodexRegistrationPaths Production { get; } = new(ProductionMarketplaceRoot);

    public string PluginRoot => Path.Combine(MarketplaceRoot, "plugins", PluginName);

    public CodexMarketplaceIdentity Identity => new(MarketplaceRoot, MarketplaceName, PluginName);

    internal NativeGoLiveCodexIdentity NativeGoLiveIdentity =>
        new(MarketplaceRoot, MarketplaceName, PluginName);

    internal static CodexRegistrationPaths FromNativeGoLive(NativeGoLiveCodexIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.MarketplaceName, MarketplaceName, StringComparison.Ordinal) ||
            !string.Equals(identity.PluginName, PluginName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The native go-live Codex identity is foreign.", nameof(identity));
        }

        return CreateForIsolatedTests(identity.MarketplaceRoot);
    }

    public static CodexRegistrationPaths CreateForIsolatedTests(string marketplaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplaceRoot);
        if (!Path.IsPathFullyQualified(marketplaceRoot) ||
            marketplaceRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
            marketplaceRoot.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The Codex marketplace root must be an absolute local path.", nameof(marketplaceRoot));
        }

        return new CodexRegistrationPaths(Path.TrimEndingDirectorySeparator(Path.GetFullPath(marketplaceRoot)));
    }
}
