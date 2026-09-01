using System.Security.Cryptography;
using System.Net;
using System.Text;
using FluxKnowledge.Application.Operations;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal sealed class NativeGoLiveContractException : InvalidOperationException
{
    internal NativeGoLiveContractException(
        string reasonCode,
        string? diagnosticDetail = null,
        Exception? innerException = null) : base(reasonCode, innerException)
    {
        ReasonCode = reasonCode;
        DiagnosticDetail = diagnosticDetail;
    }

    internal string ReasonCode { get; }
    internal string? DiagnosticDetail { get; }
}

/// <summary>
/// Opaque, in-memory one-shot capability. It contains no public serialisable state and is recognised
/// only by the issuer instance that created it.
/// </summary>
internal sealed class NativeGoLiveCloseoutCapability
{
    private const int Issued = 0;
    private const int Execution = 1;
    private const int Failed = 2;
    private const int Completed = 3;
    private int _executionState;
    internal NativeGoLiveCloseoutCapability(
        Guid nonce,
        NativeGoLivePlan plan,
        string mergedMainRoot,
        string payloadSha256,
        NativeGoLivePayloadManifest payloadManifest,
        DateTimeOffset expiresAtUtc)
    {
        Nonce = nonce;
        Plan = plan;
        MergedMainRoot = mergedMainRoot;
        PayloadSha256 = payloadSha256;
        PayloadManifest = payloadManifest;
        ExpiresAtUtc = expiresAtUtc;
    }

    internal Guid Nonce { get; }
    internal NativeGoLivePlan Plan { get; }
    internal string MergedMainRoot { get; }
    internal string PayloadSha256 { get; }
    internal NativeGoLivePayloadManifest PayloadManifest { get; }
    internal DateTimeOffset ExpiresAtUtc { get; }
    internal bool IsConsumedForExecution => Volatile.Read(ref _executionState) == Execution;
    internal bool TryBeginExecution() =>
        Interlocked.CompareExchange(ref _executionState, Execution, Issued) == Issued;
    internal void MarkFailed() => Interlocked.Exchange(ref _executionState, Failed);
    internal void MarkCompleted() => Interlocked.Exchange(ref _executionState, Completed);

}

internal sealed class NativeGoLiveCloseoutCapabilityIssuer(TimeProvider? clock = null)
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, NativeGoLiveCloseoutCapability> _issued = [];

    /// <summary>Issues a one-shot non-privileged intent after merged-main verification.</summary>
    internal NativeGoLiveCloseoutCapability Issue(
        NativeGoLivePlan plan,
        string mergedMainRoot,
        string payloadSha256) => Issue(plan, mergedMainRoot, payloadSha256, TimeSpan.FromMinutes(30));

    internal NativeGoLiveCloseoutCapability Issue(
        NativeGoLivePlan plan,
        string mergedMainRoot,
        string payloadSha256,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonicalRoot = CanonicalPath(mergedMainRoot, "merged-main-root-not-canonical");
        RequireSha256(payloadSha256, "merged-main-payload-hash-invalid");
        var payloadManifest = NativeGoLivePayloadHasher.Compute(canonicalRoot);
        if (!string.Equals(payloadSha256, payloadManifest.Sha256, StringComparison.Ordinal))
            throw new NativeGoLiveContractException("merged-main-payload-hash-invalid");
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
        {
            throw new NativeGoLiveContractException("go-live-closeout-capability-lifetime-invalid");
        }

        var now = _clock.GetUtcNow();
        var expires = now.Add(lifetime);

        var capability = new NativeGoLiveCloseoutCapability(
            Guid.NewGuid(), plan, canonicalRoot, payloadSha256, payloadManifest, expires);
        lock (_gate) _issued.Add(capability.Nonce, capability);
        return capability;
    }

    internal bool TryConsume(
        NativeGoLiveCloseoutCapability? capability,
        NativeGoLiveRequest request,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (capability is null ||
                !_issued.TryGetValue(capability.Nonce, out var issued) ||
                !ReferenceEquals(issued, capability))
            {
                reason = "go-live-closeout-capability-unrecognised";
                return false;
            }
            if (_clock.GetUtcNow() >= capability.ExpiresAtUtc)
            {
                capability.MarkFailed();
                reason = "go-live-closeout-capability-expired";
                return false;
            }
            if (!ReferenceEquals(request.Plan, capability.Plan) ||
                !SamePath(request.MergedMainRoot, capability.MergedMainRoot) ||
                !string.Equals(request.MergedMainPayloadSha256, capability.PayloadSha256, StringComparison.Ordinal) ||
                !NativeGoLivePayloadHasher.Same(request.MergedMainPayloadManifest, capability.PayloadManifest))
            {
                reason = "go-live-closeout-capability-binding-mismatch";
                return false;
            }

            if (capability.TryBeginExecution())
            {
                reason = null;
                return true;
            }
            reason = "go-live-closeout-capability-consumed";
            return false;
        }
    }

    internal void Complete(NativeGoLiveCloseoutCapability capability, bool succeeded)
    {
        if (succeeded)
        {
            capability.MarkCompleted();
            return;
        }

        capability.MarkFailed();
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(CanonicalPath(left, string.Empty), right, StringComparison.OrdinalIgnoreCase);
        }
        catch (NativeGoLiveContractException)
        {
            return false;
        }
    }

    private static string CanonicalPath(string path, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException();
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new NativeGoLiveContractException(reason);
        }
    }

    private static void RequireSha256(string value, string reason)
    {
        if (value is not { Length: 64 } || !value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new NativeGoLiveContractException(reason);
        }
    }
}

internal sealed class NativeGoLiveCloseoutExecutor(
    NativeGoLiveCloseoutCapabilityIssuer capabilityIssuer,
    NativeGoLiveExecutor? executor = null)
{
    private readonly NativeGoLiveExecutor _executor = executor ?? new NativeGoLiveExecutor();

    internal async Task<NativeGoLiveResult> ExecuteAsync(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLiveRequest request,
        INativeGoLiveHost host,
        CancellationToken cancellationToken = default)
    {
        if (!capabilityIssuer.TryConsume(capability, request, out var reason))
        {
            return NativeGoLiveResult.Refused(reason!);
        }

        try
        {
            var result = await _executor.ExecuteAsync(request, host, cancellationToken).ConfigureAwait(false);
            capabilityIssuer.Complete(capability, result.Succeeded);
            return result;
        }
        catch
        {
            capabilityIssuer.Complete(capability, succeeded: false);
            throw;
        }
        finally
        {
            NativeGoLiveSqlBootstrap.ClearProcessEnvironment();
        }
    }
}

/// <summary>Reflection boundary used only by the private PowerShell closeout module.</summary>
internal static class NativeGoLiveCloseoutBridge
{
    internal static Task<NativeGoLiveResult> ExecuteAsync(
        object capabilityIssuer,
        object capability,
        object request,
        object host,
        CancellationToken cancellationToken = default)
    {
        if (capabilityIssuer is not NativeGoLiveCloseoutCapabilityIssuer typedIssuer ||
            capability is not NativeGoLiveCloseoutCapability typedCapability)
            throw new NativeGoLiveContractException("go-live-closeout-capability-unrecognised");
        if (request is not NativeGoLiveRequest typedRequest)
            throw new NativeGoLiveContractException("go-live-request-unrecognised");
        if (host is not GuardedNativeGoLiveHost typedHost)
            throw new NativeGoLiveContractException("go-live-host-not-guarded");

        return new NativeGoLiveCloseoutExecutor(typedIssuer)
            .ExecuteAsync(typedCapability, typedRequest, typedHost, cancellationToken);
    }
}

internal sealed class NativeGoLiveSqlBootstrapConnection
{
    internal NativeGoLiveSqlBootstrapConnection(
        string connectionString,
        string dataSource,
        string initialCatalog,
        bool integratedSecurity,
        int connectTimeout)
    {
        ConnectionString = connectionString;
        DataSource = dataSource;
        InitialCatalog = initialCatalog;
        IntegratedSecurity = integratedSecurity;
        ConnectTimeout = connectTimeout;
    }

    internal string ConnectionString { get; }
    internal string DataSource { get; }
    internal string InitialCatalog { get; }
    internal bool IntegratedSecurity { get; }
    internal int ConnectTimeout { get; }
}

internal static class NativeGoLiveSqlBootstrap
{
    internal const string EnvironmentVariable = "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP";
    private static readonly HashSet<string> AllowedRawKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data Source", "Initial Catalog", "Integrated Security", "Encrypt",
        "Trust Server Certificate", "Connect Timeout", "Connect Retry Count",
        "Pooling", "Application Name"
    };

    internal static NativeGoLiveSqlBootstrapConnection Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) ||
            connectionString.Length > 1024 ||
            connectionString.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new NativeGoLiveContractException("sql-bootstrap-malformed");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new NativeGoLiveContractException("sql-bootstrap-malformed");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ReadRawKeys(connectionString))
        {
            if (!AllowedRawKeys.Contains(key))
            {
                throw new NativeGoLiveContractException("sql-bootstrap-option-not-allowed");
            }
            if (!seen.Add(key))
            {
                throw new NativeGoLiveContractException("sql-bootstrap-conflicting-option");
            }
        }

        if (!seen.SetEquals(AllowedRawKeys))
        {
            throw new NativeGoLiveContractException("sql-bootstrap-required-option-missing");
        }

        if (!string.Equals(builder.DataSource, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase) ||
            !builder.IntegratedSecurity ||
            !string.IsNullOrEmpty(builder.UserID) ||
            !string.IsNullOrEmpty(builder.Password) ||
            !string.IsNullOrEmpty(builder.AttachDBFilename) ||
            builder.UserInstance ||
            !string.IsNullOrEmpty(builder.FailoverPartner) ||
            builder.MultiSubnetFailover ||
            builder.Encrypt != SqlConnectionEncryptOption.Mandatory ||
            !builder.TrustServerCertificate ||
            builder.ConnectTimeout != 5 ||
            builder.ConnectRetryCount != 0 ||
            builder.Pooling ||
            !string.Equals(builder.ApplicationName, "FluxKnowledge.NativeGoLive", StringComparison.Ordinal))
        {
            throw new NativeGoLiveContractException("sql-bootstrap-not-local-integrated");
        }

        return new NativeGoLiveSqlBootstrapConnection(
            builder.ConnectionString,
            builder.DataSource,
            builder.InitialCatalog,
            builder.IntegratedSecurity,
            builder.ConnectTimeout);
    }

    internal static void ClearProcessEnvironment() =>
        Environment.SetEnvironmentVariable(EnvironmentVariable, null, EnvironmentVariableTarget.Process);

    private static IReadOnlyList<string> ReadRawKeys(string connectionString)
    {
        var keys = new List<string>();
        var index = 0;
        while (index < connectionString.Length)
        {
            while (index < connectionString.Length &&
                   (connectionString[index] == ';' || char.IsWhiteSpace(connectionString[index]))) index++;
            if (index == connectionString.Length) break;
            var equals = connectionString.IndexOf('=', index);
            if (equals < 0) throw new NativeGoLiveContractException("sql-bootstrap-malformed");
            var key = connectionString[index..equals].Trim();
            if (key.Length == 0) throw new NativeGoLiveContractException("sql-bootstrap-malformed");
            keys.Add(key);
            index = equals + 1;

            char quote = '\0';
            var braces = false;
            for (; index < connectionString.Length; index++)
            {
                var character = connectionString[index];
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        if (index + 1 < connectionString.Length && connectionString[index + 1] == quote) index++;
                        else quote = '\0';
                    }
                    continue;
                }
                if (braces)
                {
                    if (character == '}' && index + 1 < connectionString.Length && connectionString[index + 1] == '}') index++;
                    else if (character == '}') braces = false;
                    continue;
                }
                if (character is '\'' or '"') quote = character;
                else if (character == '{') braces = true;
                else if (character == ';') { index++; break; }
            }
            if (quote != '\0' || braces) throw new NativeGoLiveContractException("sql-bootstrap-malformed");
        }
        return keys;
    }
}

internal static class NativeGoLivePayloadHasher
{
    private const int MaximumFiles = 25_000;
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;

    internal static NativeGoLivePayloadManifest Compute(string root)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootInfo = new DirectoryInfo(canonicalRoot);
        if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new NativeGoLiveContractException("merged-main-root-not-canonical");
        }

        var pending = new Stack<DirectoryInfo>();
        var collected = new List<FileInfo>();
        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(canonicalRoot, entry.FullName).Replace('\\', '/');
                if (string.Equals(relative, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                entry.Refresh();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new NativeGoLiveContractException("merged-main-payload-reparse-point");
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file) collected.Add(file);
            }
        }

        var files = collected
            .OrderBy(file => Path.GetRelativePath(canonicalRoot, file.FullName).Replace('\\', '/'), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0 || files.Length > MaximumFiles)
        {
            throw new NativeGoLiveContractException("merged-main-payload-file-count-invalid");
        }

        long totalBytes = 0;
        foreach (var file in files)
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new NativeGoLiveContractException("merged-main-payload-reparse-point");
            }
            totalBytes = checked(totalBytes + file.Length);
            if (totalBytes > MaximumBytes) throw new NativeGoLiveContractException("merged-main-payload-too-large");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("native-go-live-payload-v2"));
        AppendUInt32(hash, checked((uint)files.Length));
        AppendUInt64(hash, checked((ulong)totalBytes));
        var manifestFiles = new List<NativeGoLivePayloadFile>(files.Length);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(canonicalRoot, file.FullName).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative);
            AppendUInt32(hash, checked((uint)relativeBytes.Length));
            hash.AppendData(relativeBytes);
            AppendUInt64(hash, checked((ulong)file.Length));
            using var stream = new FileStream(
                file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
            manifestFiles.Add(new NativeGoLivePayloadFile(relative, file.Length));
        }

        return new NativeGoLivePayloadManifest(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            files.Length,
            totalBytes,
            manifestFiles);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    internal static bool Same(NativeGoLivePayloadManifest? left, NativeGoLivePayloadManifest? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal) &&
        left.FileCount == right.FileCount &&
        left.TotalBytes == right.TotalBytes &&
        left.Files.SequenceEqual(right.Files);
}

internal sealed record NativeGoLiveIisBinding(string Protocol, string Address, int Port, string Host);
internal sealed record NativeGoLiveIisObservation(
    string SiteName,
    string AppPoolName,
    string PhysicalPath,
    bool AnonymousAuthentication,
    bool WindowsAuthentication,
    IReadOnlyList<NativeGoLiveIisBinding> Bindings);
internal sealed record NativeGoLivePathAncestorObservation(
    string Path,
    string ResolvedPath,
    string VolumeId,
    bool Exists,
    bool IsReparsePoint);
internal sealed record NativeGoLiveRootObservation(
    string Root,
    bool Exists,
    bool HasReparsePoint,
    string CommittedSha,
    string PlanHash,
    IReadOnlyList<NativeGoLivePathAncestorObservation> Ancestors);
internal sealed record NativeGoLiveRuntimeObservation(
    bool OutlookEnabled, bool PhaseSixEnabled, bool ModelRuntimeEnabled,
    bool GpuEnabled, bool FfmpegEnabled, bool NetworkParsingEnabled);
internal sealed record NativeGoLiveMarketplaceObservation(string State, string Name, string Root, string PluginName);
internal sealed record NativeGoLiveSqlPreflightObservation(
    bool FullTextInstalled,
    string AppPoolLoginName,
    string ExpectedAppPoolSid,
    bool AppPoolLoginExists,
    string? AppPoolLoginSid,
    string? AppPoolLoginSidHex,
    bool AppPoolLoginIsSysAdmin,
    IReadOnlyList<NativeGoLiveSqlProcedureObservation>? BootstrapProcedures);
internal sealed record NativeGoLiveSqlProcedureObservation(
    string Name,
    int ObjectId,
    string DefinitionSha256,
    IReadOnlyList<NativeGoLiveSqlParameterObservation> Parameters);
internal sealed record NativeGoLiveSqlParameterObservation(
    string Name,
    string TypeName,
    short MaximumLength,
    bool IsOutput);
internal static partial class NativeGoLiveSqlBootstrapAuthorityContract
{
    internal static bool IsValidProcedureSet(IReadOnlyList<NativeGoLiveSqlProcedureObservation>? procedures)
    {
        if (procedures is null || procedures.Count != 4) return false;
        var observedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var procedure in procedures)
        {
            string expectedHash;
            try { expectedHash = DefinitionSha256(procedure.Name); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (!observedNames.Add(procedure.Name) ||
                !string.Equals(procedure.DefinitionSha256, expectedHash, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
internal sealed record NativeGoLiveSqlDatabaseFileObservation(
    int FileId,
    string TypeDescription,
    string PhysicalPath);
internal sealed record NativeGoLiveSqlPostBootstrapObservation(
    string CatalogueName,
    int CatalogueDatabaseId,
    string CatalogueOwnerSidHex,
    IReadOnlyList<NativeGoLiveSqlDatabaseFileObservation> DatabaseFiles,
    bool FullTextInstalled,
    IReadOnlyList<string> ExpectedMigrations,
    IReadOnlyList<string> AppliedMigrations,
    bool EmptyMarkerDurable,
    bool EmptyReadinessProved,
    bool AppPoolCanConnect,
    string AppPoolLoginName,
    string ExpectedAppPoolSid,
    bool AppPoolLoginExists,
    string AppPoolLoginSid,
    string AppPoolLoginSidHex,
    bool AppPoolLoginIsSysAdmin,
    bool AppPoolHasSqlFileAccess,
    long KnowledgeItems,
    long Edges,
    long PendingOperations,
    long ActiveIndexGenerations,
    IReadOnlyList<NativeGoLiveSqlProcedureObservation>? BootstrapProcedures);
internal sealed record NativeGoLiveAclObservation(
    IReadOnlyList<string> SqlServiceWriteRoots,
    IReadOnlyList<string> AppPoolReadExecuteRoots,
    IReadOnlyList<string> AppPoolReadRoots,
    IReadOnlyList<string> AppPoolModifyRoots,
    bool AppPoolSqlFileAccess,
    bool AppPoolAppWriteAccess,
    bool AppPoolConfigWriteAccess,
    bool AppPoolRecoveryAccess,
    bool AppPoolDataProtectionModifyOnly,
    string AppPoolSid,
    string SqlServiceSid,
    IReadOnlyList<NativeGoLiveAclPathObservation>? Paths);
internal sealed record NativeGoLiveAclPathObservation(
    string Path,
    bool IsProtected,
    IReadOnlyList<NativeGoLiveAclAceObservation> Rules);
internal sealed record NativeGoLiveAclAceObservation(
    string Sid,
    long Rights,
    bool Allow,
    bool Inherited,
    int InheritanceFlags,
    int PropagationFlags,
    bool AppliesToSelf,
    bool AppliesToChildContainers,
    bool AppliesToChildObjects);
internal sealed record NativeGoLivePoolObservation(
    string AppPoolName,
    bool WasRunning,
    string State,
    bool OperationSucceeded);
internal sealed record NativeGoLiveHttpPeer(string LocalAddress, string RemoteAddress);
internal sealed record NativeGoLiveHttpObservation(
    string Method,
    string Uri,
    int StatusCode,
    NativeGoLiveHttpPeer Peer,
    IReadOnlyList<string> SentHeaderNames,
    bool HasNativeProofMarker);
internal sealed record NativeGoLiveGpuObservation(
    NativeGoLiveHttpObservation Http, int Ready, int Active, int Deferred, int Uncertain, string? ActiveBatch);
internal sealed record NativeGoLiveSearchObservation(
    NativeGoLiveHttpObservation Http, bool Succeeded, string Query, int Limit, int ResultCount);
internal sealed record NativeGoLiveMcpObservation(
    NativeGoLiveHttpObservation Initialise, NativeGoLiveHttpObservation ToolsList, IReadOnlyList<string> Tools);
/// <summary>
/// Count-only evidence that no active local non-loopback IPv4 address served the native health contract.
/// A TCP handshake alone is not evidence of application exposure: HTTP.sys can complete it for a loopback
/// binding. Address values are deliberately not retained or exposed by the live-validation contract.
/// </summary>
internal sealed record NativeGoLiveTcpDenialObservation(
    bool CandidateEnumerationComplete,
    int CandidateCount,
    int ConnectionRefusedCount,
    int ConnectionSucceededCount,
    int IndeterminateCount);
internal sealed record NativeGoLiveLoopbackObservation(
    NativeGoLiveHttpObservation HealthLive,
    NativeGoLiveHttpObservation HealthReady,
    NativeGoLiveHttpObservation IndexHealth,
    NativeGoLiveGpuObservation Gpu,
    NativeGoLiveSearchObservation Search,
    NativeGoLiveMcpObservation Mcp,
    NativeGoLiveHttpObservation ForwardedDenial,
    NativeGoLiveTcpDenialObservation NonLoopbackDenial,
    bool UsedProxy,
    bool FollowedRedirect,
    NativeGoLiveRuntimeObservation EffectiveRuntime);
internal sealed record NativeGoLivePreflightObservation(
    NativeGoLiveIisObservation Iis,
    NativeGoLiveRootObservation Root,
    NativeGoLiveSqlPreflightObservation Sql,
    NativeGoLiveRuntimeObservation Runtime,
    NativeGoLiveMarketplaceObservation Marketplace,
    NativeGoLiveVssPreflightObservation Vss);

internal static class NativeGoLiveDatabaseContract
{
    internal static readonly string[] RequiredMigrations =
    [
        "20260726215521_InitialPhase1",
        "20260726221653_EnforceCanonicalSqlSafety",
        "20260726235718_AddIndexGenerationMembership",
        "20260727055755_DistinguishVectorIdentityAndPayloadChecksum",
        "20260729080641_AddGpuSchedulerDurability",
        "20260729094809_AddGpuSchedulerOperationReceipts",
        "20260729103104_CompleteGpuSchedulerOperationReceipts",
        "20260729120305_AddGpuSchedulerOperationReceiptRequestFingerprint",
        "20260802182703_AddGpuSchedulerBinaryFenceCollation",
        "20260802191240_AddGpuSchedulerOpaqueKeyCanonicality",
        "20260805112341_AddGpuExecutorDispatchAndReceipts",
        "20260806120000_AddPhase3ALocalSources",
        "20260808191700_AddRetainedTextPipelineLink",
        "20260809110000_AddPhase3BWatcherCorpusEvents",
        "20260810185641_AddNativeWorkerSupervision",
        "20260811093501_AddNativeOutlookIngress",
        "20260811094729_HardenNativeOutlookIngress",
        "20260811100247_FixOutlookPrivateIdentityColumns",
        "20260811101550_EnforceOutlookCaptureIdentityFences",
        "20260811105928_HardenOutlookCaptureReplay",
        "20260811112742_BindOutlookExportClaimIdentity",
        "20260811132655_BindOutlookProfileSourceRoot",
        "20260811133300_AlignDeferredCapabilityFingerprintCollation",
        "20260811143122_RecordOutlookExportBlockedReason",
        "20260811152249_AllowIdentitylessBlockedOutlookExports",
        "20260812102333_AddOutlookBrowseTargetPath",
        "20260813103233_AddRetainedZipProcessorBranches",
        "20260813125157_AddRetainedProcessorBranchMemberChildForeignKeys",
        "20260814144818_AddSourceProcessorForceRequests",
        "20260814161559_AddOperatorActionCapabilityFoundation",
        "20260814162746_EnforceOperatorActionCapabilityInvariants",
        "20260814170852_EnforceOperatorActionRequestPolicies",
        "20260820062157_AddRetainedCsharpCodeFacts",
        "20260820070404_HardenRetainedCsharpLifecycle",
        "20260820101021_CloseRetainedCsharpMixedOutcomes",
        "20260825093830_AddNativeV1OperationLedger",
        "20260825095932_AddNativeKnowledgeGraph",
        "20260825100839_AddNativeKnowledgeSafeSearchProjection",
        "20260826160702_AddEmptyCatalogueReadiness"
    ];
}

internal interface INativeGoLiveOneShotPreflightPort
{
    ValueTask<NativeGoLivePreflightObservation> ObserveAsync(
        NativeGoLivePlan expectedPlan,
        NativeGoLiveSqlBootstrapConnection bootstrap,
        CancellationToken cancellationToken);
}
internal sealed record NativeGoLiveOneShotAdmission(
    bool RootExists,
    bool CatalogueExists)
{
    internal bool IsAbsent => !RootExists && !CatalogueExists;
}
internal interface INativeGoLiveAdmissionPort
{
    ValueTask<NativeGoLiveOneShotAdmission> ObserveAsync(CancellationToken cancellationToken);
    ValueTask WipeAsync(CancellationToken cancellationToken);
}
internal interface INativeGoLiveIisPort
{
    /// <summary>Replaces only the fixed app-owned IIS site and pool, then returns its canonical observation.</summary>
    NativeGoLiveIisObservation ReplaceCanonical(NativeGoLivePlan plan) =>
        throw new NativeGoLiveContractException("iis-canonical-replacement-not-supported");
    ValueTask<NativeGoLivePoolObservation> StopAsync(string appPoolName, CancellationToken cancellationToken);
    ValueTask<NativeGoLivePoolObservation> RestoreAsync(string appPoolName, CancellationToken cancellationToken);
    ValueTask<NativeGoLivePoolObservation> StartAsync(string appPoolName, CancellationToken cancellationToken);
}
internal interface INativeGoLiveOwnedStatePort
{
    ValueTask WipeRootAsync(CancellationToken cancellationToken);
    ValueTask CreateEmptyRootAsync(CancellationToken cancellationToken);
    ValueTask WriteProductionConfigurationAsync(CancellationToken cancellationToken);
}
internal interface INativeGoLiveSqlPort
{
    ValueTask<NativeGoLiveSqlPostBootstrapObservation> ProvisionAndObserveAsync(
        NativeGoLiveSqlIdentity identity,
        NativeGoLiveSqlBootstrapConnection bootstrap,
        NativeGoLivePayloadManifest payloadManifest,
        CancellationToken cancellationToken);
}
internal interface INativeGoLiveAclPort
{
    ValueTask<NativeGoLiveAclObservation> ApplyAndObserveAsync(NativeGoLivePlan expectedPlan, CancellationToken cancellationToken);
    ValueTask<NativeGoLiveAclObservation> ObserveEffectiveAsync(NativeGoLivePlan expectedPlan, CancellationToken cancellationToken);
}
internal interface INativeGoLivePublishPort
{
    ValueTask PublishAsync(string mergedMainRoot, string applicationRoot, CancellationToken cancellationToken);
}
internal interface INativeGoLiveCompositionPort
{
    ValueTask ValidatePublishedCompositionAsync(NativeGoLivePlan plan, CancellationToken cancellationToken);
}
internal interface INativeGoLiveLoopbackPort
{
    ValueTask<NativeGoLiveLoopbackObservation> ObserveAsync(CancellationToken cancellationToken);
}
internal interface INativeGoLiveMarketplacePort
{
    ValueTask<NativeGoLiveMarketplaceObservation> RegisterAndObserveAsync(
        NativeGoLiveCodexIdentity identity, CancellationToken cancellationToken);
}
internal interface INativeGoLiveVssPort
{
    NativeGoLiveVssPreflightObservation Query(CancellationToken cancellationToken);
    NativeGoLiveVssMutationObservation Ensure(
        NativeGoLiveVssPolicy policy,
        NativeGoLiveVssPreflightObservation expected,
        CancellationToken cancellationToken);
}

internal sealed record NativeGoLiveHostPorts(
    INativeGoLiveOneShotPreflightPort OneShotPreflight,
    INativeGoLiveIisPort Iis,
    INativeGoLiveOwnedStatePort OwnedState,
    INativeGoLiveSqlPort Sql,
    INativeGoLiveAclPort Acls,
    INativeGoLivePublishPort Publish,
    INativeGoLiveLoopbackPort Loopback,
    INativeGoLiveMarketplacePort Marketplace,
    INativeGoLiveVssPort Vss,
    INativeGoLiveCompositionPort? Composition = null,
    INativeGoLiveAdmissionPort? Admission = null);

/// <summary>
/// Holds the sole machine-wide exclusion for a currently active one-shot invocation.  The named
/// operating-system semaphore exists only while a process holds an open handle; it stores no
/// deployment state and provides no recovery or cross-run authority.
/// </summary>
internal sealed class NativeGoLiveMachineWideLease : IAsyncDisposable
{
    private const string SemaphoreName = @"Global\FluxKnowledge.NativeGoLive.OneShot";
    private Semaphore? _semaphore;

    private NativeGoLiveMachineWideLease(Semaphore semaphore) => _semaphore = semaphore;

    internal static NativeGoLiveMachineWideLease Acquire()
    {
        Semaphore? semaphore = null;
        try
        {
            semaphore = new Semaphore(1, 1, SemaphoreName, out _);
            if (!semaphore.WaitOne(0))
                throw new NativeGoLiveLeaseUnavailableException();
            return new NativeGoLiveMachineWideLease(semaphore);
        }
        catch (NativeGoLiveLeaseUnavailableException)
        {
            semaphore?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                          WaitHandleCannotBeOpenedException)
        {
            semaphore?.Dispose();
            throw new NativeGoLiveLeaseUnavailableException();
        }
    }

    public ValueTask DisposeAsync()
    {
        var semaphore = Interlocked.Exchange(ref _semaphore, null);
        if (semaphore is null) return ValueTask.CompletedTask;
        try
        {
            semaphore.Release();
        }
        finally
        {
            semaphore.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Concrete Task 2 host; all sequencing remains in <see cref="NativeGoLiveExecutor"/>.</summary>
internal sealed class GuardedNativeGoLiveHost : INativeGoLiveHost
{
    private readonly NativeGoLiveCloseoutCapability _capability;
    private readonly NativeGoLivePlan _plan;
    private readonly string _mergedMainRoot;
    private readonly string _applicationRoot;
    private readonly Func<NativeGoLiveCloseoutCapability, NativeGoLiveSqlBootstrapConnection, NativeGoLiveHostPorts> _bindPorts;
    private readonly NativeGoLiveSqlBootstrapConnection? _testBootstrap;
    private readonly Func<string, CancellationToken, Task>? _bootstrapInstaller;
    private NativeGoLiveSqlBootstrapConnection _bootstrap = null!;
    private NativeGoLiveHostPorts _ports = null!;
    private int _leaseState;
    private NativeGoLiveVssPreflightObservation? _preflightVss;

    internal GuardedNativeGoLiveHost(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLivePlan plan,
        string mergedMainRoot,
        NativeGoLiveSqlBootstrapConnection bootstrap,
        NativeGoLiveHostPorts ports,
        Func<string, CancellationToken, Task>? bootstrapInstaller = null)
        : this(capability, plan, mergedMainRoot, (_, _) => ports, bootstrap, bootstrapInstaller)
    {
    }

    /// <summary>Production construction remains unbound until one-shot execution consumes its closeout capability.</summary>
    internal GuardedNativeGoLiveHost(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLivePlan plan,
        string mergedMainRoot,
        NativeGoLiveProductionPortFactory productionFactory,
        Func<string, CancellationToken, Task> bootstrapInstaller)
        : this(capability, plan, mergedMainRoot, productionFactory.Bind, null, bootstrapInstaller)
    {
    }

    private GuardedNativeGoLiveHost(
        NativeGoLiveCloseoutCapability capability,
        NativeGoLivePlan plan,
        string mergedMainRoot,
        Func<NativeGoLiveCloseoutCapability, NativeGoLiveSqlBootstrapConnection, NativeGoLiveHostPorts> bindPorts,
        NativeGoLiveSqlBootstrapConnection? testBootstrap,
        Func<string, CancellationToken, Task>? bootstrapInstaller)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bindPorts);
        if (!ReferenceEquals(capability.Plan, plan) ||
            !SamePath(capability.MergedMainRoot, mergedMainRoot))
        {
            throw new NativeGoLiveContractException("go-live-closeout-capability-binding-mismatch");
        }
        _capability = capability;
        _plan = plan;
        _mergedMainRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mergedMainRoot));
        _applicationRoot = plan.Layout.ApplicationRoot;
        _bindPorts = bindPorts;
        _testBootstrap = testBootstrap;
        _bootstrapInstaller = bootstrapInstaller;
    }

    public async ValueTask<INativeGoLiveLease> AcquireLeaseAsync(
        NativeGoLiveRequest request, CancellationToken cancellationToken)
    {
        if (!_capability.IsConsumedForExecution ||
            !ReferenceEquals(request.Plan, _capability.Plan) ||
            !SamePath(request.MergedMainRoot, _capability.MergedMainRoot) ||
            !string.Equals(request.MergedMainPayloadSha256, _capability.PayloadSha256, StringComparison.Ordinal) ||
            !NativeGoLivePayloadHasher.Same(request.MergedMainPayloadManifest, _capability.PayloadManifest))
            throw new NativeGoLiveContractException("go-live-closeout-capability-not-consumed");
        if (Interlocked.CompareExchange(ref _leaseState, 1, 0) != 0)
            throw new NativeGoLiveLeaseUnavailableException();
        NativeGoLiveMachineWideLease? machineWideLease = null;
        try
        {
            machineWideLease = NativeGoLiveMachineWideLease.Acquire();
            _bootstrap = ParseAndClearBootstrap();
            _ports = _bindPorts(_capability, _bootstrap);
            return new HostLease(this, machineWideLease);
        }
        catch
        {
            if (machineWideLease is not null)
                await machineWideLease.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _leaseState, 0);
            throw;
        }
    }

    public async ValueTask AdmitAndWipeAsync(
        NativeGoLiveRequest request,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(request.Plan, _plan) || !_capability.IsConsumedForExecution)
            throw new NativeGoLiveContractException("go-live-closeout-capability-not-consumed");

        var admission = _ports.Admission ??
            throw new NativeGoLiveContractException("go-live-one-shot-admission-not-supported");
        var observed = await admission.ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (observed.IsAbsent) return;
        if (!request.ConfirmCleanSlate)
            throw new NativeGoLiveContractException("go-live-wipe-confirmation-required");

        await admission.WipeAsync(cancellationToken).ConfigureAwait(false);
        var afterWipe = await admission.ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (!afterWipe.IsAbsent)
            throw new NativeGoLiveContractException("go-live-wipe-not-proved");
    }

    private NativeGoLiveSqlBootstrapConnection ParseAndClearBootstrap()
    {
        try
        {
            return _testBootstrap ?? NativeGoLiveSqlBootstrap.Parse(Environment.GetEnvironmentVariable(
                NativeGoLiveSqlBootstrap.EnvironmentVariable,
                EnvironmentVariableTarget.Process) ?? throw new NativeGoLiveContractException(
                "sql-bootstrap-environment-missing"));
        }
        finally
        {
            NativeGoLiveSqlBootstrap.ClearProcessEnvironment();
        }
    }

    public async ValueTask VerifyOneShotPreflightAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
    {
        EnsurePlan(plan);
        var actualPayload = NativeGoLivePayloadHasher.Compute(_mergedMainRoot);
        if (!NativeGoLivePayloadHasher.Same(actualPayload, _capability.PayloadManifest))
            throw new NativeGoLiveContractException("payload-not-one-shot-bound-native");

        var observation = await _ports.OneShotPreflight.ObserveAsync(
            plan, _bootstrap, cancellationToken).ConfigureAwait(false);
        ValidatePreflight(observation, plan);
        if (!NativeGoLiveSqlBootstrapAuthorityContract.IsValidProcedureSet(observation.Sql.BootstrapProcedures))
            throw new NativeGoLiveContractException("sql-bootstrap-authority-drift");
        var independentlyObservedVss = _ports.Vss.Query(cancellationToken);
        if (independentlyObservedVss != observation.Vss)
            throw new NativeGoLiveContractException("vss-observation-not-bound");
        _preflightVss = independentlyObservedVss;
    }

    public async ValueTask PrepareHostPrerequisitesAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
    {
        EnsurePlan(plan);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIis(_ports.Iis.ReplaceCanonical(plan), plan);
        if (_bootstrapInstaller is null)
        {
            if (_testBootstrap is null)
                throw new NativeGoLiveContractException("sql-bootstrap-installer-not-supported");
            return;
        }
        await _bootstrapInstaller(_bootstrap.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> StopPoolAsync(CancellationToken cancellationToken)
    {
        var result = await _ports.Iis.StopAsync(_plan.AppPoolName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.AppPoolName, _plan.AppPoolName, StringComparison.Ordinal) ||
            !string.Equals(result.State, "Stopped", StringComparison.Ordinal) ||
            !result.OperationSucceeded)
            throw new NativeGoLivePoolStopException(result.WasRunning, "app-pool-stop-not-proved");
        return result.WasRunning;
    }

    public async ValueTask RestorePoolAsync(CancellationToken cancellationToken)
    {
        var result = await _ports.Iis.RestoreAsync(_plan.AppPoolName, cancellationToken).ConfigureAwait(false);
        if (!result.OperationSucceeded ||
            !string.Equals(result.AppPoolName, _plan.AppPoolName, StringComparison.Ordinal) ||
            !string.Equals(result.State, "Started", StringComparison.Ordinal))
            throw new NativeGoLiveContractException("app-pool-restore-not-proved");
    }

    public ValueTask ConfigureVssAsync(NativeGoLiveVssPolicy policy, CancellationToken cancellationToken)
    {
        if (_preflightVss is null) throw new NativeGoLiveContractException("vss-preflight-missing");
        if (policy != _plan.Vss) throw new NativeGoLiveContractException("vss-policy-not-plan-bound");
        var result = _ports.Vss.Ensure(policy, _preflightVss, cancellationToken);
        var expectedAction = _preflightVss.Association.State == VssAssociationState.ExactExisting
            ? NativeGoLiveVssAction.ChangeDiffAreaMaximumSize
            : NativeGoLiveVssAction.AddDiffArea;
        if (result.Observed != _preflightVss.Association ||
            result.Action != expectedAction ||
            result.Verified.State != VssAssociationState.ExactExisting ||
            !SameVolume(result.Verified.SourceVolumeId, _preflightVss.Association.SourceVolumeId) ||
            !SameVolume(result.Verified.StorageVolumeId, _preflightVss.Association.StorageVolumeId) ||
            result.Verified.MaximumBytes is not { } verifiedMaximum ||
            verifiedMaximum < 0 ||
            checked((ulong)verifiedMaximum) != _preflightVss.RequiredMaximumBytes)
            throw new NativeGoLiveContractException("vss-exact-action-not-proved");
        return ValueTask.CompletedTask;
    }

    public ValueTask CreateEmptyRootAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
    {
        EnsurePlan(plan);
        return _ports.OwnedState.CreateEmptyRootAsync(cancellationToken);
    }

    public async ValueTask ProvisionEmptyCatalogueAsync(
        NativeGoLiveSqlIdentity sql, CancellationToken cancellationToken)
    {
        try
        {
            if (!NativeGoLivePayloadHasher.Same(
                    NativeGoLivePayloadHasher.Compute(_mergedMainRoot),
                    _capability.PayloadManifest))
                throw new NativeGoLiveContractException("merged-main-payload-changed");
            var acls = await _ports.Acls.ApplyAndObserveAsync(_plan, cancellationToken).ConfigureAwait(false);
            ValidateAcls(acls, _plan);
            var observation = await _ports.Sql.ProvisionAndObserveAsync(
                sql, _bootstrap, _capability.PayloadManifest, cancellationToken).ConfigureAwait(false);
            ValidatePostBootstrap(observation);
        }
        finally
        {
            NativeGoLiveSqlBootstrap.ClearProcessEnvironment();
        }
    }

    public async ValueTask PublishAndStartAsync(
        NativeGoLivePlan plan,
        CancellationToken cancellationToken)
    {
        EnsurePlan(plan);
        EnsureBootstrapCleared();
        var sourceBefore = NativeGoLivePayloadHasher.Compute(_mergedMainRoot);
        if (!NativeGoLivePayloadHasher.Same(sourceBefore, _capability.PayloadManifest))
            throw new NativeGoLiveContractException("merged-main-payload-changed");
        await _ports.Publish.PublishAsync(_mergedMainRoot, _applicationRoot, cancellationToken).ConfigureAwait(false);
        var sourceAfter = NativeGoLivePayloadHasher.Compute(_mergedMainRoot);
        var destination = NativeGoLivePayloadHasher.Compute(_applicationRoot);
        if (!SameManifest(sourceBefore, sourceAfter) ||
            !SameManifest(destination, sourceBefore) ||
            !string.Equals(destination.Sha256, _capability.PayloadSha256, StringComparison.Ordinal))
            throw new NativeGoLiveContractException("published-payload-hash-mismatch");
        ValidateAcls(await _ports.Acls.ObserveEffectiveAsync(plan, cancellationToken).ConfigureAwait(false), plan);
        await _ports.OwnedState.WriteProductionConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await (_ports.Composition ?? throw new NativeGoLiveContractException("published-composition-proof-missing"))
            .ValidatePublishedCompositionAsync(plan, cancellationToken).ConfigureAwait(false);
        var pool = await _ports.Iis.StartAsync(plan.AppPoolName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(pool.AppPoolName, plan.AppPoolName, StringComparison.Ordinal) ||
            !string.Equals(pool.State, "Started", StringComparison.Ordinal) ||
            !pool.OperationSucceeded)
            throw new NativeGoLiveContractException("app-pool-start-not-proved");
    }

    public async ValueTask ValidateAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
    {
        EnsurePlan(plan);
        EnsureBootstrapCleared();
        ValidateLoopback(await _ports.Loopback.ObserveAsync(cancellationToken).ConfigureAwait(false));
        ValidateAcls(await _ports.Acls.ObserveEffectiveAsync(plan, cancellationToken).ConfigureAwait(false), plan);
    }

    public async ValueTask RegisterMarketplaceAsync(
        NativeGoLiveCodexIdentity codex, CancellationToken cancellationToken)
    {
        EnsureBootstrapCleared();
        var result = await _ports.Marketplace.RegisterAndObserveAsync(codex, cancellationToken).ConfigureAwait(false);
        ValidateMarketplace(result, codex, allowMissing: false);
    }

    private void EnsurePlan(NativeGoLivePlan plan)
    {
        if (!ReferenceEquals(plan, _plan)) throw new NativeGoLiveContractException("go-live-plan-not-canonical");
    }

    private void ValidatePreflight(NativeGoLivePreflightObservation value, NativeGoLivePlan plan)
    {
        ValidateIis(value.Iis, plan);
        if (!string.Equals(value.Root.Root, plan.Layout.Root, StringComparison.Ordinal) ||
            value.Root.Exists || value.Root.HasReparsePoint ||
            !string.Equals(value.Root.CommittedSha, plan.CommittedSha, StringComparison.Ordinal) ||
            !string.Equals(value.Root.PlanHash, plan.PlanHash, StringComparison.Ordinal) ||
            !ValidateAncestors(value.Root.Ancestors, plan.Layout.Root, allowMissingTerminalRoot: true))
            throw new NativeGoLiveContractException("live-root-not-absent");
        ValidateRuntime(value.Runtime);
        ValidateSqlPreflight(value.Sql, plan);
        ValidateMarketplace(value.Marketplace, plan.Codex, allowMissing: true);
        var expectedVssMaximum = value.Vss.VolumeCapacityBytes == 0
            ? 0
            : checked((ulong)decimal.Floor(value.Vss.VolumeCapacityBytes * plan.Vss.MaximumStorageFraction));
        if (value.Vss.Association.State is not (VssAssociationState.ExactExisting or VssAssociationState.SupportedAbsent) ||
            !SameVolume(value.Vss.Association.SourceVolumeId, value.Vss.Association.StorageVolumeId) ||
            value.Vss.VolumeCapacityBytes == 0 ||
            value.Vss.RequiredMaximumBytes != expectedVssMaximum ||
            expectedVssMaximum < VssDiffAreaAdministration.MinimumDiffAreaBytes ||
            expectedVssMaximum > long.MaxValue)
            throw new NativeGoLiveContractException("vss-association-not-supported");
    }

    private static void ValidateIis(NativeGoLiveIisObservation value, NativeGoLivePlan plan)
    {
        if (!string.Equals(value.SiteName, plan.IisSiteName, StringComparison.Ordinal) ||
            !string.Equals(value.AppPoolName, plan.AppPoolName, StringComparison.Ordinal) ||
            !SamePath(value.PhysicalPath, plan.Layout.ApplicationRoot) ||
            !value.AnonymousAuthentication || value.WindowsAuthentication ||
            value.Bindings.Count != 1 ||
            value.Bindings[0] != new NativeGoLiveIisBinding("http", "127.0.0.1", plan.LoopbackPort, string.Empty))
            throw new NativeGoLiveContractException("iis-binding-not-canonical");
    }

    private static void ValidateSqlPreflight(
        NativeGoLiveSqlPreflightObservation value,
        NativeGoLivePlan plan)
    {
        var expectedAppPoolSid = value.ExpectedAppPoolSid;
        var existingLoginSafe = value.AppPoolLoginExists
            ? !string.IsNullOrWhiteSpace(value.AppPoolLoginSid) &&
              string.Equals(value.AppPoolLoginSid, expectedAppPoolSid, StringComparison.Ordinal)
            : value.AppPoolLoginSid is null;
        if (!value.FullTextInstalled ||
            !string.Equals(value.AppPoolLoginName, @"IIS AppPool\FluxKnowledge", StringComparison.Ordinal) ||
            !IsSid(expectedAppPoolSid) || !existingLoginSafe ||
            string.IsNullOrWhiteSpace(value.AppPoolLoginSidHex) ||
            !value.AppPoolLoginIsSysAdmin ||
            !ValidateBootstrapProcedureEvidence(value.BootstrapProcedures))
            throw new NativeGoLiveContractException("sql-preflight-direct-admin-not-proved");
    }

    private void ValidatePostBootstrap(NativeGoLiveSqlPostBootstrapObservation value)
    {
        if (!string.Equals(value.CatalogueName, "FluxKnowledge", StringComparison.Ordinal) ||
            value.CatalogueDatabaseId <= 4 ||
            !IsOpaqueSqlSid(value.CatalogueOwnerSidHex) ||
            !ExactDatabaseFiles(value.DatabaseFiles, _plan.Sql) ||
            !value.FullTextInstalled ||
            !string.Equals(value.CatalogueOwnerSidHex, value.AppPoolLoginSidHex, StringComparison.Ordinal) ||
            !value.ExpectedMigrations.SequenceEqual(NativeGoLiveDatabaseContract.RequiredMigrations, StringComparer.Ordinal) ||
            !value.AppliedMigrations.SequenceEqual(NativeGoLiveDatabaseContract.RequiredMigrations, StringComparer.Ordinal) ||
            !value.EmptyMarkerDurable || !value.EmptyReadinessProved ||
            !value.AppPoolCanConnect ||
            !string.Equals(value.AppPoolLoginName, @"IIS AppPool\FluxKnowledge", StringComparison.Ordinal) ||
            !value.AppPoolLoginExists || !IsSid(value.ExpectedAppPoolSid) ||
            !string.Equals(value.AppPoolLoginSid, value.ExpectedAppPoolSid, StringComparison.Ordinal) ||
            !IsOpaqueSqlSid(value.AppPoolLoginSidHex) ||
            !value.AppPoolLoginIsSysAdmin ||
            value.AppPoolHasSqlFileAccess || value.KnowledgeItems != 0 || value.Edges != 0 ||
            value.PendingOperations != 0 || value.ActiveIndexGenerations != 0 ||
            !ValidateBootstrapProcedureEvidence(value.BootstrapProcedures))
            throw new NativeGoLiveContractException("sql-bootstrap-postcondition-failed");
    }

    private static bool ValidateBootstrapProcedureEvidence(
        IReadOnlyList<NativeGoLiveSqlProcedureObservation>? procedures)
    {
        if (procedures is null ||
            !NativeGoLiveSqlBootstrapAuthorityContract.IsValidProcedureSet(procedures))
            return false;

        var expected = new Dictionary<string, NativeGoLiveSqlParameterObservation[]>(StringComparer.Ordinal)
        {
            ["FluxKnowledgeNativeGoLiveCreate"] =
            [
                new("@Catalogue", "nvarchar", 256, false),
                new("@DataFile", "nvarchar", 520, false),
                new("@LogFile", "nvarchar", 520, false),
                new("@AppPoolLogin", "nvarchar", 256, false),
                new("@AppPoolSid", "varbinary", 85, false)
            ],
            ["FluxKnowledgeNativeGoLiveDrop"] = [new("@Catalogue", "nvarchar", 256, false)],
            ["FluxKnowledgeNativeGoLiveManageAppPool"] =
            [
                new("@Catalogue", "nvarchar", 256, false),
                new("@AppPoolLogin", "nvarchar", 256, false),
                new("@AppPoolSid", "varbinary", 85, false),
                new("@BootstrapLogin", "nvarchar", 256, false)
            ],
            ["FluxKnowledgeNativeGoLiveObserveAppPool"] =
            [
                new("@Catalogue", "nvarchar", 256, false),
                new("@AppPoolLogin", "nvarchar", 256, false)
            ]
        };
        if (procedures.Count != expected.Count || !procedures.All(procedure =>
            expected.TryGetValue(procedure.Name, out var parameters) &&
            procedure.ObjectId > 0 &&
            procedure.Parameters.SequenceEqual(parameters)))
            return false;
        return true;
    }

    private static void ValidateAcls(NativeGoLiveAclObservation value, NativeGoLivePlan plan)
    {
        var layout = plan.Layout;
        var sqlRoots = new[] { layout.SqlDataRoot, layout.SqlLogRoot };
        var appPoolModifyRoots = new[]
        {
            Path.Combine(layout.ConfigRoot, "data-protection"), layout.IndexRoot,
            layout.RetainedRoot, layout.SpoolRoot, layout.TempRoot, layout.LogsRoot
        };
        var expectedObservedPaths = new[]
        {
            layout.Root, layout.ApplicationRoot, layout.ConfigRoot, Path.Combine(layout.ConfigRoot, "data-protection"),
            layout.DataRoot, layout.SqlRoot,
            layout.SqlDataRoot, layout.SqlLogRoot, layout.IndexRoot, layout.RetainedRoot,
            layout.RuntimeRoot, layout.SpoolRoot, layout.TempRoot, layout.LogsRoot,
            layout.CodexPluginRoot, layout.RecoveryRoot
        };
        var allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-5-18", "S-1-5-32-544", value.AppPoolSid, value.SqlServiceSid
        };
        var aclShapeSafe = IsSid(value.AppPoolSid) && IsSid(value.SqlServiceSid) &&
            value.Paths is not null &&
            ExactSet(value.Paths.Select(path => path.Path).ToArray(), expectedObservedPaths) &&
            value.Paths.All(path => path.IsProtected && path.Rules.All(rule =>
                !rule.Inherited && IsSid(rule.Sid) &&
                (!rule.Allow || allowedSids.Contains(rule.Sid) &&
                    rule.InheritanceFlags == 3 && rule.PropagationFlags == 0 &&
                    rule.AppliesToSelf && rule.AppliesToChildContainers && rule.AppliesToChildObjects))) &&
            ValidateObservedAclRights(value.Paths, value.AppPoolSid, value.SqlServiceSid, plan);
        if (!aclShapeSafe ||
            !ExactSet(value.SqlServiceWriteRoots, sqlRoots) ||
            !ExactSet(value.AppPoolReadExecuteRoots, [layout.ApplicationRoot]) ||
            !ExactSet(value.AppPoolReadRoots, [layout.ConfigRoot]) ||
            !ExactSet(value.AppPoolModifyRoots, appPoolModifyRoots) ||
            value.AppPoolSqlFileAccess || value.AppPoolAppWriteAccess || value.AppPoolConfigWriteAccess ||
            value.AppPoolRecoveryAccess || !value.AppPoolDataProtectionModifyOnly)
            throw new NativeGoLiveContractException("effective-acl-postcondition-failed");
    }

    private static bool ValidateObservedAclRights(
        IReadOnlyList<NativeGoLiveAclPathObservation> paths,
        string appPoolSid,
        string sqlServiceSid,
        NativeGoLivePlan plan)
    {
        const long Read = 131209;
        const long ReadAndExecute = 131241;
        const long Modify = 197055;
        const long FullControl = 2032127;
        var byPath = paths.ToDictionary(
            path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Path)),
            StringComparer.OrdinalIgnoreCase);
        NativeGoLiveAclPathObservation At(string path) =>
            byPath[Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))];
        bool HasExactRules(
            string path,
            params (string Sid, long Rights)[] additionalRules)
        {
            var expected = new[]
            {
                (Sid: "S-1-5-18", Rights: FullControl),
                (Sid: "S-1-5-32-544", Rights: FullControl)
            }.Concat(additionalRules).ToArray();
            var actual = At(path).Rules;
            return actual.Count == expected.Length && expected.All(item =>
                actual.Count(rule =>
                    rule.Allow && !rule.Inherited &&
                    rule.InheritanceFlags == 3 && rule.PropagationFlags == 0 &&
                    rule.AppliesToSelf && rule.AppliesToChildContainers && rule.AppliesToChildObjects &&
                    string.Equals(rule.Sid, item.Sid, StringComparison.OrdinalIgnoreCase) &&
                    rule.Rights == item.Rights) == 1);
        }
        var layout = plan.Layout;
        var appModify = new[]
        {
            Path.Combine(layout.ConfigRoot, "data-protection"), layout.IndexRoot, layout.RetainedRoot,
            layout.SpoolRoot, layout.TempRoot, layout.LogsRoot
        };
        var sqlRoots = new[] { layout.SqlDataRoot, layout.SqlLogRoot };
        var deleteChildBoundaries = new[]
        {
            layout.Root, layout.DataRoot, layout.SqlRoot, layout.RuntimeRoot, layout.CodexPluginRoot
        };
        return deleteChildBoundaries.All(path => HasExactRules(path)) &&
            HasExactRules(layout.ApplicationRoot, (appPoolSid, ReadAndExecute)) &&
            HasExactRules(layout.ConfigRoot, (appPoolSid, Read)) &&
            appModify.All(path => HasExactRules(path, (appPoolSid, Modify))) &&
            sqlRoots.All(path => HasExactRules(path, (sqlServiceSid, Modify))) &&
            HasExactRules(layout.RecoveryRoot);
    }

    private static bool ExactDatabaseFiles(
        IReadOnlyList<NativeGoLiveSqlDatabaseFileObservation> files,
        NativeGoLiveSqlIdentity expected) =>
        files.Count == 2 &&
        files[0].FileId == 1 &&
        string.Equals(files[0].TypeDescription, "ROWS", StringComparison.Ordinal) &&
        SamePath(files[0].PhysicalPath, expected.DataFilePath) &&
        files[1].FileId == 2 &&
        string.Equals(files[1].TypeDescription, "LOG", StringComparison.Ordinal) &&
        SamePath(files[1].PhysicalPath, expected.LogFilePath);

    private static bool IsSid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("S-1-", StringComparison.Ordinal) &&
        value.Length <= 184;

    private static bool IsOpaqueSqlSid(string? value) =>
        value is { Length: > 0 } && value.Length % 2 == 0 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool SameManifest(NativeGoLivePayloadManifest left, NativeGoLivePayloadManifest right) =>
        NativeGoLivePayloadHasher.Same(left, right);

    private static void ValidateLoopback(NativeGoLiveLoopbackObservation value)
    {
        RequireHttp(value.HealthLive, "GET", "/health/live", 200, requireNativeProofMarker: true);
        RequireHttp(value.HealthReady, "GET", "/health/ready", 200, requireNativeProofMarker: true);
        RequireHttp(value.IndexHealth, "GET", "/api/index-health", 200);
        RequireHttp(value.Gpu.Http, "GET", "/api/gpu-status", 200);
        if (value.Gpu.Ready != 0 || value.Gpu.Active != 0 || value.Gpu.Deferred != 0 ||
            value.Gpu.Uncertain != 0 || value.Gpu.ActiveBatch is not null)
            throw new NativeGoLiveContractException("gpu-zero-work-contract-failed");
        RequireHttp(value.Search.Http, "POST", "/api/v1/knowledge/search", 200);
        if (!value.Search.Succeeded ||
            !string.Equals(value.Search.Query, "native-go-live-empty-probe", StringComparison.Ordinal) ||
            value.Search.Limit != 1 || value.Search.ResultCount != 0)
            throw new NativeGoLiveContractException("rest-empty-search-contract-failed");
        RequireHttp(value.Mcp.Initialise, "POST", "/mcp", 200);
        RequireHttp(value.Mcp.ToolsList, "POST", "/mcp", 200);
        if (!ExactSet(value.Mcp.Tools, NativeGoLiveLoopbackContract.RequiredMcpTools))
            throw new NativeGoLiveContractException("mcp-tool-contract-mismatch");
        if (!string.Equals(value.ForwardedDenial.Method, "GET", StringComparison.Ordinal) ||
            !string.Equals(value.ForwardedDenial.Uri, NativeGoLiveLoopbackContract.BaseUri + "/health/live", StringComparison.Ordinal) ||
            value.ForwardedDenial.StatusCode != 403 ||
            !ExactSet(value.ForwardedDenial.SentHeaderNames, ["Forwarded", "X-Forwarded-For"]) ||
            !IsLoopbackPeer(value.ForwardedDenial.Peer))
            throw new NativeGoLiveContractException("forwarded-denial-contract-failed");
        if (!value.NonLoopbackDenial.CandidateEnumerationComplete ||
            value.NonLoopbackDenial.ConnectionRefusedCount != value.NonLoopbackDenial.CandidateCount ||
            value.NonLoopbackDenial.ConnectionSucceededCount != 0 ||
            value.NonLoopbackDenial.IndeterminateCount != 0)
            throw new NativeGoLiveContractException("non-loopback-application-denial-contract-failed");
        if (value.UsedProxy || value.FollowedRedirect)
            throw new NativeGoLiveContractException("loopback-probe-indirection-refused");
        ValidateRuntime(value.EffectiveRuntime);
    }

    private static void RequireHttp(
        NativeGoLiveHttpObservation value,
        string method,
        string path,
        int status,
        bool requireNativeProofMarker = false)
    {
        if (!string.Equals(value.Method, method, StringComparison.Ordinal) ||
            !string.Equals(value.Uri, NativeGoLiveLoopbackContract.BaseUri + path, StringComparison.Ordinal) ||
            value.StatusCode != status ||
            !IsLoopbackPeer(value.Peer) ||
            value.SentHeaderNames.Count != 0)
            throw new NativeGoLiveContractException("loopback-http-contract-failed");
        if (requireNativeProofMarker && !value.HasNativeProofMarker)
            throw new NativeGoLiveContractException("loopback-native-proof-marker-missing");
    }

    private static bool IsLoopbackPeer(NativeGoLiveHttpPeer peer) =>
        IPAddress.TryParse(peer.LocalAddress, out var local) && IPAddress.IsLoopback(local) &&
        IPAddress.TryParse(peer.RemoteAddress, out var remote) && IPAddress.IsLoopback(remote);

    private static void ValidateRuntime(NativeGoLiveRuntimeObservation value)
    {
        if (value.OutlookEnabled || value.PhaseSixEnabled || value.ModelRuntimeEnabled ||
            value.GpuEnabled || value.FfmpegEnabled || value.NetworkParsingEnabled)
            throw new NativeGoLiveContractException("prohibited-runtime-enabled");
    }

    private static void ValidateMarketplace(
        NativeGoLiveMarketplaceObservation value, NativeGoLiveCodexIdentity expected, bool allowMissing)
    {
        var stateValid = string.Equals(value.State, "ExactExisting", StringComparison.Ordinal) ||
                         allowMissing && string.Equals(value.State, "Missing", StringComparison.Ordinal);
        if (!stateValid ||
            !string.Equals(value.Name, expected.MarketplaceName, StringComparison.Ordinal) ||
            !SamePath(value.Root, expected.MarketplaceRoot) ||
            !string.Equals(value.PluginName, expected.PluginName, StringComparison.Ordinal))
            throw new NativeGoLiveContractException("marketplace-state-foreign");
    }

    private static bool ExactSet(IReadOnlyList<string> actual, IReadOnlyList<string> expected) =>
        actual.Count == expected.Count &&
        actual.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(
            expected.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool SameVolume(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ValidateAncestors(
        IReadOnlyList<NativeGoLivePathAncestorObservation> actual,
        string expectedRoot,
        bool allowMissingTerminalRoot = false)
    {
        var expected = EnumerateAncestorPaths(expectedRoot);
        if (actual.Count != expected.Count || actual.Count == 0) return false;
        var volumeId = actual[0].VolumeId;
        if (string.IsNullOrWhiteSpace(volumeId)) return false;
        for (var index = 0; index < expected.Count; index++)
        {
            var item = actual[index];
            var isMissingTerminalRoot = allowMissingTerminalRoot && index == expected.Count - 1 && !item.Exists;
            if ((!isMissingTerminalRoot && !item.Exists) || item.IsReparsePoint ||
                !SamePath(item.Path, expected[index]) ||
                (!isMissingTerminalRoot && !SamePath(item.ResolvedPath, expected[index])) ||
                (!isMissingTerminalRoot && !string.Equals(item.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    private static IReadOnlyList<string> EnumerateAncestorPaths(string root)
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

    private static void EnsureBootstrapCleared()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(
                NativeGoLiveSqlBootstrap.EnvironmentVariable, EnvironmentVariableTarget.Process)))
            throw new NativeGoLiveContractException("sql-bootstrap-environment-not-cleared");
    }

    private sealed class HostLease(
        GuardedNativeGoLiveHost owner,
        NativeGoLiveMachineWideLease machineWideLease) : INativeGoLiveLease
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await machineWideLease.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref owner._leaseState, 0);
            }
        }
    }
}

internal sealed class NativeGoLiveVssComPort(VssDiffAreaAdministration administration) : INativeGoLiveVssPort
{
    internal NativeGoLiveVssComPort() : this(new VssDiffAreaAdministration()) { }
    public NativeGoLiveVssPreflightObservation Query(CancellationToken cancellationToken) =>
        administration.QueryCanonicalObservation(cancellationToken);
    public NativeGoLiveVssMutationObservation Ensure(
        NativeGoLiveVssPolicy policy,
        NativeGoLiveVssPreflightObservation expected,
        CancellationToken cancellationToken) =>
        administration.EnsureMaximumStorageObserved(
            policy.Volume, policy.MaximumStorageFraction, expected, cancellationToken);
}
