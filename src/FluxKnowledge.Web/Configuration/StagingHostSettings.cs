using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Web.Configuration;

/// <summary>Explicit settings for the separate, model-free functional staging executable.</summary>
internal sealed class StagingHostSettings
{
    internal const string BaseRoot = @"E:\Codex Workspaces\Staging\FluxKnowledge";
    internal const string WorkingBaseRoot = @"I:\Codex Workspaces\Staging\FluxKnowledge";
    private StagingHostSettings(LiveRootLayout layout, StagingSqlBinding sql, Uri origin, string indexPublicationMode, bool dashboardReadBenchmark,
        string dashboardReadConsistency)
    {
        Layout = layout;
        Sql = sql;
        Origin = origin;
        IndexPublicationMode = indexPublicationMode;
        DashboardReadBenchmark = dashboardReadBenchmark;
        DashboardReadConsistency = dashboardReadConsistency;
    }

    internal LiveRootLayout Layout { get; }
    internal StagingSqlBinding Sql { get; }
    internal Uri Origin { get; }
    internal string SourcesRoot => Path.Combine(Layout.Root, "Sources");
    internal string CatalogName => Sql.CatalogName;
    internal string RuntimeConnectionString => Sql.Options.ConnectionString;
    internal string IndexPublicationMode { get; }
    internal bool DashboardReadBenchmark { get; }
    internal string DashboardReadConsistency { get; }
    internal bool SourceJournalEnabled { get; private init; }
    internal IReadOnlyList<string> SourceJournalVolumes { get; private init; } = [];
    internal bool SourceJournalSealingEnabled { get; private init; }
    internal IReadOnlyList<SourceJournalSealQualification> SourceJournalSealQualifications { get; private init; } = [];
    internal int SourceJournalMetadataEntriesPerSecond { get; private init; } = 64;

    internal static StagingHostSettings Create(string root, string connection, string origin,
        ILiveRootPathInspector? inspector = null, string indexPublicationMode = "Legacy", bool dashboardReadBenchmark = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (indexPublicationMode is not ("Legacy" or "Segmented")) throw new ArgumentException("Unknown staging index publication mode.");
        if (!Path.IsPathFullyQualified(root) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)), BaseRoot, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)), WorkingBaseRoot, StringComparison.OrdinalIgnoreCase)) ||
            !Regex.IsMatch(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), @"\A[A-Za-z0-9_-]{1,64}\z"))
            throw new ArgumentException("Staging must use one unambiguous owned directory beneath the staging base.");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.Port < 1024 || uri.Port == 5137 ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Staging must use an explicit http://127.0.0.1 origin on a separate port.");
        var layout = LiveRootLayout.CreateForIsolatedTests(root);
        var pathInspector = inspector ?? FileSystemLiveRootPathInspector.Instance;
        foreach (var path in layout.AppOwnedLocations.Append(Path.Combine(root, "Sources")))
            if (!layout.ValidateOwnedPath(path, pathInspector).IsValid)
                throw new ArgumentException("Staging path is linked, escaped or ambiguous.");
        return new(layout, new StagingSqlBinding(layout, connection), uri, indexPublicationMode, dashboardReadBenchmark, "LegacyFenced");
    }

    internal static StagingHostSettings Load(string file)
    {
        var full = Path.GetFullPath(file);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(full))!;
        if (!string.Equals(full, Path.Combine(root, "Config", "staging.json"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The staging settings file must be Config\\staging.json under its owned root.");
        // Validate the root and settings path before opening any configuration bytes.
        var layout = Create(root, "Server=localhost;Database=FluxKnowledge_Staging_Validate;User ID=validate;Password=not-used", "http://127.0.0.1:5140").Layout;
        new LiveRootStorageSafety(layout, FileSystemLiveRootPathInspector.Instance).ValidateBeforeIo(full);
        var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(full), new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ArgumentException("Staging settings are empty.");
        var loaded = Create(root, document.SqlConnectionString, document.Origin, indexPublicationMode: document.IndexPublicationMode,
            dashboardReadBenchmark: document.DashboardReadBenchmark);
        if (document.DashboardReadConsistency is not ("LegacyFenced" or "Snapshot"))
            throw new ArgumentException("Invalid staging dashboard read consistency.");
        var volumes = document.SourceJournalVolumes ?? [];
        if (volumes.Any(volume => volume is null || !volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Staging journal targets must be explicit canonical volume identities.");
        try { volumes = volumes.Select(WindowsSourceChangeJournalReader.CanonicalVolume).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException)
        { throw new ArgumentException("Staging journal targets must be explicit canonical volume identities.", error); }
        if (document.SourceJournalEnabled && volumes.Length == 0)
            throw new ArgumentException("Incremental staging requires explicit journal volumes.");
        return new(loaded.Layout, loaded.Sql, loaded.Origin, loaded.IndexPublicationMode, loaded.DashboardReadBenchmark,
            document.DashboardReadConsistency)
        {
            SourceJournalEnabled = document.SourceJournalEnabled, SourceJournalVolumes = volumes,
            SourceJournalSealingEnabled = document.SourceJournalSealingEnabled,
            SourceJournalSealQualifications = document.SourceJournalSealQualifications ?? [],
            SourceJournalMetadataEntriesPerSecond = document.SourceJournalMetadataEntriesPerSecond
        };
    }

    internal void ValidateBeforeStart()
    {
        var safety = new LiveRootStorageSafety(Layout, FileSystemLiveRootPathInspector.Instance);
        foreach (var path in Layout.AppOwnedLocations.Append(SourcesRoot)
            .Append(Path.Combine(Layout.ConfigRoot, "data-protection"))
            .Append(Path.Combine(Layout.ConfigRoot, "browser-data-protection"))) safety.ValidateBeforeIo(path);
    }

    internal async Task VerifyDatabaseBindingAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(Sql.Options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT DB_NAME(), IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator'),
                HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER'), HAS_DBACCESS('FluxKnowledge');
            SELECT type_desc, physical_name FROM sys.database_files;
            """, connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != Sql.CatalogName ||
            Enumerable.Range(1, 4).Any(i => reader.IsDBNull(i) || reader.GetInt32(i) != 0))
            throw new InvalidOperationException("Staging requires a restricted login without production database access or server administration.");
        if (!await reader.NextResultAsync(ct)) throw new InvalidOperationException("Staging SQL file state is absent.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) files.Add(reader.GetString(0), reader.GetString(1));
        if (files.Count != 2 || !files.TryGetValue("ROWS", out var data) || !files.TryGetValue("LOG", out var log) ||
            !string.Equals(data, Sql.Options.DataFilePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(log, Sql.Options.LogFilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Staging SQL files do not match the exact owned layout.");
    }

    internal IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"] = Layout.IndexRoot,
        ["SourceArtifactStore:Root"] = Layout.RetainedRoot,
        [PrivatePcDataProtectionProviderFactory.LocalApplicationDataRootConfigurationKey] = Layout.ConfigRoot,
        ["LocalIngress:AllowedRoots:0"] = SourcesRoot,
        ["LocalOperator:CanonicalOrigin"] = Origin.GetLeftPart(UriPartial.Authority),
        ["Outlook:AllowedSpoolRoots:0"] = Layout.SpoolRoot,
        ["OutlookCaptureRecovery:Enabled"] = "false",
        ["Search:HybridPassagesEnabled"] = "false",
        ["IndexPublication:Mode"] = IndexPublicationMode,
        ["Worker:Enabled"] = DashboardReadBenchmark ? "false" : "true",
        ["SourceJournal:Enabled"] = SourceJournalEnabled ? "true" : "false",
        ["SourceJournal:PollSeconds"] = "2",
        ["SourceJournal:SealingEnabled"] = SourceJournalSealingEnabled ? "true" : "false",
        ["SourceJournal:MetadataEntriesPerSecond"] = SourceJournalMetadataEntriesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Dashboard:ReadConsistency"] = DashboardReadConsistency,
        ["Codex:PromptContextEnabled"] = "false",
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:FluxKnowledge.Web.Components.Status.OverviewSnapshotCoordinator"] = DashboardReadBenchmark ? "Information" : "Warning",
        ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Information",
        ["Logging:LogLevel:FluxKnowledge.Infrastructure.SqlServer.Persistence.SqlDatabaseStartupReadiness"] = "Information",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
    }.Concat(SourceJournalVolumes.Select((volume, index) =>
        new KeyValuePair<string, string?>($"SourceJournal:ApprovedVolumes:{index}", volume)))
        .Concat(SourceJournalSealQualifications.SelectMany((qualification, index) => new Dictionary<string, string?>
        {
            [$"SourceJournal:SealQualifications:{index}:RootIdentityFingerprint"] = qualification.RootIdentityFingerprint,
            [$"SourceJournal:SealQualifications:{index}:EnvironmentFingerprint"] = qualification.EnvironmentFingerprint,
            [$"SourceJournal:SealQualifications:{index}:Scope"] = qualification.Scope,
            [$"SourceJournal:SealQualifications:{index}:QualificationId"] = qualification.QualificationId,
            [$"SourceJournal:SealQualifications:{index}:ProviderName"] = qualification.ProviderName,
            [$"SourceJournal:SealQualifications:{index}:ProviderVersion"] = qualification.ProviderVersion
        }))).Build();

    private sealed record SettingsDocument(string SqlConnectionString, string Origin, string IndexPublicationMode = "Legacy",
        bool DashboardReadBenchmark = false, string DashboardReadConsistency = "LegacyFenced",
        bool SourceJournalEnabled = false, string[]? SourceJournalVolumes = null,
        bool SourceJournalSealingEnabled = false, SourceJournalSealQualification[]? SourceJournalSealQualifications = null,
        int SourceJournalMetadataEntriesPerSecond = 64);
}

internal sealed class StagingSourceRootPathPolicy(ISourceRootPathPolicy inner, string sourcesRoot) : ISourceRootPathPolicy
{
    public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.FullPath));
        if (!path.StartsWith(sourcesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Staging sources must be beneath its owned Sources directory.");
        return inner.ValidateAndCanonicalise(request);
    }
}
