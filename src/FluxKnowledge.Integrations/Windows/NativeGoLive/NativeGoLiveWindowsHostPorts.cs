using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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
            new NativeGoLiveWindowsOneShotAdmissionPort(_plan, ownedState, sql, bootstrap));
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
            OutlookCapture = new { Enabled = false },
            Worker = new { Enabled = false },
            NativeWorker = new { Enabled = false },
            Runtime = new
            {
                ModelRuntimeEnabled = false,
                GpuEnabled = false,
                OcrEnabled = false,
                VisionEnabled = false,
                AsrEnabled = false,
                FfmpegEnabled = false,
                NetworkParsingEnabled = false
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
    private const string AppPoolLogin = @"IIS AppPool\FluxKnowledge";
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
        var expectedAppPoolSid = ResolveAccountSid(AppPoolLogin);
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql =
            """
            SELECT CONVERT(int, SERVERPROPERTY('IsFullTextInstalled'));
            SELECT CONVERT(int, CASE WHEN p.principal_id IS NULL THEN 0 ELSE 1 END),
                   p.sid, CONVERT(int, COALESCE(IS_SRVROLEMEMBER(N'sysadmin', N'IIS AppPool\FluxKnowledge'),0))
            FROM (VALUES(0)) seed(value)
            LEFT JOIN sys.server_principals p ON p.name=N'IIS AppPool\FluxKnowledge';
            SELECT p.name,p.object_id,
                   LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),REPLACE(sm.definition,CHAR(13)+CHAR(10),CHAR(10)))),2))
            FROM sys.procedures p JOIN sys.sql_modules sm ON sm.object_id=p.object_id
            WHERE SCHEMA_NAME(p.schema_id)=N'dbo' AND p.name IN
                (N'FluxKnowledgeNativeGoLiveCreate',N'FluxKnowledgeNativeGoLiveDrop',
                 N'FluxKnowledgeNativeGoLiveManageAppPool',N'FluxKnowledgeNativeGoLiveObserveAppPool')
            ORDER BY p.name;
            SELECT p.name,prm.parameter_id,prm.name,TYPE_NAME(prm.user_type_id),prm.max_length,CONVERT(int,prm.is_output)
            FROM sys.procedures p JOIN sys.parameters prm ON prm.object_id=p.object_id
            WHERE SCHEMA_NAME(p.schema_id)=N'dbo' AND p.name IN
                (N'FluxKnowledgeNativeGoLiveCreate',N'FluxKnowledgeNativeGoLiveDrop',
                 N'FluxKnowledgeNativeGoLiveManageAppPool',N'FluxKnowledgeNativeGoLiveObserveAppPool')
            ORDER BY p.name,prm.parameter_id;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = bootstrap.ConnectTimeout };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-preflight-observation-missing");
        var fullText = reader.GetInt32(0) == 1;
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) ||
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-app-pool-observation-missing");
        var loginExists = reader.GetInt32(0) == 1;
        var loginSid = reader.IsDBNull(1) ? null : Sid(reader, 1);
        var loginSidHex = reader.IsDBNull(1) ? null : OpaqueSid(reader, 1);
        var loginSysAdmin = reader.GetInt32(2) == 1;
        var procedures = await ReadProcedureEvidenceAsync(reader, cancellationToken).ConfigureAwait(false);
        await reader.DisposeAsync().ConfigureAwait(false);
        return new NativeGoLiveSqlPreflightObservation(
            fullText,
            AppPoolLogin,
            expectedAppPoolSid,
            loginExists,
            loginSid,
            loginSidHex,
            loginSysAdmin,
            procedures);
    }

    public async ValueTask<NativeGoLiveSqlPostBootstrapObservation> ProvisionAndObserveAsync(
        NativeGoLiveSqlIdentity identity,
        NativeGoLiveSqlBootstrapConnection bootstrap,
        NativeGoLivePayloadManifest payloadManifest,
        CancellationToken cancellationToken)
    {
        if (identity != _plan.Sql) throw new NativeGoLiveContractException("sql-identity-not-canonical");
        using var dataRoot = OpenSqlStorageDirectory(_plan.Layout.SqlDataRoot, "data");
        using var logRoot = OpenSqlStorageDirectory(_plan.Layout.SqlLogRoot, "log");
        var expectedAppPoolSid = ResolveAccountSid(AppPoolLogin);
        await ExecuteCreateAsync(bootstrap, expectedAppPoolSid, cancellationToken).ConfigureAwait(false);
        await RunMigrationsAsync(bootstrap, payloadManifest, cancellationToken).ConfigureAwait(false);
        await MarkEmptyCatalogueAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        return await ObservePostBootstrapAsync(bootstrap, expectedAppPoolSid, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask DropCatalogueAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "EXEC master.dbo.FluxKnowledgeNativeGoLiveDrop @Catalogue=N'FluxKnowledge';",
            connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ExecuteCreateAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        string appPoolSid,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(bootstrap.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string sql =
                "EXEC master.dbo.FluxKnowledgeNativeGoLiveCreate " +
                "@Catalogue=N'FluxKnowledge',@DataFile=@data,@LogFile=@log,@AppPoolLogin=N'IIS AppPool\\FluxKnowledge',@AppPoolSid=@sid;";
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            command.Parameters.AddWithValue("@data", _plan.Sql.DataFilePath);
            command.Parameters.AddWithValue("@log", _plan.Sql.LogFilePath);
            command.Parameters.Add("@sid", System.Data.SqlDbType.VarBinary, 85).Value =
                new SecurityIdentifier(appPoolSid).GetBinaryForm();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            throw new NativeGoLiveContractException(
                $"sql-provisioning-create-error-{exception.Number}",
                innerException: exception);
        }
    }

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

    private async ValueTask FinalizeBootstrapAuthorityAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        string bootstrapLogin,
        string appPoolSid,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql =
            "EXEC master.dbo.FluxKnowledgeNativeGoLiveManageAppPool " +
            "@Catalogue=N'FluxKnowledge',@AppPoolLogin=N'IIS AppPool\\FluxKnowledge'," +
            "@AppPoolSid=@sid,@BootstrapLogin=@bootstrap;";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@sid", System.Data.SqlDbType.VarBinary, 85).Value =
            new SecurityIdentifier(appPoolSid).GetBinaryForm();
        command.Parameters.Add("@bootstrap", System.Data.SqlDbType.NVarChar, 128).Value = bootstrapLogin;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string> ObserveCatalogueOwnerSidAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "SELECT owner_sid FROM sys.databases WHERE name=N'FluxKnowledge';",
            connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-bootstrap-catalogue-owner-missing");
        return OpaqueSid(reader, 0);
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
            _mergedMainRoot, application, payloadManifest, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<NativeGoLiveSqlPostBootstrapObservation> ObservePostBootstrapAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        string expectedAppPoolSid,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CatalogueConnection(bootstrap));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql =
            """
            SELECT DB_NAME(),DB_ID(),owner_sid,CONVERT(int,SERVERPROPERTY('IsFullTextInstalled'))
            FROM sys.databases WHERE name=N'FluxKnowledge';
            SELECT file_id,type_desc,physical_name FROM sys.database_files ORDER BY file_id;
            SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;
            SELECT CONVERT(int,CASE WHEN EmptyCatalogueValidatedAtUtc IS NOT NULL AND ActiveIndexGenerationId IS NULL THEN 1 ELSE 0 END)
              FROM dbo.IndexState WHERE Id=1;
            SELECT COUNT_BIG(*) FROM dbo.KnowledgeItems;
            SELECT COUNT_BIG(*) FROM dbo.KnowledgeRelations;
            SELECT COUNT_BIG(*) FROM dbo.NativeOperationIntents WHERE ConsumedAtUtc IS NULL;
            SELECT COUNT_BIG(*) FROM dbo.IndexState WHERE ActiveIndexGenerationId IS NOT NULL;
            SELECT SUSER_SNAME(),SUSER_SID();
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-bootstrap-postcondition-missing");
        var name = reader.GetString(0);
        var databaseId = reader.GetInt32(1);
        var ownerSidHex = OpaqueSid(reader, 2);
        var fullText = reader.GetInt32(3) == 1;
        var files = new List<NativeGoLiveSqlDatabaseFileObservation>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            files.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        var migrations = await ReadStringResultAsync(reader, cancellationToken).ConfigureAwait(false);
        var empty = await ReadSingleIntAsync(reader, cancellationToken).ConfigureAwait(false) == 1;
        var knowledge = await ReadSingleLongAsync(reader, cancellationToken).ConfigureAwait(false);
        var edges = await ReadSingleLongAsync(reader, cancellationToken).ConfigureAwait(false);
        var pending = await ReadSingleLongAsync(reader, cancellationToken).ConfigureAwait(false);
        var generations = await ReadSingleLongAsync(reader, cancellationToken).ConfigureAwait(false);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var bootstrapName = reader.GetString(0);
        var bootstrapSid = Sid(reader, 1);
        await reader.DisposeAsync().ConfigureAwait(false);
        var bootstrapEvidence = await ObservePreflightAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        await FinalizeBootstrapAuthorityAsync(
            bootstrap, bootstrapName, expectedAppPoolSid, cancellationToken).ConfigureAwait(false);
        var appPool = await ObserveAppPoolAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        ownerSidHex = await ObserveCatalogueOwnerSidAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        return new NativeGoLiveSqlPostBootstrapObservation(
            name, databaseId, ownerSidHex, files, fullText,
            NativeGoLiveDatabaseContract.RequiredMigrations, migrations,
            empty, empty, appPool.CanConnect, AppPoolLogin, expectedAppPoolSid,
            true, appPool.LoginSid, appPool.LoginSidHex, appPool.SysAdmin,
            NativeGoLiveWindowsAclInspector.HasAnyAccess(_plan.Layout.SqlDataRoot, expectedAppPoolSid) ||
            NativeGoLiveWindowsAclInspector.HasAnyAccess(_plan.Layout.SqlLogRoot, expectedAppPoolSid),
            knowledge, edges, pending, generations,
            bootstrapEvidence.BootstrapProcedures);
    }

    private string CatalogueConnection(NativeGoLiveSqlBootstrapConnection bootstrap) =>
        new SqlConnectionStringBuilder(bootstrap.ConnectionString)
        {
            InitialCatalog = _plan.Sql.CatalogName
        }.ConnectionString;

    private async ValueTask<AppPoolSqlObservation> ObserveAppPoolAsync(
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(bootstrap.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "EXEC master.dbo.FluxKnowledgeNativeGoLiveObserveAppPool @Catalogue=N'FluxKnowledge',@AppPoolLogin=N'IIS AppPool\\FluxKnowledge';",
            connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-app-pool-principal-missing");
        var loginSid = Sid(reader, 0);
        var loginSidHex = OpaqueSid(reader, 0);
        var sysAdmin = reader.GetInt32(1) == 1;
        var canConnect = reader.GetInt32(2) == 1;
        return new AppPoolSqlObservation(loginSid, loginSidHex, sysAdmin, canConnect);
    }

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

    private static async Task<IReadOnlyList<string>> ReadStringResultAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) return [];
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<int> ReadSingleIntAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) ||
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-bootstrap-postcondition-missing");
        return reader.GetInt32(0);
    }

    private static async Task<long> ReadSingleLongAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) ||
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new NativeGoLiveContractException("sql-bootstrap-postcondition-missing");
        return reader.GetInt64(0);
    }

    private static string Sid(SqlDataReader reader, int ordinal)
        => NativeGoLiveSqlSid.WindowsAccount((byte[])reader.GetValue(ordinal));

    private static string OpaqueSid(SqlDataReader reader, int ordinal) =>
        NativeGoLiveSqlSid.CanonicalOpaqueHex((byte[])reader.GetValue(ordinal));

    private static string ResolveAccountSid(string account) =>
        ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;

    private sealed record AppPoolSqlObservation(
        string LoginSid,
        string LoginSidHex,
        bool SysAdmin,
        bool CanConnect);
}

internal static class NativeGoLiveSqlSid
{
    internal static string CanonicalOpaqueHex(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 85)
            throw new NativeGoLiveContractException("sql-opaque-sid-invalid");
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    internal static string WindowsAccount(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SecurityIdentifier(value, 0).Value;
    }
}

internal static class NativeGoLivePublishedMigrationRunner
{
    private const string ContextTypeName =
        "FluxKnowledge.Infrastructure.SqlServer.Persistence.FluxKnowledgeDbContext";

    internal static async ValueTask MigrateAsync(
        string publishedRoot,
        string applicationConnectionString,
        NativeGoLivePayloadManifest payloadManifest,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(publishedRoot, applicationConnectionString, payloadManifest);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static DbContext CreateContext(
        string publishedRoot,
        string applicationConnectionString,
        NativeGoLivePayloadManifest payloadManifest)
    {
        using var image = NativeGoLivePublishedAssemblyImage.Open(publishedRoot, payloadManifest);
        var assembly = image.LoadExact();
        var contextType = assembly.GetType(ContextTypeName, throwOnError: false, ignoreCase: false);
        if (contextType is null || !typeof(DbContext).IsAssignableFrom(contextType))
            throw new NativeGoLiveContractException("published-migration-context-missing");

        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = Activator.CreateInstance(builderType)
            ?? throw new NativeGoLiveContractException("published-migration-context-invalid");
        ConfigureSqlServer(builderType, builder, contextType, applicationConnectionString);
        var options = builderType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(property => property.Name == "Options" &&
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
            ?.GetValue(builder)
            ?? throw new NativeGoLiveContractException("published-migration-options-invalid");
        if (Activator.CreateInstance(contextType, options) is not DbContext context)
            throw new NativeGoLiveContractException("published-migration-context-invalid");
        return context;
    }

    private static void ConfigureSqlServer(
        Type builderType,
        object builder,
        Type contextType,
        string connectionString)
    {
        var method = typeof(SqlServerDbContextOptionsExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Where(candidate => candidate.Name == nameof(SqlServerDbContextOptionsExtensions.UseSqlServer) &&
                                candidate.IsGenericMethodDefinition)
            .Select(candidate => candidate.MakeGenericMethod(contextType))
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 3 && parameters[0].ParameterType == builderType &&
                       parameters[1].ParameterType == typeof(string);
            }) ?? throw new NativeGoLiveContractException("published-migration-options-invalid");
        try
        {
            method.Invoke(null, [builder, connectionString, null]);
        }
        catch (TargetInvocationException exception)
        {
            throw new NativeGoLiveContractException(
                exception.InnerException is ArgumentException
                    ? "published-migration-connection-invalid"
                    : "published-migration-options-invalid");
        }
    }
}

internal sealed class NativeGoLivePublishedAssemblyImage : IDisposable
{
    private const string AssemblyName = "FluxKnowledge.Infrastructure.SqlServer";
    private const string AssemblyFileName = AssemblyName + ".dll";
    private const long MaximumAssemblyBytes = 64L * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly string _publishedRoot;
    private int _disposed;

    private NativeGoLivePublishedAssemblyImage(FileStream stream, string publishedRoot)
    {
        _stream = stream;
        _publishedRoot = publishedRoot;
    }

    internal static NativeGoLivePublishedAssemblyImage Open(
        string publishedRoot,
        NativeGoLivePayloadManifest payloadManifest)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(publishedRoot));
        var assemblyPath = Path.Combine(root, AssemblyFileName);
        FileStream stream;
        try
        {
            stream = new FileStream(
                assemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            throw new NativeGoLiveContractException("published-migration-assembly-missing");
        }
        try
        {
            if (stream.Length is <= 0 or > MaximumAssemblyBytes)
                throw new NativeGoLiveContractException("published-migration-assembly-invalid");
            if (!NativeGoLivePayloadHasher.Same(NativeGoLivePayloadHasher.Compute(root), payloadManifest))
                throw new NativeGoLiveContractException("published-migration-manifest-mismatch");
            stream.Position = 0;
            _ = SHA256.HashData(stream);
            stream.Position = 0;
            return new NativeGoLivePublishedAssemblyImage(stream, root);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal Assembly LoadExact()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _stream.Position = 0;
        return new NativeGoLiveMigrationAssemblyLoadContext(_publishedRoot).LoadFromStream(_stream);
    }

    internal static string? ResolvePayloadDependency(string publishedRoot, AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var candidate = Path.Combine(publishedRoot, name + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stream.Dispose();
    }

    private sealed class NativeGoLiveMigrationAssemblyLoadContext(string publishedRoot)
        : AssemblyLoadContext($"NativeGoLiveMigration-{Guid.NewGuid():N}", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var existing = Default.Assemblies.FirstOrDefault(candidate =>
                System.Reflection.AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (existing is not null) return existing;
            var dependency = ResolvePayloadDependency(publishedRoot, assemblyName);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
    }
}

internal static class SecurityIdentifierExtensions
{
    internal static byte[] GetBinaryForm(this SecurityIdentifier sid)
    {
        var result = new byte[sid.BinaryLength];
        sid.GetBinaryForm(result, 0);
        return result;
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

        foreach (var path in new[]
                 {
                     layout.Root, layout.DataRoot, layout.SqlRoot, layout.RuntimeRoot, layout.CodexPluginRoot
                 })
            ApplyDirectoryAcl(path);
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
        return ValueTask.FromResult(new NativeGoLiveAclObservation(
            sqlWrite,
            NativeGoLiveWindowsAclInspector.HasRights(layout.ApplicationRoot, appPoolSid.Value, FileSystemRights.ReadAndExecute)
                ? [layout.ApplicationRoot] : [],
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
        using var directory = _fileSystem.OpenDirectory(path);
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
        return Observation(state, identity);
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
            if (!document.RootElement.TryGetProperty("marketplaces", out var marketplaces) ||
                marketplaces.ValueKind != JsonValueKind.Array)
                return (CodexMarketplaceLifecycleState.Unavailable, null);
            var matching = marketplaces.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object &&
                                StringProperty(entry, "name") == identity.MarketplaceName)
                .ToArray();
            if (matching.Length == 0) return (CodexMarketplaceLifecycleState.Missing, null);
            if (matching.Length != 1 ||
                !matching[0].TryGetProperty("source", out var source) ||
                StringProperty(source, "source") != "local")
                return (CodexMarketplaceLifecycleState.Foreign, null);
            var path = StringProperty(source, "path");
            return SamePath(path, identity.MarketplaceRoot)
                ? (CodexMarketplaceLifecycleState.Registered, path)
                : (CodexMarketplaceLifecycleState.Foreign, path);
        }
        catch (JsonException)
        {
            return (CodexMarketplaceLifecycleState.Unavailable, null);
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool SamePath(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class NativeGoLiveCodexProcessRunner(NativeGoLiveCodexIdentity expected)
    : INativeCodexMarketplaceCommandRunner
{
    private const int MaximumOutputCharacters = 256 * 1024;

    public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
        string marketplaceRoot,
        CancellationToken cancellationToken)
    {
        if (!SamePath(marketplaceRoot, expected.MarketplaceRoot))
            throw new NativeGoLiveContractException("marketplace-state-foreign");
        return RunAsync(["plugin", "marketplace", "add", marketplaceRoot], captureOutput: false, cancellationToken);
    }

    public ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
        CancellationToken cancellationToken) =>
        RunAsync(["plugin", "marketplace", "list", "--json"], captureOutput: true, cancellationToken);

    private async ValueTask<NativeCodexMarketplaceCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        bool captureOutput,
        CancellationToken cancellationToken)
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
        var structuralHash = captureOutput
            ? HashUnrelatedConfiguration(output)
            : new string('0', 64);
        return new NativeCodexMarketplaceCommandResult(
            process.ExitCode,
            captureOutput ? output : string.Empty,
            structuralHash);
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
                                       expected.MarketplaceName,
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
