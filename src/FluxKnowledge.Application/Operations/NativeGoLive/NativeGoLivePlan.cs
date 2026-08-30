using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

namespace FluxKnowledge.Application.Operations;

/// <summary>Immutable, native-only production identities for the separately authorised go-live operation.</summary>
public sealed record NativeGoLivePlan(
    LiveRootLayout Layout,
    NativeGoLiveSqlIdentity Sql,
    NativeGoLiveVssPolicy Vss,
    NativeGoLiveCodexIdentity Codex,
    string CommittedSha,
    string PlanHash)
{
    private static readonly ConditionalWeakTable<NativeGoLivePlan, CanonicalPlanMarker> CanonicalPlans = new();
    public const string NativeProductName = "FluxKnowledge";
    public const string NativeMarketplaceName = "fluxknowledge";
    public const int NativeLoopbackPort = 5137;

    public string IisSiteName => NativeProductName;
    public string AppPoolName => NativeProductName;
    public int LoopbackPort => NativeLoopbackPort;

    public static NativeGoLivePlan CreateProduction(string committedSha)
    {
        if (!IsCanonicalSha(committedSha))
        {
            throw new ArgumentException("The committed SHA must be a lowercase 40-character hexadecimal Git SHA.", nameof(committedSha));
        }

        return CreateCanonical(LiveRootLayout.Production, committedSha);
    }

    internal static NativeGoLivePlan CreateForIsolatedTests(LiveRootLayout layout, string committedSha)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsProduction)
        {
            throw new ArgumentException("An isolated native go-live plan cannot use the production layout.", nameof(layout));
        }
        if (!IsCanonicalSha(committedSha))
        {
            throw new ArgumentException("The committed SHA must be a lowercase 40-character hexadecimal Git SHA.", nameof(committedSha));
        }
        return CreateCanonical(layout, committedSha);
    }

    private static NativeGoLivePlan CreateCanonical(LiveRootLayout layout, string committedSha)
    {
        var sql = new NativeGoLiveSqlIdentity(NativeProductName, layout.SqlDataFilePath, layout.SqlLogFilePath);
        var vss = new NativeGoLiveVssPolicy("I:", 0.10m);
        var codex = new NativeGoLiveCodexIdentity(layout.CodexPluginRoot, NativeMarketplaceName, NativeMarketplaceName);
        var planHash = CalculatePlanHash(layout, sql, vss, codex, committedSha);
        var plan = new NativeGoLivePlan(layout, sql, vss, codex, committedSha, planHash);
        CanonicalPlans.Add(plan, new CanonicalPlanMarker());
        return plan;
    }

    internal void ValidateCanonicalProduction()
    {
        ValidateCanonicalExecution(allowIsolated: false);
    }

    internal void ValidateCanonicalExecution(bool allowIsolated)
    {
        if (!CanonicalPlans.TryGetValue(this, out _) ||
            (!allowIsolated && !Layout.IsProduction) ||
            !IsCanonicalSha(CommittedSha))
        {
            throw new ArgumentException("The native go-live plan was not issued by the canonical plan factory.", nameof(NativeGoLivePlan));
        }

        var expectedSql = new NativeGoLiveSqlIdentity(NativeProductName, Layout.SqlDataFilePath, Layout.SqlLogFilePath);
        var expectedVss = new NativeGoLiveVssPolicy("I:", 0.10m);
        var expectedCodex = new NativeGoLiveCodexIdentity(
            Layout.CodexPluginRoot, NativeMarketplaceName, NativeMarketplaceName);
        var expectedHash = CalculatePlanHash(Layout, expectedSql, expectedVss, expectedCodex, CommittedSha);
        if (Sql != expectedSql ||
            Vss != expectedVss ||
            Codex != expectedCodex ||
            !string.Equals(PlanHash, expectedHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The native go-live plan does not have canonical execution semantics.", nameof(NativeGoLivePlan));
        }
    }

    private static string CalculatePlanHash(
        LiveRootLayout layout,
        NativeGoLiveSqlIdentity sql,
        NativeGoLiveVssPolicy vss,
        NativeGoLiveCodexIdentity codex,
        string committedSha)
    {
        var canonical = string.Join("\n",
            "native-go-live-v1",
            layout.Root,
            sql.CatalogName,
            sql.DataFilePath,
            sql.LogFilePath,
            vss.Volume,
            vss.MaximumStorageFraction.ToString(System.Globalization.CultureInfo.InvariantCulture),
            codex.MarketplaceRoot,
            codex.MarketplaceName,
            codex.PluginName,
            NativeProductName,
            NativeLoopbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            committedSha);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsCanonicalSha(string? value) =>
        value is { Length: 40 } && value.All(character =>
            character is >= '0' and <= '9' || character is >= 'a' and <= 'f');

    private sealed class CanonicalPlanMarker;
}

public sealed record NativeGoLiveSqlIdentity(string CatalogName, string DataFilePath, string LogFilePath);

/// <summary>A pure VSS policy. Production adapters choose an API; this model contains no command or process shape.</summary>
public sealed record NativeGoLiveVssPolicy(string Volume, decimal MaximumStorageFraction);

public sealed record NativeGoLiveCodexIdentity(string MarketplaceRoot, string MarketplaceName, string PluginName);
