namespace FluxKnowledge.Application.Operations;

/// <summary>A diagnostic-only legacy plan. Native production execution is defined by <see cref="NativeGoLivePlan"/>.</summary>
public sealed class FreshStartPlan
{
    public const string RequiredMode = "fresh-start";
    public const string CatalogName = "FluxKnowledge";
    public const string MarketplaceName = "fluxknowledge";
    public const string PluginName = "fluxknowledge";
    public static readonly TimeSpan DefaultAuthorityLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumAuthorityLifetime = TimeSpan.FromMinutes(5);

    private FreshStartPlan(
        string mode,
        LiveRootLayout layout,
        bool isDisposableSimulation,
        TimeSpan authorityLifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(layout);
        if (authorityLifetime <= TimeSpan.Zero || authorityLifetime > MaximumAuthorityLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(authorityLifetime));
        }

        Mode = mode;
        Layout = layout;
        IsDisposableSimulation = isDisposableSimulation;
        AuthorityLifetime = authorityLifetime;
        DatabaseIdentity = new(CatalogName, layout.SqlDataFilePath, layout.SqlLogFilePath);
        PluginIdentity = new(layout.CodexPluginRoot, MarketplaceName, PluginName);
        Volume = Path.GetPathRoot(layout.Root)?.TrimEnd(Path.DirectorySeparatorChar)
            ?? throw new ArgumentException("The fresh-start root has no volume.", nameof(layout));
        ResetRoots =
        [
            layout.IndexRoot,
            layout.RetainedRoot,
            layout.SpoolRoot,
            layout.TempRoot,
            layout.LogsRoot,
            layout.CodexPluginRoot
        ];
    }

    public string Mode { get; }
    public LiveRootLayout Layout { get; }
    public bool IsDisposableSimulation { get; }
    public bool ExecutionAvailable => false;
    public TimeSpan AuthorityLifetime { get; }
    public FreshStartDatabaseIdentity DatabaseIdentity { get; }
    public FreshStartPluginIdentity PluginIdentity { get; }
    public string Volume { get; }
    public IReadOnlyList<string> ResetRoots { get; }

    public static FreshStartPlan CreateProduction(string mode) =>
        new(mode, LiveRootLayout.Production, false, DefaultAuthorityLifetime);

    internal static FreshStartPlan CreateForDisposableSimulation(
        string mode,
        LiveRootLayout layout,
        TimeSpan? authorityLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsProduction)
        {
            throw new ArgumentException("A disposable simulation cannot target the production root.", nameof(layout));
        }

        return new(mode, layout, true, authorityLifetime ?? DefaultAuthorityLifetime);
    }
}

public sealed record FreshStartDatabaseIdentity(string CatalogName, string DataFilePath, string LogFilePath);

public sealed record FreshStartPluginIdentity(string MarketplaceRoot, string MarketplaceName, string PluginName);
