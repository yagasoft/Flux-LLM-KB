using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using Microsoft.Data.SqlClient;
using Microsoft.Web.Administration;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

internal interface INativeGoLiveIisAdministration
{
    NativeGoLiveIisObservation Observe(NativeGoLivePlan plan);
    void ReplaceCanonical(NativeGoLivePlan plan);
    string ObservePoolState(string appPoolName);
    void StopPool(string appPoolName);
    void StartPool(string appPoolName);
}

internal sealed class NativeGoLiveWindowsIisPort : INativeGoLiveIisPort
{
    private static readonly TimeSpan PoolStopObservationWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PoolStopObservationInterval = TimeSpan.FromMilliseconds(100);
    private readonly INativeGoLiveIisAdministration _administration;

    internal NativeGoLiveWindowsIisPort()
        : this(new MicrosoftWebAdministrationNativeGoLiveApi())
    {
    }

    internal NativeGoLiveWindowsIisPort(INativeGoLiveIisAdministration administration) =>
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));

    internal NativeGoLiveIisObservation Observe(NativeGoLivePlan plan) => _administration.Observe(plan);

    public NativeGoLiveIisObservation ReplaceCanonical(NativeGoLivePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _administration.ReplaceCanonical(plan);
        return _administration.Observe(plan);
    }

    public async ValueTask<NativeGoLivePoolObservation> StopAsync(
        string appPoolName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = _administration.ObservePoolState(appPoolName);
        TryMutate(() => _administration.StopPool(appPoolName));
        string after;
        try
        {
            after = await ObserveStoppedAsync(appPoolName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or Win32Exception)
        {
            throw new NativeGoLivePoolStopException(
                string.Equals(before, "Started", StringComparison.Ordinal),
                "app-pool-stop-final-observation-failed");
        }
        return new NativeGoLivePoolObservation(
            appPoolName,
            string.Equals(before, "Started", StringComparison.Ordinal),
            after,
            string.Equals(after, "Stopped", StringComparison.Ordinal));
    }

    public ValueTask<NativeGoLivePoolObservation> RestoreAsync(
        string appPoolName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryMutate(() => _administration.StartPool(appPoolName));
        var after = _administration.ObservePoolState(appPoolName);
        return ValueTask.FromResult(new NativeGoLivePoolObservation(
            appPoolName,
            WasRunning: false,
            after,
            string.Equals(after, "Started", StringComparison.Ordinal)));
    }

    public ValueTask<NativeGoLivePoolObservation> StartAsync(
        string appPoolName,
        CancellationToken cancellationToken) => RestoreAsync(appPoolName, cancellationToken);

    private static bool TryMutate(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or Win32Exception)
        {
            return false;
        }
    }

    private async ValueTask<string> ObserveStoppedAsync(string appPoolName, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + PoolStopObservationWindow;
        while (true)
        {
            var state = _administration.ObservePoolState(appPoolName);
            if (string.Equals(state, "Stopped", StringComparison.Ordinal) || DateTime.UtcNow >= deadline)
                return state;
            await Task.Delay(PoolStopObservationInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class MicrosoftWebAdministrationNativeGoLiveApi : INativeGoLiveIisAdministration
{
    public void ReplaceCanonical(NativeGoLivePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureWindows();
        using var manager = new ServerManager();
        var existingSite = manager.Sites[plan.IisSiteName];
        if (existingSite is not null) manager.Sites.Remove(existingSite);
        var existingPool = manager.ApplicationPools[plan.AppPoolName];
        if (existingPool is not null) manager.ApplicationPools.Remove(existingPool);

        var pool = manager.ApplicationPools.Add(plan.AppPoolName);
        pool.ProcessModel.IdentityType = ProcessModelIdentityType.ApplicationPoolIdentity;
        pool.ManagedPipelineMode = ManagedPipelineMode.Integrated;
        pool.AutoStart = true;
        var site = manager.Sites.Add(
            plan.IisSiteName,
            "http",
            $"127.0.0.1:{plan.LoopbackPort}:",
            plan.Layout.ApplicationRoot);
        site.Applications["/"].ApplicationPoolName = plan.AppPoolName;
        var configuration = manager.GetApplicationHostConfiguration();
        configuration.GetSection(
                "system.webServer/security/authentication/anonymousAuthentication", plan.IisSiteName)
            .SetAttributeValue("enabled", true);
        configuration.GetSection(
                "system.webServer/security/authentication/windowsAuthentication", plan.IisSiteName)
            .SetAttributeValue("enabled", false);
        manager.CommitChanges();
    }

    public NativeGoLiveIisObservation Observe(NativeGoLivePlan plan)
    {
        EnsureWindows();
        using var manager = new ServerManager();
        var site = manager.Sites.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, plan.IisSiteName, StringComparison.Ordinal));
        var pool = manager.ApplicationPools.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, plan.AppPoolName, StringComparison.Ordinal));
        var application = site?.Applications.SingleOrDefault(candidate => candidate.Path == "/");
        var virtualDirectory = application?.VirtualDirectories.SingleOrDefault(candidate => candidate.Path == "/");
        if (site is null || pool is null || application is null || virtualDirectory is null)
            throw new NativeGoLiveContractException("iis-site-or-pool-missing");

        var configuration = manager.GetApplicationHostConfiguration();
        // The application root is intentionally absent until the confirmed clean-slate admission.
        // Reading a site-scoped section here makes IIS try to load its not-yet-published web.config.
        // These authentication values are written to applicationHost.config during replacement, so
        // observe them there without dereferencing the future application root.
        var anonymous = Convert.ToBoolean(configuration
            .GetSection("system.webServer/security/authentication/anonymousAuthentication")
            .GetAttributeValue("enabled"), System.Globalization.CultureInfo.InvariantCulture);
        var windows = Convert.ToBoolean(configuration
            .GetSection("system.webServer/security/authentication/windowsAuthentication")
            .GetAttributeValue("enabled"), System.Globalization.CultureInfo.InvariantCulture);
        var bindings = site.Bindings.Select(binding =>
        {
            var parts = binding.BindingInformation.Split(':', 3);
            if (parts.Length != 3 || !int.TryParse(parts[1], out var port))
                return new NativeGoLiveIisBinding(binding.Protocol, string.Empty, -1, string.Empty);
            return new NativeGoLiveIisBinding(binding.Protocol, parts[0], port, parts[2]);
        }).ToArray();
        return new NativeGoLiveIisObservation(
            site.Name,
            pool.Name,
            virtualDirectory.PhysicalPath,
            anonymous,
            windows,
            bindings);
    }

    public string ObservePoolState(string appPoolName)
    {
        EnsureWindows();
        using var manager = new ServerManager();
        var pool = manager.ApplicationPools.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, appPoolName, StringComparison.Ordinal))
            ?? throw new NativeGoLiveContractException("iis-app-pool-missing");
        return pool.State.ToString();
    }

    public void StopPool(string appPoolName)
    {
        EnsureWindows();
        using var manager = new ServerManager();
        var pool = manager.ApplicationPools[appPoolName]
            ?? throw new NativeGoLiveContractException("iis-app-pool-missing");
        if (pool.State != ObjectState.Stopped) pool.Stop();
    }

    public void StartPool(string appPoolName)
    {
        EnsureWindows();
        using var manager = new ServerManager();
        var pool = manager.ApplicationPools[appPoolName]
            ?? throw new NativeGoLiveContractException("iis-app-pool-missing");
        if (pool.State != ObjectState.Started) pool.Start();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native IIS administration is Windows-only.");
    }
}

internal sealed class NativeGoLiveWindowsPublishPort(
    NativeGoLivePlan plan,
    HandleRelativeNativeFileSystem? fileSystem = null) : INativeGoLivePublishPort
{
    private readonly NativeGoLivePlan _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    private readonly HandleRelativeNativeFileSystem _fileSystem = fileSystem ?? new HandleRelativeNativeFileSystem();

    public async ValueTask PublishAsync(
        string mergedMainRoot,
        string applicationRoot,
        CancellationToken cancellationToken)
    {
        if (!SamePath(applicationRoot, _plan.Layout.ApplicationRoot))
            throw new NativeGoLiveContractException("publish-destination-not-canonical");
        var expected = NativeGoLivePayloadHasher.Compute(mergedMainRoot);
        using var source = _fileSystem.OpenDirectory(mergedMainRoot);
        using var destination = _fileSystem.OpenOrCreateDirectory(_plan.Layout.ApplicationRoot);
        RequireMutation(await _fileSystem.DeleteTreeContentsAsync(destination, cancellationToken).ConfigureAwait(false));
        await CopyTreeAsync(source, destination, cancellationToken, [0]).ConfigureAwait(false);
        var actual = NativeGoLivePayloadHasher.Compute(_plan.Layout.ApplicationRoot);
        if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) ||
            expected.FileCount != actual.FileCount ||
            expected.TotalBytes != actual.TotalBytes ||
            !expected.Files.SequenceEqual(actual.Files))
            throw new NativeGoLiveContractException("published-payload-hash-mismatch");
    }

    private async ValueTask CopyTreeAsync(
        VerifiedNativeDirectory source,
        VerifiedNativeDirectory destination,
        CancellationToken cancellationToken,
        int[] visited)
    {
        foreach (var name in _fileSystem.EnumerateLiteralChildren(source).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (++visited[0] > 25_000)
                throw new NativeGoLiveContractException("merged-main-payload-file-count-invalid");
            var child = _fileSystem.InspectLiteralChild(source, name);
            if (child.IsDirectory)
            {
                RequireMutation(await _fileSystem.CreateDirectoryAsync(destination, name, cancellationToken).ConfigureAwait(false));
                using var sourceChild = _fileSystem.OpenDirectory(source, name);
                if (sourceChild.Identity != child.Identity)
                    throw new NativeGoLiveContractException("publish-file-identity-changed");
                using var destinationChild = _fileSystem.OpenDirectory(destination, name);
                await CopyTreeAsync(sourceChild, destinationChild, cancellationToken, visited).ConfigureAwait(false);
                continue;
            }

            RequireMutation(await _fileSystem.CopyLiteralChildAsync(
                source, name, child.Identity, destination, name, cancellationToken).ConfigureAwait(false));
        }
    }

    private static void RequireMutation(NativeFileMutation mutation)
    {
        if (!mutation.Changed)
            throw new NativeGoLiveContractException("publish-" + (mutation.Reason ?? "mutation-refused"));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record NativeGoLiveHttpRequestSpec(
    string Method,
    Uri Uri,
    string? JsonBody,
    IReadOnlyDictionary<string, string> Headers);

internal sealed record NativeGoLiveRawHttpResponse(
    int StatusCode,
    NativeGoLiveHttpPeer Peer,
    string Body,
    bool UsedProxy,
    bool FollowedRedirect,
    IReadOnlyDictionary<string, string>? Headers = null);

internal interface INativeGoLiveHttpTransport
{
    ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
        NativeGoLiveHttpRequestSpec request,
        CancellationToken cancellationToken);
}

internal enum NativeGoLiveTcpConnectionOutcome
{
    ConnectionRefused,
    ConnectionSucceeded,
    Indeterminate
}

internal interface INativeGoLiveTcpProbe
{
    ValueTask<NativeGoLiveTcpConnectionOutcome> ProbeAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken);
}

internal sealed class NativeGoLiveWindowsLoopbackPort : INativeGoLiveLoopbackPort
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan SearchReadinessObservationWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SearchReadinessObservationInterval = TimeSpan.FromMilliseconds(100);
    private static readonly Uri BaseUri = new(NativeGoLiveLoopbackContract.BaseUri + "/");
    private readonly INativeGoLiveHttpTransport? _transport;
    private readonly Func<IReadOnlyList<IPAddress>>? _nonLoopbackAddresses;
    private readonly INativeGoLiveTcpProbe? _tcpProbe;
    private readonly NativeGoLiveRuntimeObservation? _runtime;
    private readonly NativeGoLivePlan? _plan;

    private enum NonLoopbackEndpointOutcome
    {
        NonApplicationDenial,
        ApplicationReachable,
        Indeterminate
    }

    internal NativeGoLiveWindowsLoopbackPort(NativeGoLivePlan plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    internal NativeGoLiveWindowsLoopbackPort(
        INativeGoLiveHttpTransport transport,
        Func<IReadOnlyList<IPAddress>> nonLoopbackAddresses,
        NativeGoLiveRuntimeObservation runtime,
        INativeGoLiveTcpProbe tcpProbe)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _nonLoopbackAddresses = nonLoopbackAddresses ?? throw new ArgumentNullException(nameof(nonLoopbackAddresses));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _tcpProbe = tcpProbe ?? throw new ArgumentNullException(nameof(tcpProbe));
    }

    public async ValueTask<NativeGoLiveLoopbackObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var runtime = _runtime ?? NativeGoLiveRuntimeConfiguration.ReadPublished(_plan!);
        var live = await SendAsync("GET", "/health/live", null, null, cancellationToken).ConfigureAwait(false);
        var ready = await SendAsync("GET", "/health/ready", null, null, cancellationToken).ConfigureAwait(false);
        var index = await SendAsync("GET", "/api/index-health", null, null, cancellationToken).ConfigureAwait(false);
        var gpu = await SendAsync("GET", "/api/gpu-status", null, null, cancellationToken).ConfigureAwait(false);
        var search = await ObserveEmptySearchReadyAsync(cancellationToken).ConfigureAwait(false);
        var mcpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json, text/event-stream"
        };
        var initialise = await SendAsync(
            "POST",
            "/mcp",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"native-go-live\",\"version\":\"1\"}}}",
            mcpHeaders,
            cancellationToken).ConfigureAwait(false);
        var tools = await SendAsync(
            "POST",
            "/mcp",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
            mcpHeaders,
            cancellationToken).ConfigureAwait(false);
        var forwardedHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Forwarded"] = "for=192.0.2.20;proto=https;host=foreign.invalid",
            ["X-Forwarded-For"] = "192.0.2.20"
        };
        var forwarded = await SendAsync(
            "GET", "/health/live", null, forwardedHeaders, cancellationToken).ConfigureAwait(false);
        var nonLoopback = await ObserveNonLoopbackDenialAsync(cancellationToken).ConfigureAwait(false);

        using var gpuJson = ParseObject(gpu.Raw.Body, "gpu-status-invalid");
        using var indexJson = ParseObject(index.Raw.Body, "index-health-invalid");
        using var searchJson = ParseObject(search.Raw.Body, "rest-empty-search-invalid");
        using var initialiseJson = ParseObject(JsonRpcPayload(initialise.Raw.Body), "mcp-initialise-invalid");
        using var toolsJson = ParseObject(JsonRpcPayload(tools.Raw.Body), "mcp-tools-invalid");
        ValidateIndex(indexJson.RootElement);
        ValidateInitialise(initialiseJson.RootElement);
        ValidateJsonRpc(toolsJson.RootElement, expectedId: 2, "mcp-tools-invalid");
        var toolNames = toolsJson.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        var searchRoot = searchJson.RootElement;
        var resultCount = SearchResultCount(searchRoot);
        var gpuRoot = gpuJson.RootElement;

        return new NativeGoLiveLoopbackObservation(
            live.Observation,
            ready.Observation,
            index.Observation,
            new NativeGoLiveGpuObservation(
                gpu.Observation,
                RequiredInt(gpuRoot, "readyCount"),
                RequiredInt(gpuRoot, "activeCount"),
                RequiredInt(gpuRoot, "deferredCount"),
                RequiredInt(gpuRoot, "outcomeUncertainCount"),
                gpuRoot.TryGetProperty("hasActiveBatch", out var active) && active.GetBoolean()
                    ? gpuRoot.GetProperty("activeBatchLane").GetString()
                    : null),
            new NativeGoLiveSearchObservation(
                search.Observation,
                searchRoot.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                "native-go-live-empty-probe",
                1,
                resultCount),
            new NativeGoLiveMcpObservation(initialise.Observation, tools.Observation, toolNames),
            forwarded.Observation,
            nonLoopback,
            new[] { live.Raw, ready.Raw, index.Raw, gpu.Raw, search.Raw, initialise.Raw, tools.Raw, forwarded.Raw }
                .Any(response => response.UsedProxy),
            new[] { live.Raw, ready.Raw, index.Raw, gpu.Raw, search.Raw, initialise.Raw, tools.Raw, forwarded.Raw }
                .Any(response => response.FollowedRedirect),
            runtime);
    }

    private async ValueTask<NativeGoLiveTcpDenialObservation> ObserveNonLoopbackDenialAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> candidates;
        try
        {
            candidates = (_nonLoopbackAddresses ?? NativeGoLiveNetworkIdentity.GetActiveNonLoopbackIpv4)();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new NativeGoLiveTcpDenialObservation(false, 0, 0, 0, 1);
        }

        if (candidates is null)
            return new NativeGoLiveTcpDenialObservation(false, 0, 0, 0, 1);

        var refused = 0;
        var succeeded = 0;
        var indeterminate = 0;
        var probe = _tcpProbe ?? new SocketBoundNativeGoLiveTcpProbe();
        foreach (var candidate in candidates)
        {
            if (candidate.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(candidate))
            {
                indeterminate++;
                continue;
            }

            try
            {
                switch (await probe.ProbeAsync(candidate, NativeGoLivePlan.NativeLoopbackPort, cancellationToken)
                    .ConfigureAwait(false))
                {
                    case NativeGoLiveTcpConnectionOutcome.ConnectionRefused:
                        refused++;
                        break;
                    case NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded:
                        switch (await ProbeNonLoopbackApplicationAsync(candidate, cancellationToken).ConfigureAwait(false))
                        {
                            case NativeGoLiveTcpConnectionOutcome.ConnectionRefused:
                                refused++;
                                break;
                            case NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded:
                                succeeded++;
                                break;
                            default:
                                indeterminate++;
                                break;
                        }
                        break;
                    default:
                        indeterminate++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                indeterminate++;
            }
        }

        return new NativeGoLiveTcpDenialObservation(true, candidates.Count, refused, succeeded, indeterminate);
    }

    private async ValueTask<NativeGoLiveTcpConnectionOutcome> ProbeNonLoopbackApplicationAsync(
        IPAddress candidate,
        CancellationToken cancellationToken)
    {
        var baseUri = new UriBuilder("http", candidate.ToString(), NativeGoLivePlan.NativeLoopbackPort).Uri;
        var live = await ProbeNonLoopbackEndpointAsync(
            new Uri(baseUri, "health/live"), cancellationToken).ConfigureAwait(false);
        var ready = await ProbeNonLoopbackEndpointAsync(
            new Uri(baseUri, "health/ready"), cancellationToken).ConfigureAwait(false);
        if (live == NonLoopbackEndpointOutcome.NonApplicationDenial &&
            ready == NonLoopbackEndpointOutcome.NonApplicationDenial)
        {
            return NativeGoLiveTcpConnectionOutcome.ConnectionRefused;
        }
        if (live == NonLoopbackEndpointOutcome.ApplicationReachable ||
            ready == NonLoopbackEndpointOutcome.ApplicationReachable)
        {
            return NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded;
        }
        return NativeGoLiveTcpConnectionOutcome.Indeterminate;
    }

    private async ValueTask<NonLoopbackEndpointOutcome> ProbeNonLoopbackEndpointAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAbsoluteAsync("GET", endpoint, null, null, cancellationToken).ConfigureAwait(false);
            if (HasNativeProofMarker(response.Raw)) return NonLoopbackEndpointOutcome.ApplicationReachable;
            return IsSafeNonApplicationDenial(response.Raw)
                ? NonLoopbackEndpointOutcome.NonApplicationDenial
                : NonLoopbackEndpointOutcome.Indeterminate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NonLoopbackEndpointOutcome.Indeterminate;
        }
    }

    private static bool HasNativeProofMarker(NativeGoLiveRawHttpResponse response) =>
        response.Headers is not null && response.Headers.Any(header =>
            string.Equals(header.Key, NativeGoLiveLoopbackContract.NativeProofHeader, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(header.Value, NativeGoLiveLoopbackContract.NativeProofValue, StringComparison.Ordinal));

    private static bool IsSafeNonApplicationDenial(NativeGoLiveRawHttpResponse response)
    {
        if (response.UsedProxy || response.FollowedRedirect || HasNativeProofMarker(response)) return false;
        return response.StatusCode switch
        {
            400 => HasHeader(response, "Server", "Microsoft-HTTPAPI/2.0"),
            404 => HasHeader(response, "Server", "Microsoft-IIS/10.0"),
            _ => false
        };
    }

    private static bool HasHeader(NativeGoLiveRawHttpResponse response, string name, string value) =>
        response.Headers is not null && response.Headers.Any(header =>
            string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(header.Value, value, StringComparison.OrdinalIgnoreCase));

    private async ValueTask<(NativeGoLiveHttpObservation Observation, NativeGoLiveRawHttpResponse Raw)> SendAsync(
        string method,
        string path,
        string? json,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken) =>
        await SendAbsoluteAsync(method, new Uri(BaseUri, path), json, headers, cancellationToken).ConfigureAwait(false);

    private async ValueTask<(NativeGoLiveHttpObservation Observation, NativeGoLiveRawHttpResponse Raw)> ObserveEmptySearchReadyAsync(
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SearchReadinessObservationWindow;
        while (true)
        {
            var search = await SendAsync(
                "POST",
                "/api/v1/knowledge/search",
                "{\"query\":\"native-go-live-empty-probe\",\"limit\":1}",
                null,
                cancellationToken).ConfigureAwait(false);
            if (search.Observation.StatusCode == 200 || DateTime.UtcNow >= deadline)
                return search;

            await Task.Delay(SearchReadinessObservationInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<(NativeGoLiveHttpObservation Observation, NativeGoLiveRawHttpResponse Raw)> SendAbsoluteAsync(
        string method,
        Uri uri,
        string? json,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var actualHeaders = headers ?? new Dictionary<string, string>();
        var request = new NativeGoLiveHttpRequestSpec(method, uri, json, actualHeaders);
        var transport = _transport ?? new SocketBoundNativeGoLiveHttpTransport(MaximumResponseBytes);
        var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (new NativeGoLiveHttpObservation(
            method,
            uri.AbsoluteUri,
            response.StatusCode,
            response.Peer,
            actualHeaders.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            HasNativeProofMarker(response)), response);
    }

    private static JsonDocument ParseObject(string body, string reason)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new NativeGoLiveContractException(reason);
            }
            return document;
        }
        catch (JsonException)
        {
            throw new NativeGoLiveContractException(reason);
        }
    }

    private static string JsonRpcPayload(string body)
    {
        var data = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        return data is null ? body : data[5..].Trim();
    }

    private static int RequiredInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new NativeGoLiveContractException("gpu-status-invalid");

    private static void ValidateIndex(JsonElement root)
    {
        if (!root.TryGetProperty("state", out var state) || state.GetString() != "Healthy" ||
            !root.TryGetProperty("activeGeneration", out var active) || active.ValueKind != JsonValueKind.Null ||
            !root.TryGetProperty("failureCategory", out var failure) || failure.ValueKind != JsonValueKind.Null ||
            !root.TryGetProperty("cleanedCandidateCount", out var cleaned) || !cleaned.TryGetInt32(out var count) || count != 0)
            throw new NativeGoLiveContractException("index-health-invalid");
    }

    private static void ValidateInitialise(JsonElement root)
    {
        ValidateJsonRpc(root, expectedId: 1, "mcp-initialise-invalid");
        var result = root.GetProperty("result");
        if (!result.TryGetProperty("protocolVersion", out var protocol) ||
            !string.Equals(protocol.GetString(), "2025-11-25", StringComparison.Ordinal) ||
            !result.TryGetProperty("serverInfo", out var serverInfo) || serverInfo.ValueKind != JsonValueKind.Object ||
            !serverInfo.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString()) ||
            !serverInfo.TryGetProperty("version", out var version) || string.IsNullOrWhiteSpace(version.GetString()) ||
            !result.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            throw new NativeGoLiveContractException("mcp-initialise-invalid");
    }

    private static void ValidateJsonRpc(JsonElement root, int expectedId, string reason)
    {
        if (!root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
            !root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var actualId) || actualId != expectedId ||
            root.TryGetProperty("error", out _) ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new NativeGoLiveContractException(reason);
    }

    private static int SearchResultCount(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new NativeGoLiveContractException("rest-empty-search-invalid");
        foreach (var name in new[] { "items", "results" })
        {
            if (result.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array)
                return items.GetArrayLength();
        }
        throw new NativeGoLiveContractException("rest-empty-search-invalid");
    }
}

internal sealed class SocketBoundNativeGoLiveHttpTransport(int maximumResponseBytes) : INativeGoLiveHttpTransport
{
    public async ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
        NativeGoLiveHttpRequestSpec request,
        CancellationToken cancellationToken)
    {
        IPEndPoint? local = null;
        IPEndPoint? remote = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                if (!IPAddress.TryParse(context.DnsEndPoint.Host, out var address))
                    throw new NativeGoLiveContractException("numeric-local-address-required");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    local = socket.LocalEndPoint as IPEndPoint;
                    remote = socket.RemoteEndPoint as IPEndPoint;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Uri);
        if (request.JsonBody is not null)
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
        foreach (var header in request.Headers)
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content, maximumResponseBytes, cancellationToken).ConfigureAwait(false);
        if (local is null || remote is null)
            throw new NativeGoLiveContractException("http-peer-not-observed");
        return new NativeGoLiveRawHttpResponse(
            (int)response.StatusCode,
            new NativeGoLiveHttpPeer(local.Address.ToString(), remote.Address.ToString()),
            body,
            UsedProxy: false,
            FollowedRedirect: false,
            Headers: response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(",", group.SelectMany(header => header.Value)),
                    StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maximumBytes + 1 - checked((int)output.Length);
            if (remaining <= 0) throw new NativeGoLiveContractException("http-response-too-large");
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (output.Length > maximumBytes)
                throw new NativeGoLiveContractException("http-response-too-large");
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}

internal sealed class SocketBoundNativeGoLiveTcpProbe : INativeGoLiveTcpProbe
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    public async ValueTask<NativeGoLiveTcpConnectionOutcome> ProbeAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            return NativeGoLiveTcpConnectionOutcome.Indeterminate;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return NativeGoLiveTcpConnectionOutcome.ConnectionRefused;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NativeGoLiveTcpConnectionOutcome.Indeterminate;
        }
    }
}

internal static class NativeGoLiveNetworkIdentity
{
    internal static IReadOnlyList<IPAddress> GetActiveNonLoopbackIpv4()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()
            .ToArray();
        return addresses;
    }
}

internal static class NativeGoLiveRuntimeConfiguration
{
    internal static NativeGoLiveRuntimeObservation ReadPublished(NativeGoLivePlan plan) =>
        Read(Path.Combine(plan.Layout.ApplicationRoot, "appsettings.json"), plan.Layout.RetainedRoot);

    internal static NativeGoLiveRuntimeObservation ReadMergedMain(NativeGoLivePlan plan, string mergedMainRoot) =>
        Read(Path.Combine(mergedMainRoot, "appsettings.json"), plan.Layout.RetainedRoot);

    internal static void ValidateProductionConfiguration(
        byte[] bytes,
        NativeGoLivePlan plan,
        string expectedConnectionString)
    {
        if (bytes.Length is 0 or > 64 * 1024)
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        var canonical = NativeGoLiveProductionConfigurationSerializer.Serialize(plan, expectedConnectionString);
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var configuredConnection = root.GetProperty("ConnectionStrings").GetProperty("FluxKnowledge").GetString();
            var actual = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configuredConnection);
            var expected = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(expectedConnectionString);
            if (!actual.IntegratedSecurity || !string.IsNullOrEmpty(actual.UserID) || !string.IsNullOrEmpty(actual.Password) ||
                !string.Equals(actual.DataSource, "localhost", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.InitialCatalog, plan.Sql.CatalogName, StringComparison.Ordinal) ||
                !string.Equals(actual.ConnectionString, expected.ConnectionString, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("SqlServer").GetProperty("DataFilePath").GetString(), plan.Sql.DataFilePath, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("SqlServer").GetProperty("LogFilePath").GetString(), plan.Sql.LogFilePath, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("PrivatePc").GetProperty("LocalApplicationDataRoot").GetString(), plan.Layout.ConfigRoot, StringComparison.Ordinal))
            {
                throw new NativeGoLiveContractException("runtime-configuration-invalid");
            }
            ValidateDisabled(ReadRuntime(root, plan.Layout.RetainedRoot));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        }
    }

    private static NativeGoLiveRuntimeObservation Read(string path, string expectedRetainedRoot)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 64 * 1024)
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return ReadRuntime(document.RootElement, expectedRetainedRoot);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        }
    }

    private static NativeGoLiveRuntimeObservation ReadRuntime(JsonElement root, string expectedRetainedRoot)
    {
        var runtime = root.GetProperty("Runtime");
        if (root.TryGetProperty("SourceRoots", out var sourceRoots) &&
            (sourceRoots.ValueKind != JsonValueKind.Array || sourceRoots.GetArrayLength() != 0))
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        var allowedRoots = root.GetProperty("LocalIngress").GetProperty("AllowedRoots");
        if (allowedRoots.ValueKind != JsonValueKind.Array || allowedRoots.GetArrayLength() != 1 ||
            !string.Equals(allowedRoots[0].GetString(), expectedRetainedRoot, StringComparison.OrdinalIgnoreCase))
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        return new NativeGoLiveRuntimeObservation(
            RequiredBoolean(root, "Outlook", "Enabled") || RequiredBoolean(root, "OutlookCapture", "Enabled"),
            RequiredBoolean(root, "Worker", "Enabled") || Boolean(root, "NativeWorker", "Enabled"),
            RequiredBoolean(runtime, "ModelRuntimeEnabled") || RequiredBoolean(runtime, "OcrEnabled") ||
                RequiredBoolean(runtime, "VisionEnabled") || RequiredBoolean(runtime, "AsrEnabled"),
            RequiredBoolean(runtime, "GpuEnabled"),
            RequiredBoolean(runtime, "FfmpegEnabled"),
            RequiredBoolean(runtime, "NetworkParsingEnabled"));
    }

    private static void ValidateDisabled(NativeGoLiveRuntimeObservation observation)
    {
        if (observation.OutlookEnabled || observation.PhaseSixEnabled || observation.ModelRuntimeEnabled ||
            observation.GpuEnabled || observation.FfmpegEnabled || observation.NetworkParsingEnabled)
        {
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        }
    }

    private static bool Boolean(JsonElement root, string section, string property) =>
        root.TryGetProperty(section, out var value) && Boolean(value, property);

    private static bool Boolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new NativeGoLiveContractException("runtime-configuration-invalid")
        };

    private static bool RequiredBoolean(JsonElement root, string section, string property) =>
        root.TryGetProperty(section, out var value)
            ? RequiredBoolean(value, property)
            : throw new NativeGoLiveContractException("runtime-configuration-invalid");

    private static bool RequiredBoolean(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            throw new NativeGoLiveContractException("runtime-configuration-invalid");
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new NativeGoLiveContractException("runtime-configuration-invalid")
        };
    }
}
