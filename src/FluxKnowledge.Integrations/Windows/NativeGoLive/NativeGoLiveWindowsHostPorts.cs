using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

#pragma warning disable CA1416 // This file is the explicit Windows-only production adapter boundary.

internal static class NativeGoLiveWindowsHostPorts
{
    /// <summary>
    /// Creates only immutable production composition. It deliberately has no closeout capability
    /// or bootstrap connection: both are bound only after the one-shot host has accepted the request.
    /// </summary>
    internal static NativeGoLiveProductionPortFactory CreateProduction(
        NativeGoLivePlan plan,
        string mergedMainRoot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ValidateCanonicalProduction();
        return new NativeGoLiveProductionPortFactory(plan, mergedMainRoot);
    }
}

/// <summary>
/// Holds only pure one-shot composition values. Binding the bootstrap connection and closeout
/// capability is deferred to the guarded host.
/// </summary>
internal sealed class NativeGoLiveProductionPortFactory
{
    private readonly NativeGoLivePlan _plan;
    private readonly string _mergedMainRoot;

    internal NativeGoLiveProductionPortFactory(
        NativeGoLivePlan plan,
        string mergedMainRoot)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _mergedMainRoot = CanonicalPath(mergedMainRoot);
    }

    internal NativeGoLiveHostPorts Bind(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLiveSqlBootstrapConnection bootstrap)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!ReferenceEquals(capability.Plan, _plan) ||
            !SamePath(capability.MergedMainRoot, _mergedMainRoot))
            throw new NativeGoLiveContractException("go-live-closeout-capability-binding-mismatch");

        var iis = new NativeGoLiveWindowsIisPort();
        var sql = new NativeGoLiveWindowsSqlPort(_plan, _mergedMainRoot);
        var acls = new NativeGoLiveWindowsAclPort();
        var marketplace = new NativeGoLiveWindowsMarketplacePort(capability, _plan.Codex);
        var vss = new NativeGoLiveVssComPort();
        var ownedState = new NativeGoLiveWindowsOwnedStatePort(_plan, bootstrap);
        return new NativeGoLiveHostPorts(
            new NativeGoLiveWindowsPreflightPort(_plan, iis, sql, marketplace, vss),
            iis,
            ownedState,
            sql,
            acls,
            new NativeGoLiveWindowsPublishPort(_plan),
            new NativeGoLiveWindowsLoopbackPort(_plan),
            marketplace,
            vss,
            new NativeGoLivePublishedCompositionPort(_plan),
            new NativeGoLiveWindowsOneShotAdmissionPort(_plan, ownedState, sql, bootstrap),
            new NativeGoLiveWindowsTaskActivationPort(_plan));
    }

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new NativeGoLiveContractException("merged-main-root-not-canonical");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
}

internal sealed class NativeGoLiveWindowsPreflightPort : INativeGoLiveOneShotPreflightPort
{
    private readonly NativeGoLivePlan _plan;
    private readonly NativeGoLiveWindowsPreflightSources _sources;

    internal NativeGoLiveWindowsPreflightPort(
        NativeGoLivePlan plan,
        NativeGoLiveWindowsIisPort iis,
        NativeGoLiveWindowsSqlPort sql,
        NativeGoLiveWindowsMarketplacePort marketplace,
        INativeGoLiveVssPort vss)
        : this(
            plan,
            new NativeGoLiveWindowsPreflightSources(
                iis.Observe,
                sql.ObservePreflightAsync,
                _ => NativeGoLiveRuntimeConfiguration.ReadMergedMain(plan, sql.MergedMainRoot),
                marketplace.ObserveAsync,
                vss.Query))
    {
        ArgumentNullException.ThrowIfNull(iis);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(marketplace);
        ArgumentNullException.ThrowIfNull(vss);
    }

    internal NativeGoLiveWindowsPreflightPort(
        NativeGoLivePlan plan,
        NativeGoLiveWindowsPreflightSources sources)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public async ValueTask<NativeGoLivePreflightObservation> ObserveAsync(
        NativeGoLivePlan expectedPlan,
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_plan, expectedPlan))
            throw new NativeGoLiveContractException("go-live-plan-not-canonical");
        cancellationToken.ThrowIfCancellationRequested();
        var root = NativeGoLiveWindowsRootObserver.ObserveOneShot(_plan);
        var sqlObservation = await _sources.ObserveSql(bootstrap, cancellationToken).ConfigureAwait(false);
        var marketObservation = await _sources.ObserveMarketplace(_plan.Codex, cancellationToken).ConfigureAwait(false);
        return new NativeGoLivePreflightObservation(
            _sources.ObserveIis(_plan),
            root,
            sqlObservation,
            _sources.ObserveRuntime(_plan),
            marketObservation,
            _sources.ObserveVss(cancellationToken));
    }
}

internal sealed record NativeGoLiveWindowsPreflightSources(
    Func<NativeGoLivePlan, NativeGoLiveIisObservation> ObserveIis,
    Func<NativeGoLiveSqlBootstrapConnection, CancellationToken, ValueTask<NativeGoLiveSqlPreflightObservation>> ObserveSql,
    Func<NativeGoLivePlan, NativeGoLiveRuntimeObservation> ObserveRuntime,
    Func<NativeGoLiveCodexIdentity, CancellationToken, ValueTask<NativeGoLiveMarketplaceObservation>> ObserveMarketplace,
    Func<CancellationToken, NativeGoLiveVssPreflightObservation> ObserveVss);

/// <summary>Creates every native child process with the bootstrap authority removed before any
/// executable receives its environment.</summary>
internal static class NativeGoLiveChildStartBuilder
{
    internal static ProcessStartInfo Create(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        NativeGoLiveProcessEnvironment.RemoveBootstrapFromChildEnvironment(start);
        return start;
    }
}

/// <summary>
/// Runs the published Web assembly in its no-listener composition-validation mode.  No output is
/// surfaced because it may contain configuration diagnostics; an exit code is the only proof.
/// </summary>
internal sealed class NativeGoLivePublishedCompositionPort(NativeGoLivePlan plan) : INativeGoLiveCompositionPort
{
    public async ValueTask ValidatePublishedCompositionAsync(
        NativeGoLivePlan expectedPlan,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(expectedPlan, plan))
            throw new NativeGoLiveContractException("go-live-plan-not-canonical");
        var assembly = Path.Combine(plan.Layout.ApplicationRoot, "FluxKnowledge.Web.dll");
        if (!File.Exists(assembly))
            throw new NativeGoLiveContractException("published-composition-assembly-missing");
        var start = NativeGoLiveChildStartBuilder.Create("dotnet");
        start.WorkingDirectory = plan.Layout.ApplicationRoot;
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--validate-native-go-live-composition");
        using var process = Process.Start(start)
            ?? throw new NativeGoLiveContractException("published-composition-start-failed");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new NativeGoLiveContractException("published-composition-invalid");
    }
}

internal static class NativeGoLiveWindowsRootObserver
{
    internal static NativeGoLiveRootObservation ObserveOneShot(NativeGoLivePlan plan)
    {
        var ancestors = EnumerateAncestors(plan.Layout.Root)
            .Select(ObserveAncestor)
            .ToArray();
        var rootExists = Directory.Exists(plan.Layout.Root) || File.Exists(plan.Layout.Root);
        var hasReparse = ancestors.Any(value => value.IsReparsePoint) ||
            Directory.Exists(plan.Layout.Root) && EnumerateTree(plan.Layout.Root)
                .Any(info => info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        return new NativeGoLiveRootObservation(
            plan.Layout.Root,
            rootExists,
            hasReparse,
            plan.CommittedSha,
            plan.PlanHash,
            ancestors);
    }

    private static NativeGoLivePathAncestorObservation ObserveAncestor(string path)
    {
        var inspection = FileSystemLiveRootPathInspector.Instance.Inspect(path);
        var volumeId = inspection.Exists ? NativeGoLiveWindowsVolumeIdentity.Query(path) : string.Empty;
        return new NativeGoLivePathAncestorObservation(
            path,
            inspection.ResolvedPath ?? string.Empty,
            volumeId,
            inspection.Exists,
            inspection.IsReparsePoint);
    }

    private static IEnumerable<FileSystemInfo> EnumerateTree(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directory.Refresh();
            yield return directory;
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                entry.Refresh();
                yield return entry;
                if (entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(child);
            }
        }
    }

    private static IReadOnlyList<string> EnumerateAncestors(string root)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var pathRoot = Path.GetPathRoot(canonical)
            ?? throw new NativeGoLiveContractException("live-root-not-canonical-native");
        var result = new List<string> { pathRoot };
        var current = pathRoot;
        foreach (var segment in Path.GetRelativePath(pathRoot, canonical)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            result.Add(current);
        }
        return result;
    }
}

internal static class NativeGoLiveWindowsVolumeIdentity
{
    internal static string Query(string path)
    {
        if (!OperatingSystem.IsWindows())
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        var mountPoint = new StringBuilder(512);
        if (!GetVolumePathName(Path.GetFullPath(path), mountPoint, mountPoint.Capacity))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint.ToString(), buffer, buffer.Capacity))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        return buffer.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName, StringBuilder volumePathName, int bufferLength);
}

/// <summary>
/// The sole production configuration serializer.  It deliberately returns bytes only; callers
/// validate and write those bytes through the no-follow handle-relative writer without logging
/// connection material.
/// </summary>
internal static class NativeGoLiveProductionConfigurationSerializer
{
    internal static byte[] Serialize(
        NativeGoLivePlan plan,
        NativeGoLiveSqlBootstrapConnection bootstrap)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bootstrap);
        return Serialize(plan, CreateConnectionString(plan, bootstrap));
    }

    /// <summary>Serializes the sole canonical byte representation used both for the atomic write
    /// and for its subsequent no-follow read-back verification.</summary>
    internal static byte[] Serialize(
        NativeGoLivePlan plan,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            ConnectionStrings = new { FluxKnowledge = connectionString },
            SqlServer = new { DataFilePath = plan.Sql.DataFilePath, LogFilePath = plan.Sql.LogFilePath },
            PrivatePc = new { LocalApplicationDataRoot = plan.Layout.ConfigRoot },
            SourceRoots = Array.Empty<string>(),
            LocalIngress = new { AllowedRoots = new[] { plan.Layout.RetainedRoot } },
            Outlook = new { Enabled = false },
            OutlookCapture = new { Enabled = true },
            Worker = new { Enabled = true },
            NativeWorker = new { Enabled = false },
            Runtime = new
            {
                Model = new { Enabled = false },
                Gpu = new { Enabled = false },
                Ocr = new { Enabled = false },
                Asr = new { Enabled = false },
                Ffmpeg = new { Enabled = false },
                NetworkParsing = new { Enabled = false }
            }
        });
    }

    internal static string CreateConnectionString(
        NativeGoLivePlan plan,
        NativeGoLiveSqlBootstrapConnection bootstrap)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bootstrap);
        var configuration = new SqlConnectionStringBuilder(bootstrap.ConnectionString)
        {
            InitialCatalog = plan.Sql.CatalogName,
            ApplicationName = "FluxKnowledge.Native"
        };
        if (!configuration.IntegratedSecurity ||
            !string.Equals(configuration.DataSource, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(configuration.InitialCatalog, plan.Sql.CatalogName, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(configuration.UserID) || !string.IsNullOrEmpty(configuration.Password) ||
            !configuration.Encrypt || !configuration.TrustServerCertificate || configuration.Pooling)
        {
            throw new NativeGoLiveContractException("production-configuration-connection-invalid");
        }
        return configuration.ConnectionString;
    }
}

internal sealed class NativeGoLiveWindowsOneShotAdmissionPort : INativeGoLiveAdmissionPort
{
    private readonly NativeGoLivePlan _plan;
    private readonly NativeGoLiveWindowsOwnedStatePort _ownedState;
    private readonly Func<CancellationToken, ValueTask<bool>> _catalogueExists;
    private readonly Func<CancellationToken, ValueTask> _dropCatalogue;

    internal NativeGoLiveWindowsOneShotAdmissionPort(
        NativeGoLivePlan plan,
        NativeGoLiveWindowsOwnedStatePort ownedState,
        NativeGoLiveWindowsSqlPort sql,
        NativeGoLiveSqlBootstrapConnection bootstrap)
        : this(
            plan,
            ownedState,
            cancellationToken => sql.CatalogueExistsAsync(bootstrap, cancellationToken),
            cancellationToken => sql.DropCatalogueAsync(bootstrap, cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(bootstrap);
    }

    internal NativeGoLiveWindowsOneShotAdmissionPort(
        NativeGoLivePlan plan,
        NativeGoLiveWindowsOwnedStatePort ownedState,
        Func<CancellationToken, ValueTask<bool>> catalogueExists,
        Func<CancellationToken, ValueTask> dropCatalogue)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _ownedState = ownedState ?? throw new ArgumentNullException(nameof(ownedState));
        _catalogueExists = catalogueExists ?? throw new ArgumentNullException(nameof(catalogueExists));
        _dropCatalogue = dropCatalogue ?? throw new ArgumentNullException(nameof(dropCatalogue));
    }

    public async ValueTask<NativeGoLiveOneShotAdmission> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rootPath = _plan.Layout.Root;
        var rootDirectoryExists = Directory.Exists(rootPath);
        var rootFileExists = !rootDirectoryExists && File.Exists(rootPath);
        var catalogueExists = await _catalogueExists(cancellationToken).ConfigureAwait(false);
        return new NativeGoLiveOneShotAdmission(rootDirectoryExists || rootFileExists, catalogueExists);
    }

    public async ValueTask WipeAsync(CancellationToken cancellationToken)
    {
        if (await _catalogueExists(cancellationToken).ConfigureAwait(false))
            await _dropCatalogue(cancellationToken).ConfigureAwait(false);
        await _ownedState.WipeRootAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class NativeGoLiveWindowsOwnedStatePort(
    NativeGoLivePlan plan,
    NativeGoLiveSqlBootstrapConnection bootstrap,
    HandleRelativeNativeFileSystem? fileSystem = null) : INativeGoLiveOwnedStatePort
{
    private readonly HandleRelativeNativeFileSystem _fileSystem = fileSystem ?? new HandleRelativeNativeFileSystem();

    public async ValueTask WipeRootAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(plan.Layout.Root))
        {
            if (File.Exists(plan.Layout.Root))
            {
                var fileParentPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(plan.Layout.Root))
                    ?? throw new NativeGoLiveContractException("go-live-root-parent-invalid");
                var fileRootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.Layout.Root));
                using var fileParent = _fileSystem.OpenDirectory(fileParentPath);
                var fileRoot = _fileSystem.InspectLiteralChild(fileParent, fileRootName);
                if (fileRoot.IsDirectory)
                    throw new NativeGoLiveContractException("go-live-root-identity-changed");
                RequireMutation(await _fileSystem.DeleteLiteralChildAsync(
                    fileParent, fileRootName, fileRoot.Identity, cancellationToken).ConfigureAwait(false));
            }
            return;
        }

        var parentPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(plan.Layout.Root))
            ?? throw new NativeGoLiveContractException("go-live-root-parent-invalid");
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.Layout.Root));
        using var parent = _fileSystem.OpenDirectory(parentPath);
        var root = _fileSystem.InspectLiteralChild(parent, rootName);
        if (!root.IsDirectory)
            throw new NativeGoLiveContractException("go-live-root-not-directory");
        using (var directory = _fileSystem.OpenDirectory(parent, rootName))
        {
            if (directory.Identity != root.Identity)
                throw new NativeGoLiveContractException("go-live-root-identity-changed");
            RequireMutation(await _fileSystem.DeleteTreeContentsAsync(directory, cancellationToken).ConfigureAwait(false));
        }
        RequireMutation(await _fileSystem.DeleteLiteralChildAsync(parent, rootName, root.Identity, cancellationToken)
            .ConfigureAwait(false));
    }

    public async ValueTask CreateEmptyRootAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var root = _fileSystem.OpenOrCreateDirectory(plan.Layout.Root);
        foreach (var child in new[] { "App", "Config", "Data", "Runtime", "CodexPlugin", "Recovery" })
        {
            if (_fileSystem.EnumerateLiteralChildren(root).Contains(child, StringComparer.OrdinalIgnoreCase))
                continue;
            RequireMutation(await _fileSystem.CreateDirectoryAsync(root, child, cancellationToken).ConfigureAwait(false));
        }
        using var data = _fileSystem.OpenDirectory(root, "Data");
        foreach (var child in new[] { "Sql", "Index", "Retained", "Spool", "Temp", "Logs" })
        {
            if (_fileSystem.EnumerateLiteralChildren(data).Contains(child, StringComparer.OrdinalIgnoreCase))
                continue;
            RequireMutation(await _fileSystem.CreateDirectoryAsync(data, child, cancellationToken).ConfigureAwait(false));
        }
        using var sql = _fileSystem.OpenDirectory(data, "Sql");
        foreach (var child in new[] { "Data", "Log" })
        {
            if (_fileSystem.EnumerateLiteralChildren(sql).Contains(child, StringComparer.OrdinalIgnoreCase))
                continue;
            RequireMutation(await _fileSystem.CreateDirectoryAsync(sql, child, cancellationToken).ConfigureAwait(false));
        }
        using var runtime = _fileSystem.OpenDirectory(root, "Runtime");
        foreach (var child in new[] { "Spool", "Temp", "Logs" })
        {
            if (_fileSystem.EnumerateLiteralChildren(runtime).Contains(child, StringComparer.OrdinalIgnoreCase))
                continue;
            RequireMutation(await _fileSystem.CreateDirectoryAsync(runtime, child, cancellationToken).ConfigureAwait(false));
        }
    }

    public async ValueTask WriteProductionConfigurationAsync(CancellationToken cancellationToken)
    {
        var payload = NativeGoLiveProductionConfigurationSerializer.Serialize(plan, bootstrap);
        using var configRoot = _fileSystem.OpenDirectory(plan.Layout.ConfigRoot);
        const string name = "appsettings.Production.json";
        const string temporaryName = "appsettings.Production.json.tmp";
        var existing = await _fileSystem.ReadLiteralFileAsync(configRoot, name, cancellationToken).ConfigureAwait(false);
        RequireMutation(await _fileSystem.ReplaceFileAsync(
            configRoot, temporaryName, name, payload, existing?.Identity, cancellationToken).ConfigureAwait(false));
        var stored = await _fileSystem.ReadLiteralFileAsync(configRoot, name, cancellationToken).ConfigureAwait(false)
            ?? throw new NativeGoLiveContractException("production-configuration-write-failed");
        NativeGoLiveRuntimeConfiguration.ValidateProductionConfiguration(
            stored.Content, plan, NativeGoLiveProductionConfigurationSerializer.CreateConnectionString(plan, bootstrap));
    }

    private static void RequireMutation(NativeFileMutation mutation)
    {
        if (!mutation.Changed)
            throw new NativeGoLiveContractException("owned-state-" + (mutation.Reason ?? "mutation-refused"));
    }

}

internal sealed class NativeGoLiveWindowsSqlPort
    : INativeGoLiveSqlPort
{
    private readonly NativeGoLivePlan _plan;
    private readonly string _mergedMainRoot;
    private readonly HandleRelativeNativeFileSystem _fileSystem = new();

    internal NativeGoLiveWindowsSqlPort(NativeGoLivePlan plan, string mergedMainRoot)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _mergedMainRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mergedMainRoot));
    }

    internal string MergedMainRoot => _mergedMainRoot;

    internal async ValueTask<bool> CatalogueExistsAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "SELECT CONVERT(int, CASE WHEN DB_ID(N'FluxKnowledge') IS NULL THEN 0 ELSE 1 END);",
            connection) { CommandTimeout = bootstrap.ConnectTimeout };
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    internal async ValueTask<NativeGoLiveSqlPreflightObservation> ObservePreflightAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql =
            """
            SELECT CONVERT(int, SERVERPROPERTY('IsFullTextInstalled'));
            SELECT p.name,p.object_id,
                   LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),REPLACE(sm.definition,CHAR(13)+CHAR(10),CHAR(10)))),2))
            FROM sys.procedures p JOIN sys.sql_modules sm ON sm.object_id=p.object_id
            WHERE SCHEMA_NAME(p.schema_id)=N'dbo' AND p.name IN
                (N'FluxKnowledgeNativeGoLiveCreate',N'FluxKnowledgeNativeGoLiveDrop')
            ORDER BY p.name;
            SELECT p.name,prm.parameter_id,prm.name,TYPE_NAME(prm.user_type_id),prm.max_length,CONVERT(int,prm.is_output)
            FROM sys.procedures p JOIN sys.parameters prm ON prm.object_id=p.object_id
            WHERE SCHEMA_NAME(p.schema_id)=N'dbo' AND p.name IN
                (N'FluxKnowledgeNativeGoLiveCreate',N'FluxKnowledgeNativeGoLiveDrop')
            ORDER BY p.name,prm.parameter_id;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = bootstrap.ConnectTimeout };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-preflight-observation-missing");
        var fullText = reader.GetInt32(0) == 1;
        var procedures = await ReadProcedureEvidenceAsync(reader, cancellationToken).ConfigureAwait(false);
        await reader.DisposeAsync().ConfigureAwait(false);
        return new NativeGoLiveSqlPreflightObservation(fullText, procedures);
    }

    public async ValueTask ProvisionEmptyCatalogueAsync(
        NativeGoLiveSqlIdentity identity,
        NativeGoLiveSqlBootstrapConnection bootstrap,
        NativeGoLivePayloadManifest payloadManifest,
        CancellationToken cancellationToken)
    {
        if (identity != _plan.Sql) throw new NativeGoLiveContractException("sql-identity-not-canonical");
        using var dataRoot = OpenSqlStorageDirectory(_plan.Layout.SqlDataRoot, "data");
        using var logRoot = OpenSqlStorageDirectory(_plan.Layout.SqlLogRoot, "log");
        await ExecuteCreateAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        await RunMigrationsAsync(bootstrap, payloadManifest, cancellationToken).ConfigureAwait(false);
        await MarkEmptyCatalogueAsync(bootstrap, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask DropCatalogueAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(bootstrap.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(
                "EXEC master.dbo.FluxKnowledgeNativeGoLiveDrop @Catalogue=N'FluxKnowledge';",
                connection) { CommandTimeout = 30 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            throw new NativeGoLiveContractException(
                $"sql-admission-drop-error-{exception.Number}",
                innerException: exception);
        }
    }

    private async ValueTask ExecuteCreateAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(bootstrap.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql =
                "EXEC master.dbo.FluxKnowledgeNativeGoLiveCreate " +
                "@Catalogue=N'FluxKnowledge',@DataFile=@data,@LogFile=@log;";
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            command.Parameters.AddWithValue("@data", _plan.Sql.DataFilePath);
            command.Parameters.AddWithValue("@log", _plan.Sql.LogFilePath);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            throw new NativeGoLiveContractException(
                $"sql-provisioning-create-error-{exception.Number}",
                innerException: exception);
        }
        catch (Exception exception)
        {
            throw MapCreateFailure(exception);
        }
    }

    internal static NativeGoLiveContractException MapCreateFailure(Exception exception) =>
        new(
            "sql-provisioning-create-failed",
            $"hresult-0x{unchecked((uint)exception.HResult):X8}",
            exception);

    private VerifiedNativeDirectory OpenSqlStorageDirectory(string path, string role)
    {
        try
        {
            return _fileSystem.OpenOrCreateDirectory(path);
        }
        catch (NativeGoLiveContractException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new NativeGoLiveContractException(
                $"sql-provisioning-storage-{role}-failed",
                $"hresult-0x{unchecked((uint)exception.HResult):X8}",
                exception);
        }
    }

    private async ValueTask RunMigrationsAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        NativeGoLivePayloadManifest payloadManifest,
        CancellationToken cancellationToken)
    {
        var application = new SqlConnectionStringBuilder(bootstrap.ConnectionString)
        {
            InitialCatalog = _plan.Sql.CatalogName,
            ApplicationName = "FluxKnowledge.NativeGoLive.Migrations"
        }.ConnectionString;
        await NativeGoLivePublishedMigrationRunner.MigrateAsync(
            _mergedMainRoot, application, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MarkEmptyCatalogueAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CatalogueConnection(bootstrap));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql =
            """
            SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION;
            IF EXISTS(SELECT 1 FROM dbo.Vectors) OR EXISTS(SELECT 1 FROM dbo.IndexGenerations)
               OR EXISTS(SELECT 1 FROM dbo.IndexGenerationVectors) THROW 51000,'empty-catalogue-state-not-empty',1;
            UPDATE dbo.IndexState SET ActiveIndexGenerationId=NULL, EmptyCatalogueValidatedAtUtc=SYSUTCDATETIME(),
                                      UpdatedAtUtc=SYSUTCDATETIME() WHERE Id=1;
            IF @@ROWCOUNT<>1 THROW 51000,'empty-catalogue-index-state-missing',1;
            COMMIT TRANSACTION;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string CatalogueConnection(NativeGoLiveSqlBootstrapConnection bootstrap) =>
        new SqlConnectionStringBuilder(bootstrap.ConnectionString)
        {
            InitialCatalog = _plan.Sql.CatalogName
        }.ConnectionString;

    private static async Task<IReadOnlyList<NativeGoLiveSqlProcedureObservation>> ReadProcedureEvidenceAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) return [];
        var procedures = new Dictionary<string, (
            int ObjectId,
            string Hash)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            procedures.Add(reader.GetString(0), (
                reader.GetInt32(1), reader.GetString(2)));
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) return [];
        var parameters = new Dictionary<string, List<NativeGoLiveSqlParameterObservation>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!parameters.TryGetValue(name, out var list)) parameters.Add(name, list = []);
            list.Add(new NativeGoLiveSqlParameterObservation(
                reader.GetString(2), reader.GetString(3), reader.GetInt16(4), reader.GetInt32(5) == 1));
        }
        return procedures.Select(pair => new NativeGoLiveSqlProcedureObservation(
            pair.Key,
            pair.Value.ObjectId,
            pair.Value.Hash,
            parameters.GetValueOrDefault(pair.Key) ?? [])).ToArray();
    }

}

internal static class NativeGoLivePublishedMigrationRunner
{
    internal const string ConnectionEnvironmentVariable =
        "FLUXKNOWLEDGE_NATIVE_GO_LIVE_MIGRATION_CONNECTION";

    internal static async ValueTask MigrateAsync(
        string publishedRoot,
        string applicationConnectionString,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateStartInfo(publishedRoot, applicationConnectionString))
            ?? throw new NativeGoLiveContractException("published-migration-start-failed");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new NativeGoLiveContractException("published-migration-failed");
    }

    internal static ProcessStartInfo CreateStartInfo(string publishedRoot, string applicationConnectionString)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(publishedRoot));
        var assembly = Path.Combine(root, "FluxKnowledge.Web.dll");
        if (!File.Exists(assembly))
            throw new NativeGoLiveContractException("published-migration-assembly-missing");
        var start = NativeGoLiveChildStartBuilder.Create("dotnet");
        start.WorkingDirectory = root;
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--apply-native-go-live-migrations");
        start.Environment[ConnectionEnvironmentVariable] = applicationConnectionString;
        return start;
    }
}

internal sealed class NativeGoLiveWindowsAclPort : INativeGoLiveAclPort
{
    private const string AppPoolLogin = @"IIS AppPool\FluxKnowledge";
    private readonly HandleRelativeNativeFileSystem _fileSystem = new();

    public ValueTask<NativeGoLiveAclObservation> ApplyAndObserveAsync(
        NativeGoLivePlan expectedPlan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native ACL administration is Windows-only.");
        var appPoolSid = ResolveSid(AppPoolLogin);
        var sqlSid = ResolveSid(ReadSqlServiceIdentity());
        var layout = expectedPlan.Layout;
        var dataProtection = Path.Combine(layout.ConfigRoot, "data-protection");
        foreach (var path in new[]
                 {
                     layout.Root, layout.ApplicationRoot, layout.ConfigRoot, dataProtection,
                     layout.DataRoot, layout.SqlRoot, layout.RuntimeRoot, layout.CodexPluginRoot,
                     layout.SqlDataRoot, layout.SqlLogRoot, layout.IndexRoot, layout.RetainedRoot,
                     layout.SpoolRoot, layout.TempRoot, layout.LogsRoot, layout.RecoveryRoot
                 })
            using (var directory = _fileSystem.OpenOrCreateDirectory(path)) { }

        foreach (var path in new[] { layout.SqlRoot, layout.CodexPluginRoot })
            ApplyDirectoryAcl(path);
        foreach (var path in new[] { layout.Root, layout.DataRoot, layout.RuntimeRoot })
            ApplyDirectoryAcl(path, (appPoolSid, FileSystemRights.ReadAndExecute));
        ApplyDirectoryAcl(layout.ApplicationRoot, (appPoolSid, FileSystemRights.ReadAndExecute));
        ApplyDirectoryAcl(layout.ConfigRoot, (appPoolSid, FileSystemRights.Read));
        ApplyDirectoryAcl(dataProtection, (appPoolSid, FileSystemRights.Modify));
        ApplyDirectoryAcl(layout.SqlDataRoot, (sqlSid, FileSystemRights.Modify));
        ApplyDirectoryAcl(layout.SqlLogRoot, (sqlSid, FileSystemRights.Modify));
        foreach (var path in new[] { layout.IndexRoot, layout.RetainedRoot, layout.SpoolRoot, layout.TempRoot, layout.LogsRoot })
            ApplyDirectoryAcl(path, (appPoolSid, FileSystemRights.Modify));
        ApplyDirectoryAcl(layout.RecoveryRoot);
        return ObserveEffectiveAsync(expectedPlan, cancellationToken);
    }

    public ValueTask<NativeGoLiveAclObservation> ObserveEffectiveAsync(
        NativeGoLivePlan expectedPlan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var layout = expectedPlan.Layout;
        var appPoolSid = ResolveSid(AppPoolLogin);
        var sqlSid = ResolveSid(ReadSqlServiceIdentity());
        var dataProtection = Path.Combine(layout.ConfigRoot, "data-protection");
        var candidates = new[]
        {
            layout.Root, layout.ApplicationRoot, layout.ConfigRoot, dataProtection,
            layout.DataRoot, layout.SqlRoot, layout.SqlDataRoot, layout.SqlLogRoot,
            layout.IndexRoot, layout.RetainedRoot, layout.SpoolRoot, layout.TempRoot, layout.LogsRoot,
            layout.RuntimeRoot, layout.CodexPluginRoot, layout.RecoveryRoot
        };
        var sqlWrite = candidates.Where(path => NativeGoLiveWindowsAclInspector.HasRights(
            path, sqlSid.Value, FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.CreateFiles)).ToArray();
        var appModify = candidates.Where(path => NativeGoLiveWindowsAclInspector.HasRights(
            path, appPoolSid.Value, FileSystemRights.Modify)).ToArray();
        var paths = candidates.Select(NativeGoLiveWindowsAclInspector.Observe).ToArray();
        var appReadExecute = new[] { layout.Root, layout.ApplicationRoot, layout.DataRoot, layout.RuntimeRoot }
            .Where(path => NativeGoLiveWindowsAclInspector.HasRights(
            path, appPoolSid.Value, FileSystemRights.ReadAndExecute)).ToArray();
        return ValueTask.FromResult(new NativeGoLiveAclObservation(
            sqlWrite,
            appReadExecute,
            NativeGoLiveWindowsAclInspector.HasRights(layout.ConfigRoot, appPoolSid.Value, FileSystemRights.Read)
                ? [layout.ConfigRoot] : [],
            appModify,
            NativeGoLiveWindowsAclInspector.HasAnyAccess(layout.SqlRoot, appPoolSid.Value),
            NativeGoLiveWindowsAclInspector.HasRights(layout.ApplicationRoot, appPoolSid.Value, FileSystemRights.Write),
            NativeGoLiveWindowsAclInspector.HasRights(layout.ConfigRoot, appPoolSid.Value, FileSystemRights.Write),
            NativeGoLiveWindowsAclInspector.HasAnyAccess(layout.RecoveryRoot, appPoolSid.Value),
            NativeGoLiveWindowsAclInspector.HasRights(dataProtection, appPoolSid.Value, FileSystemRights.Modify) &&
            !NativeGoLiveWindowsAclInspector.HasRights(layout.ConfigRoot, appPoolSid.Value, FileSystemRights.Write),
            appPoolSid.Value,
            sqlSid.Value,
            paths));
    }

    private void ApplyDirectoryAcl(
        string path,
        params (SecurityIdentifier Sid, FileSystemRights Rights)[] grants)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var (sid, rights) in grants)
            security.AddAccessRule(new FileSystemAccessRule(
                sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        using var directory = _fileSystem.OpenDirectoryForSecurity(path);
        _fileSystem.SetDirectorySecurityAsync(directory, security).GetAwaiter().GetResult();
    }

    private static SecurityIdentifier ResolveSid(string account) =>
        (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));

    private static string ReadSqlServiceIdentity()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native ACL administration is Windows-only.");
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\MSSQLSERVER", writable: false);
        return key?.GetValue("ObjectName") as string is { Length: > 0 } identity
            ? identity
            : throw new NativeGoLiveContractException("sql-service-identity-unavailable");
    }
}

internal static class NativeGoLiveWindowsAclInspector
{
    internal static NativeGoLiveAclPathObservation Observe(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(
                includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new NativeGoLiveAclAceObservation(
                rule.IdentityReference.Value,
                (long)rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow,
                rule.IsInherited,
                (int)rule.InheritanceFlags,
                (int)rule.PropagationFlags,
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0,
                (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0,
                (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0))
            .ToArray();
        return new NativeGoLiveAclPathObservation(path, security.AreAccessRulesProtected, rules);
    }

    internal static bool HasAnyAccess(string path, string sid) =>
        HasRights(path, sid, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.ExecuteFile |
                             FileSystemRights.Delete | FileSystemRights.ReadPermissions);

    internal static bool HasRights(string path, string sid, FileSystemRights requested)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return false;
        if (!OperatingSystem.IsWindows()) return false;
        var rules = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetAccessRules(
                includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetAccessRules(
                includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var allow = (FileSystemRights)0;
        var deny = (FileSystemRights)0;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (!string.Equals(rule.IdentityReference.Value, sid, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.AccessControlType == AccessControlType.Deny) deny |= rule.FileSystemRights;
            else allow |= rule.FileSystemRights;
        }
        return (deny & requested) == 0 && (allow & requested) == requested;
    }
}

/// <summary>
/// Activates the already-published native work only after IIS has proved the application started.
/// The web-host source worker is enabled by the published production configuration; this port only
/// enables and starts the pre-provisioned native Outlook task. It never creates or alters task XML.
/// </summary>
internal sealed class NativeGoLiveWindowsTaskActivationPort : INativeGoLiveTaskActivationPort
{
    private const string OutlookTaskName = "FluxKnowledge.OutlookHost";
    private readonly NativeGoLivePlan _plan;
    private readonly Func<string, CancellationToken, ValueTask<int>>? _runCommand;
    private readonly Func<bool> _isWindows;

    internal NativeGoLiveWindowsTaskActivationPort(NativeGoLivePlan plan)
        : this(plan, null, OperatingSystem.IsWindows)
    {
    }

    internal NativeGoLiveWindowsTaskActivationPort(
        NativeGoLivePlan plan,
        Func<string, CancellationToken, ValueTask<int>>? runCommand,
        Func<bool> isWindows)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _runCommand = runCommand;
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
    }

    public async ValueTask ActivateAfterApplicationStartAsync(
        NativeGoLivePlan expectedPlan,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_plan, expectedPlan))
            throw new NativeGoLiveContractException("go-live-plan-not-canonical");
        if (!_isWindows())
            throw new NativeGoLiveContractException("native-task-activation-windows-only");

        var command = BuildActivationCommand();
        if (_runCommand is not null)
        {
            if (await _runCommand(command, cancellationToken).ConfigureAwait(false) != 0)
                throw new NativeGoLiveContractException("native-outlook-task-activation-failed");
            return;
        }
        var start = NativeGoLiveChildStartBuilder.Create("powershell.exe");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)
            ?? throw new NativeGoLiveContractException("native-outlook-task-runner-unavailable");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new NativeGoLiveContractException("native-outlook-task-activation-failed");
    }

    internal static bool IsSuccessfulFreshTaskRun(
        DateTimeOffset? beforeLastRunTimeUtc,
        DateTimeOffset? observedLastRunTimeUtc,
        int observedLastTaskResult,
        bool isRunning) =>
        observedLastRunTimeUtc is not null &&
        (beforeLastRunTimeUtc is null || observedLastRunTimeUtc > beforeLastRunTimeUtc) &&
        !isRunning &&
        observedLastTaskResult == 0;

    internal static string BuildActivationCommand() => "& { " +
        $"Enable-ScheduledTask -TaskName '{OutlookTaskName}' -ErrorAction Stop | Out-Null; " +
        $"$before = Get-ScheduledTaskInfo -TaskName '{OutlookTaskName}' -ErrorAction Stop; " +
        $"Start-ScheduledTask -TaskName '{OutlookTaskName}' -ErrorAction Stop; " +
        "$deadline = [DateTime]::UtcNow.AddSeconds(30); $launchProved = $false; " +
        "do { " +
        $"$task = Get-ScheduledTask -TaskName '{OutlookTaskName}' -ErrorAction Stop; " +
        $"$info = Get-ScheduledTaskInfo -TaskName '{OutlookTaskName}' -ErrorAction Stop; " +
        "if ($info.LastRunTime.ToUniversalTime() -gt $before.LastRunTime.ToUniversalTime() -and " +
        "$task.State -ne 'Running' -and $info.LastTaskResult -eq 0) { $launchProved = $true; break }; " +
        "Start-Sleep -Milliseconds 250 " +
        "} while ([DateTime]::UtcNow -lt $deadline); " +
        "if (-not $launchProved) { throw 'native-outlook-task-launch-not-proved' } }";
}

internal sealed class NativeGoLiveWindowsMarketplacePort : INativeGoLiveMarketplacePort
{
    private readonly NativeGoLiveCloseoutCapability _capability;
    private readonly NativeGoLiveCodexIdentity _expected;
    private readonly INativeCodexMarketplaceCommandRunner _runner;
    private CodexMarketplacePreflight? _preflight;

    internal NativeGoLiveWindowsMarketplacePort(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLiveCodexIdentity expected)
        : this(capability, expected, new NativeGoLiveCodexProcessRunner(expected))
    {
    }

    internal NativeGoLiveWindowsMarketplacePort(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLiveCodexIdentity expected,
        INativeCodexMarketplaceCommandRunner runner)
    {
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        if (!ReferenceEquals(_capability.Plan.Codex, _expected))
            throw new NativeGoLiveContractException("marketplace-identity-not-plan-bound");
    }

    internal async ValueTask<NativeGoLiveMarketplaceObservation> ObserveAsync(
        NativeGoLiveCodexIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!CodexMarketplaceIdentityPolicy.Same(identity, _expected))
            throw new NativeGoLiveContractException("marketplace-state-foreign");
        var result = await _runner.ListMarketplacesJsonAsync(cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new NativeGoLiveContractException("marketplace-observation-failed");
        var (state, source) = ParseState(result.StandardOutput, identity);
        _preflight = new CodexMarketplacePreflight(state, source, result.UnrelatedConfigurationStructuralHash);
        return Observation(state, identity);
    }

    public async ValueTask ResetForConfirmedCleanSlateAsync(
        NativeGoLiveCodexIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!_capability.IsConsumedForExecution ||
            !CodexMarketplaceIdentityPolicy.Same(identity, _expected))
            throw new NativeGoLiveContractException("marketplace-authority-not-consumed");

        _ = await _runner.RemoveFluxKnowledgeMarketplaceAsync(cancellationToken).ConfigureAwait(false);
        var observed = await _runner.ListMarketplacesJsonAsync(cancellationToken).ConfigureAwait(false);
        if (observed.ExitCode != 0)
            throw new NativeGoLiveContractException("marketplace-clean-slate-verification-failed");
        var (state, _) = ParseState(observed.StandardOutput, identity);
        if (state != CodexMarketplaceLifecycleState.Missing)
            throw new NativeGoLiveContractException("marketplace-clean-slate-removal-not-proved");
        _preflight = null;
    }

    public async ValueTask<NativeGoLiveMarketplaceObservation> RegisterAndObserveAsync(
        NativeGoLiveCodexIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!_capability.IsConsumedForExecution ||
            !ReferenceEquals(_capability.Plan.Codex, _expected) && _capability.Plan.Codex != _expected ||
            !CodexMarketplaceIdentityPolicy.Same(identity, _expected))
            throw new NativeGoLiveContractException("marketplace-authority-not-consumed");
        var preflight = _preflight ?? throw new NativeGoLiveContractException("marketplace-preflight-missing");
        var lifecycle = new NativeCodexMarketplaceLifecycleAdapter(
            new NativeGoLiveProvisioningCapability(_capability),
            _expected,
            new NativeCodexPluginManifestWriter(),
            _runner,
            preflight);
        var registration = await lifecycle.RegisterAsync(identity, cancellationToken).ConfigureAwait(false);
        if (!registration.IsHealthy)
            throw new NativeGoLiveContractException(registration.Reason ?? "marketplace-registration-failed");
        var observed = await _runner.ListMarketplacesJsonAsync(cancellationToken).ConfigureAwait(false);
        if (observed.ExitCode != 0)
            throw new NativeGoLiveContractException("marketplace-verification-failed");
        var (state, _) = ParseState(observed.StandardOutput, identity);
        if (state != CodexMarketplaceLifecycleState.Registered)
            throw new NativeGoLiveContractException("marketplace-verification-failed");

        var installed = await _runner.AddFluxKnowledgePluginAsync(cancellationToken).ConfigureAwait(false);
        if (installed.ExitCode != 0)
            throw new NativeGoLiveContractException("native-plugin-install-failed");
        var plugins = await _runner.ListPluginsJsonAsync(cancellationToken).ConfigureAwait(false);
        if (plugins.ExitCode != 0 || !HasInstalledEnabledPluginWithoutLegacy(plugins.StandardOutput, identity))
            throw new NativeGoLiveContractException("native-plugin-install-not-proved");
        return Observation(state, identity);
    }

    public async ValueTask RemoveExactLegacyPluginAsync(CancellationToken cancellationToken)
    {
        if (!_capability.IsConsumedForExecution)
            throw new NativeGoLiveContractException("marketplace-authority-not-consumed");

        var before = await _runner.ListPluginsJsonAsync(cancellationToken).ConfigureAwait(false);
        if (before.ExitCode != 0 ||
            !TryReadInstalledPluginState(before.StandardOutput, out var legacyPresent))
            throw new NativeGoLiveContractException("legacy-plugin-removal-not-proved");
        if (!legacyPresent) return;

        var removed = await _runner.RemoveLegacyFluxLlmKbPluginAsync(cancellationToken).ConfigureAwait(false);
        if (removed.ExitCode != 0)
            throw new NativeGoLiveContractException("legacy-plugin-removal-failed");
        var plugins = await _runner.ListPluginsJsonAsync(cancellationToken).ConfigureAwait(false);
        if (plugins.ExitCode != 0 ||
            !TryReadInstalledPluginState(plugins.StandardOutput, out legacyPresent) || legacyPresent)
            throw new NativeGoLiveContractException("legacy-plugin-removal-not-proved");
    }

    private static NativeGoLiveMarketplaceObservation Observation(
        CodexMarketplaceLifecycleState state,
        NativeGoLiveCodexIdentity identity) => new(
        state switch
        {
            CodexMarketplaceLifecycleState.Registered => "ExactExisting",
            CodexMarketplaceLifecycleState.Missing => "Missing",
            _ => "Foreign"
        },
        identity.MarketplaceName,
        identity.MarketplaceRoot,
        identity.PluginName);

    private static (CodexMarketplaceLifecycleState State, string? Source) ParseState(
        string json,
        NativeGoLiveCodexIdentity identity)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("marketplaces", out var marketplaces) ||
                marketplaces.ValueKind != JsonValueKind.Array)
                return (CodexMarketplaceLifecycleState.Unavailable, null);
            var entries = marketplaces.EnumerateArray().ToArray();
            if (!entries.All(IsWellFormedMarketplaceEntry))
                return (CodexMarketplaceLifecycleState.Unavailable, null);
            var matching = entries
                .Where(entry => StringProperty(entry, "name") == identity.MarketplaceName)
                .ToArray();
            if (matching.Length == 0) return (CodexMarketplaceLifecycleState.Missing, null);
            if (matching.Length != 1 ||
                !SamePath(StringProperty(matching[0], "root"), identity.MarketplaceRoot))
                return (CodexMarketplaceLifecycleState.Foreign, null);
            return (CodexMarketplaceLifecycleState.Registered, identity.MarketplaceRoot);
        }
        catch (JsonException)
        {
            return (CodexMarketplaceLifecycleState.Unavailable, null);
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsWellFormedMarketplaceEntry(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object &&
        !string.IsNullOrWhiteSpace(StringProperty(entry, "name")) &&
        !string.IsNullOrWhiteSpace(StringProperty(entry, "root"));

    private static bool HasInstalledEnabledPluginWithoutLegacy(string json, NativeGoLiveCodexIdentity identity)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("installed", out var installed) ||
                installed.ValueKind != JsonValueKind.Array)
                return false;
            var entries = installed.EnumerateArray().ToArray();
            var expected = entries.Where(plugin =>
                string.Equals(StringProperty(plugin, "pluginId"),
                    identity.PluginName + "@" + identity.MarketplaceName, StringComparison.Ordinal)).ToArray();
            return entries.All(IsWellFormedInstalledPluginEntry) &&
                   expected.Length == 1 &&
                   HasInstalledAndEnabledState(expected[0]) &&
                   !entries.Any(plugin => string.Equals(
                       StringProperty(plugin, "pluginId"),
                       "flux-llm-kb@flux-llm-kb-local",
                       StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadInstalledPluginState(string json, out bool legacyPresent)
    {
        legacyPresent = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("installed", out var installed) ||
                installed.ValueKind != JsonValueKind.Array)
                return false;
            var entries = installed.EnumerateArray().ToArray();
            if (!entries.All(IsWellFormedInstalledPluginEntry)) return false;
            legacyPresent = entries.Any(plugin => string.Equals(
                StringProperty(plugin, "pluginId"),
                "flux-llm-kb@flux-llm-kb-local",
                StringComparison.Ordinal));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsWellFormedInstalledPluginEntry(JsonElement plugin) =>
        plugin.ValueKind == JsonValueKind.Object &&
        !string.IsNullOrWhiteSpace(StringProperty(plugin, "pluginId")) &&
        plugin.TryGetProperty("installed", out var installed) &&
        (installed.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
        plugin.TryGetProperty("enabled", out var enabled) &&
        (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False);

    private static bool HasInstalledAndEnabledState(JsonElement plugin) =>
        plugin.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.True &&
        plugin.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed record NativeCodexProcessOutput(int ExitCode, string StandardOutput);

internal sealed class NativeGoLiveCodexProcessRunner : INativeCodexMarketplaceCommandRunner
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private readonly NativeGoLiveCodexIdentity _expected;
    private readonly Func<IReadOnlyList<string>, CancellationToken, ValueTask<NativeCodexProcessOutput>>? _run;

    internal NativeGoLiveCodexProcessRunner(NativeGoLiveCodexIdentity expected)
        : this(expected, null)
    {
    }

    internal NativeGoLiveCodexProcessRunner(
        NativeGoLiveCodexIdentity expected,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<NativeCodexProcessOutput>>? run)
    {
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
        _run = run;
    }

    public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
        string marketplaceRoot,
        CancellationToken cancellationToken)
    {
        if (!SamePath(marketplaceRoot, _expected.MarketplaceRoot))
            throw new NativeGoLiveContractException("marketplace-state-foreign");
        return RunAsync(["plugin", "marketplace", "add", marketplaceRoot], false, cancellationToken);
    }

    public ValueTask<NativeCodexMarketplaceCommandResult> RemoveFluxKnowledgeMarketplaceAsync(
        CancellationToken cancellationToken) =>
        RunAsync(["plugin", "marketplace", "remove", _expected.MarketplaceName], false, cancellationToken);

    public ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
        CancellationToken cancellationToken) =>
        RunAsync(["plugin", "marketplace", "list", "--json"], true, cancellationToken, HashUnrelatedConfiguration);

    public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgePluginAsync(
        CancellationToken cancellationToken) =>
        RunAsync(["plugin", "add", _expected.PluginName + "@" + _expected.MarketplaceName], false, cancellationToken);

    public ValueTask<NativeCodexMarketplaceCommandResult> ListPluginsJsonAsync(
        CancellationToken cancellationToken) =>
        RunAsync(["plugin", "list", "--json"], true, cancellationToken);

    public ValueTask<NativeCodexMarketplaceCommandResult> RemoveLegacyFluxLlmKbPluginAsync(
        CancellationToken cancellationToken) =>
        RunAsync(["plugin", "remove", "flux-llm-kb@flux-llm-kb-local"], false, cancellationToken);

    private async ValueTask<NativeCodexMarketplaceCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        bool captureOutput,
        CancellationToken cancellationToken,
        Func<string, string>? structuralHash = null)
    {
        if (Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Process) is not null)
            throw new NativeGoLiveContractException("codex-home-overridden");
        NativeCodexProcessOutput processOutput;
        if (_run is not null)
        {
            processOutput = await _run(arguments, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var start = NativeGoLiveChildStartBuilder.Create("codex");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                ?? throw new NativeGoLiveContractException("codex-marketplace-runner-unavailable");
            var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            processOutput = new NativeCodexProcessOutput(process.ExitCode, output);
        }
        var hash = captureOutput && processOutput.ExitCode == 0 && structuralHash is not null
            ? structuralHash(processOutput.StandardOutput)
            : new string('0', 64);
        return new NativeCodexMarketplaceCommandResult(
            processOutput.ExitCode,
            captureOutput ? processOutput.StandardOutput : string.Empty,
            hash);
    }

    private string HashUnrelatedConfiguration(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var unrelated = document.RootElement.TryGetProperty("marketplaces", out var marketplaces) &&
                            marketplaces.ValueKind == JsonValueKind.Array
                ? marketplaces.EnumerateArray()
                    .Where(item => item.ValueKind != JsonValueKind.Object ||
                                   !string.Equals(
                                       item.TryGetProperty("name", out var name) ? name.GetString() : null,
                                   _expected.MarketplaceName,
                                       StringComparison.Ordinal))
                    .Select(item => item.GetRawText())
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
                : throw new NativeGoLiveContractException("codex-marketplace-list-invalid");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\n", unrelated)))).ToLowerInvariant();
        }
        catch (JsonException)
        {
            throw new NativeGoLiveContractException("codex-marketplace-list-invalid");
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToString();
            if (output.Length + read > MaximumOutputCharacters)
                throw new NativeGoLiveContractException("codex-marketplace-output-too-large");
            output.Append(buffer, 0, read);
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
}

internal static class NativeGoLiveProcessEnvironment
{
    internal static void RemoveBootstrapFromChildEnvironment(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.Environment.Remove(NativeGoLiveSqlBootstrap.EnvironmentVariable);
    }
}
