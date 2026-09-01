using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

[Collection(NativeGoLiveMachineWideLeaseCollection.Name)]
public sealed class NativeGoLivePreIisProvisioningTests
{
    private const string CanonicalBootstrap =
        "Data Source=localhost;Initial Catalog=master;Integrated Security=True;" +
        "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
        "Pooling=False;Application Name=FluxKnowledge.NativeGoLive";

    [Fact]
    public void Published_migration_dependency_resolution_uses_the_published_payload()
    {
        var path = NativeGoLivePublishedAssemblyImage.ResolvePayloadDependency(
            AppContext.BaseDirectory,
            new AssemblyName("Microsoft.EntityFrameworkCore"));

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Microsoft.EntityFrameworkCore.dll"), path);
    }

    [Fact]
    public void Production_configuration_validation_rejects_bytes_that_are_not_the_canonical_payload()
    {
        var layout = LiveRootLayout.CreateForIsolatedTests(
            Path.Combine(Path.GetTempPath(), "FluxKnowledgeCanonicalConfig", Guid.NewGuid().ToString("N")));
        var plan = NativeGoLivePlan.CreateForIsolatedTests(layout, new string('a', 40));
        var bootstrap = NativeGoLiveSqlBootstrap.Parse(CanonicalBootstrap);
        var canonical = NativeGoLiveProductionConfigurationSerializer.Serialize(plan, bootstrap);
        var semanticallyEquivalent = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical) + "\n");

        Assert.Throws<NativeGoLiveContractException>(() =>
            NativeGoLiveRuntimeConfiguration.ValidateProductionConfiguration(
                semanticallyEquivalent,
                plan,
                NativeGoLiveProductionConfigurationSerializer.CreateConnectionString(plan, bootstrap)));
    }

    [Fact]
    public void Every_native_child_start_removes_the_bootstrap_connection_from_its_environment()
    {
        var prior = Environment.GetEnvironmentVariable(
            NativeGoLiveSqlBootstrap.EnvironmentVariable,
            EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(
                NativeGoLiveSqlBootstrap.EnvironmentVariable,
                "bootstrap-sentinel",
                EnvironmentVariableTarget.Process);

            var codex = NativeGoLiveChildStartBuilder.Create("codex");
            var composition = NativeGoLiveChildStartBuilder.Create("dotnet");

            Assert.False(codex.Environment.ContainsKey(NativeGoLiveSqlBootstrap.EnvironmentVariable));
            Assert.False(composition.Environment.ContainsKey(NativeGoLiveSqlBootstrap.EnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                NativeGoLiveSqlBootstrap.EnvironmentVariable,
                prior,
                EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task Published_configuration_and_no_listener_composition_validation_complete_before_IIS_start()
    {
        using var fixture = new PublishedCompositionFixture();
        await using var lease = await fixture.AcquireLeaseAsync();

        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        Assert.Equal(
            ["publish", "write-production-configuration", "validate-published-composition", "start-iis"],
            fixture.Events);
    }

    [Fact]
    public async Task Independent_guarded_host_instances_are_machine_wide_excluded_before_any_mutation()
    {
        using var first = new PublishedCompositionFixture();
        using var second = new PublishedCompositionFixture();
        await using var lease = await first.AcquireLeaseAsync();

        await Assert.ThrowsAsync<NativeGoLiveLeaseUnavailableException>(
            () => second.AcquireLeaseAsync().AsTask());

        Assert.Empty(second.Events);
    }

    [Fact]
    public async Task Direct_TCP_non_loopback_denial_is_evaluated_only_after_IIS_starts_the_loopback_binding()
    {
        var events = new List<string>();
        var transport = new NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport();
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionRefused));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();

        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);
        await fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None);

        Assert.True(events.IndexOf("start-iis") < events.IndexOf("probe-non-loopback-tcp"));
        Assert.Equal(8, transport.Requests.Count);
        Assert.All(transport.Requests, request =>
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
    }

    [Fact]
    public async Task Loopback_health_without_the_native_proof_marker_fails_closed()
    {
        var port = new NativeGoLiveWindowsLoopbackPort(
            new UnmarkedLoopbackTransport(),
            () => [],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe());
        using var fixture = new PublishedCompositionFixture(loopback: port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("loopback-native-proof-marker-missing", exception.Message);
    }

    [Fact]
    public async Task An_HTTP_sys_non_loopback_TCP_handshake_with_a_complete_non_application_response_is_accepted()
    {
        var events = new List<string>();
        var transport = new HttpSysDenialTransport(400, "Microsoft-HTTPAPI/2.0");
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("198.51.100.20")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        await fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None);

        Assert.Equal(12, transport.Requests.Count);
        Assert.Equal(4, transport.Requests.Count(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
        Assert.Equal(
            ["/health/live", "/health/ready", "/health/live", "/health/ready"],
            transport.Requests
                .Where(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
                .Select(request => request.Uri.AbsolutePath)
                .ToArray());
        Assert.True(events.IndexOf("start-iis") < events.IndexOf("probe-non-loopback-tcp"));
    }

    [Theory]
    [InlineData(400, "Microsoft-HTTPAPI/2.0")]
    [InlineData(404, "Microsoft-IIS/10.0")]
    public async Task A_complete_non_application_HTTP_sys_denial_response_is_accepted_for_every_non_loopback_candidate(
        int statusCode,
        string serverHeader)
    {
        var events = new List<string>();
        var transport = new HttpSysDenialTransport(statusCode, serverHeader);
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("198.51.100.20")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        await fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None);

        Assert.Equal(4, transport.Requests.Count(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
        Assert.True(events.IndexOf("start-iis") < events.IndexOf("probe-non-loopback-tcp"));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task An_asymmetric_non_loopback_no_application_result_fails_the_guarded_live_validation(
        string noApplicationPath)
    {
        var events = new List<string>();
        var transport = new AsymmetricNonLoopbackTransport(noApplicationPath);
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("non-loopback-application-denial-contract-failed", exception.Message);
        Assert.Equal(
            ["/health/live", "/health/ready"],
            transport.Requests
                .Where(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
                .Select(request => request.Uri.AbsolutePath)
                .ToArray());
    }

    [Fact]
    public async Task A_non_loopback_native_application_health_response_fails_the_guarded_live_validation()
    {
        var events = new List<string>();
        var transport = new NonLoopbackApplicationTransport();
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("198.51.100.20")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("non-loopback-application-denial-contract-failed", exception.Message);
        Assert.Equal(4, transport.Requests.Count(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
        Assert.Equal(
            ["/health/live", "/health/ready", "/health/live", "/health/ready"],
            transport.Requests
                .Where(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
                .Select(request => request.Uri.AbsolutePath)
                .ToArray());
    }

    [Fact]
    public async Task An_ambiguous_non_loopback_HTTP_response_fails_closed()
    {
        var events = new List<string>();
        var transport = new AmbiguousNonLoopbackTransport();
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("non-loopback-application-denial-contract-failed", exception.Message);
        Assert.Equal(2, transport.Requests.Count(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
    }

    [Fact]
    public async Task A_redirected_non_loopback_HTTP_response_fails_closed()
    {
        var events = new List<string>();
        var transport = new RedirectingNonLoopbackTransport();
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                events,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("non-loopback-application-denial-contract-failed", exception.Message);
        Assert.Equal(2, transport.Requests.Count(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
    }

    [Fact]
    public async Task An_ambiguous_non_loopback_TCP_socket_error_fails_the_guarded_live_validation()
    {
        var events = new List<string>();
        var transport = new NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport();
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new ThrowingTcpProbe());
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("non-loopback-application-denial-contract-failed", exception.Message);
    }

    [Fact]
    public async Task Incomplete_non_loopback_candidate_enumeration_fails_the_guarded_live_validation()
    {
        var events = new List<string>();
        var port = new NativeGoLiveWindowsLoopbackPort(
            new NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport(),
            () => throw new NetworkInformationException(),
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(
                NativeGoLiveTcpConnectionOutcome.ConnectionRefused));
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("non-loopback-application-denial-contract-failed", exception.Message);
    }

    [Fact]
    public async Task A_complete_enumeration_with_no_active_non_loopback_candidate_is_accepted()
    {
        var events = new List<string>();
        var port = new NativeGoLiveWindowsLoopbackPort(
            new NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport(),
            () => [],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe());
        using var fixture = new PublishedCompositionFixture(events, port);
        await using var lease = await fixture.AcquireLeaseAsync();
        await fixture.Host.PublishAndStartAsync(fixture.Plan, CancellationToken.None);

        await fixture.Host.ValidateAsync(fixture.Plan, CancellationToken.None);
    }

    private static NativeGoLiveWindowsLoopbackPort CreateLoopbackPort(
        List<string> events,
        NativeGoLiveTcpConnectionOutcome outcome) => new(
            new NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport(),
            () => [IPAddress.Parse("192.0.2.10")],
            new NativeGoLiveRuntimeObservation(false, false, false, false, false, false),
            new NativeGoLiveLoopbackDenialTests.StaticTcpProbe(events, outcome));

    private sealed class PublishedCompositionFixture : IDisposable
    {
        private readonly string _root;

        public PublishedCompositionFixture(
            List<string>? events = null,
            INativeGoLiveLoopbackPort? loopback = null)
        {
            Events = events ?? [];
            _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgePreIis", Guid.NewGuid().ToString("N"));
            var payloadRoot = Path.Combine(_root, "payload");
            Directory.CreateDirectory(payloadRoot);
            File.WriteAllText(Path.Combine(payloadRoot, "payload.dll"), "one-shot-payload");
            Plan = NativeGoLivePlan.CreateForIsolatedTests(
                LiveRootLayout.CreateForIsolatedTests(Path.Combine(_root, "live")),
                new string('a', 40));
            Directory.CreateDirectory(Plan.Layout.ApplicationRoot);

            var manifest = NativeGoLivePayloadHasher.Compute(payloadRoot);
            var issuer = new NativeGoLiveCloseoutCapabilityIssuer();
            _capability = issuer.Issue(Plan, payloadRoot, manifest.Sha256);
            _request = new NativeGoLiveRequest(
                Plan, false, true, true, true, true, payloadRoot, manifest.Sha256, manifest);
            var recordedEvents = Events;
            var ports = new NativeGoLiveHostPorts(
                null!,
                new RecordingIisPort(Plan.AppPoolName, recordedEvents),
                new RecordingOwnedStatePort(recordedEvents),
                null!,
                new RecordingAclPort(Plan, recordedEvents),
                new RecordingPublishPort(recordedEvents),
                loopback ?? null!,
                null!,
                null!,
                new RecordingCompositionPort(recordedEvents));
            Host = new GuardedNativeGoLiveHost(
                _capability,
                Plan,
                payloadRoot,
                NativeGoLiveSqlBootstrap.Parse(CanonicalBootstrap),
                ports);
        }

        public NativeGoLivePlan Plan { get; }
        public GuardedNativeGoLiveHost Host { get; }
        public List<string> Events { get; }
        private readonly NativeGoLiveCloseoutCapability _capability;
        private readonly NativeGoLiveRequest _request;

        public ValueTask<INativeGoLiveLease> AcquireLeaseAsync()
        {
            Assert.True(_capability.TryBeginExecution());
            return Host.AcquireLeaseAsync(_request, CancellationToken.None);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingIisPort(string appPoolName, List<string> events) : INativeGoLiveIisPort
    {
        public ValueTask<NativeGoLivePoolObservation> StopAsync(string _, CancellationToken __) =>
            throw new NotSupportedException();
        public ValueTask<NativeGoLivePoolObservation> RestoreAsync(string _, CancellationToken __) =>
            throw new NotSupportedException();
        public ValueTask<NativeGoLivePoolObservation> StartAsync(string _, CancellationToken __)
        {
            events.Add("start-iis");
            return ValueTask.FromResult(new NativeGoLivePoolObservation(appPoolName, false, "Started", true));
        }
    }

    private sealed class ThrowingTcpProbe : INativeGoLiveTcpProbe
    {
        public ValueTask<NativeGoLiveTcpConnectionOutcome> ProbeAsync(
            IPAddress _,
            int __,
            CancellationToken ___) =>
            ValueTask.FromException<NativeGoLiveTcpConnectionOutcome>(
                new SocketException((int)SocketError.NetworkUnreachable));
    }

    private sealed class NonLoopbackApplicationTransport : INativeGoLiveHttpTransport
    {
        private readonly NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport _loopback = new();

        public List<NativeGoLiveHttpRequestSpec> Requests { get; } = [];

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
            {
                return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                    200,
                    new NativeGoLiveHttpPeer(request.Uri.Host, "127.0.0.1"),
                    "{}",
                    UsedProxy: false,
                    FollowedRedirect: false,
                    Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [NativeGoLiveLoopbackContract.NativeProofHeader] = NativeGoLiveLoopbackContract.NativeProofValue
                    }));
            }

            return _loopback.SendAsync(request, cancellationToken);
        }
    }

    private sealed class UnmarkedLoopbackTransport : INativeGoLiveHttpTransport
    {
        private readonly NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport _inner = new(includeNativeProofMarker: false);

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken cancellationToken) =>
            _inner.SendAsync(request, cancellationToken);
    }

    private sealed class HttpSysDenialTransport(int statusCode, string serverHeader) : INativeGoLiveHttpTransport
    {
        private readonly NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport _loopback = new();

        public List<NativeGoLiveHttpRequestSpec> Requests { get; } = [];

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
                return _loopback.SendAsync(request, cancellationToken);
            return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                statusCode,
                new NativeGoLiveHttpPeer(request.Uri.Host, "127.0.0.1"),
                "Bad Request",
                UsedProxy: false,
                FollowedRedirect: false,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Server"] = serverHeader
                }));
        }
    }

    private sealed class AmbiguousNonLoopbackTransport : INativeGoLiveHttpTransport
    {
        private readonly NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport _loopback = new();

        public List<NativeGoLiveHttpRequestSpec> Requests { get; } = [];

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
            {
                return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                    404,
                    new NativeGoLiveHttpPeer(request.Uri.Host, "127.0.0.1"),
                    "unrelated HTTP response",
                    UsedProxy: false,
                    FollowedRedirect: false));
            }

            return _loopback.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RedirectingNonLoopbackTransport : INativeGoLiveHttpTransport
    {
        private readonly NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport _loopback = new();

        public List<NativeGoLiveHttpRequestSpec> Requests { get; } = [];

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
                return _loopback.SendAsync(request, cancellationToken);
            return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                400,
                new NativeGoLiveHttpPeer(request.Uri.Host, "127.0.0.1"),
                "Bad Request",
                UsedProxy: false,
                FollowedRedirect: true,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Server"] = "Microsoft-HTTPAPI/2.0"
                }));
        }
    }

    private sealed class AsymmetricNonLoopbackTransport(string noApplicationPath) : INativeGoLiveHttpTransport
    {
        private readonly NativeGoLiveLoopbackDenialTests.LoopbackOnlyTransport _loopback = new();

        public List<NativeGoLiveHttpRequestSpec> Requests { get; } = [];

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
                return _loopback.SendAsync(request, cancellationToken);
            if (string.Equals(request.Uri.AbsolutePath, noApplicationPath, StringComparison.Ordinal))
                return ValueTask.FromException<NativeGoLiveRawHttpResponse>(
                    new SocketException((int)SocketError.ConnectionReset));
            return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                200,
                new NativeGoLiveHttpPeer(request.Uri.Host, "127.0.0.1"),
                "{}",
                UsedProxy: false,
                FollowedRedirect: false));
        }
    }

    private sealed class RecordingOwnedStatePort(List<string> events) : INativeGoLiveOwnedStatePort
    {
        public ValueTask WipeRootAsync(CancellationToken _) => throw new NotSupportedException();
        public ValueTask CreateEmptyRootAsync(CancellationToken _) => throw new NotSupportedException();
        public ValueTask WriteProductionConfigurationAsync(CancellationToken _)
        {
            events.Add("write-production-configuration");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPublishPort(List<string> events) : INativeGoLivePublishPort
    {
        public ValueTask PublishAsync(string source, string destination, CancellationToken _)
        {
            events.Add("publish");
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCompositionPort(List<string> events) : INativeGoLiveCompositionPort
    {
        public ValueTask ValidatePublishedCompositionAsync(NativeGoLivePlan _, CancellationToken __)
        {
            events.Add("validate-published-composition");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAclPort(NativeGoLivePlan plan, List<string> events) : INativeGoLiveAclPort
    {
        public ValueTask<NativeGoLiveAclObservation> ApplyAndObserveAsync(NativeGoLivePlan _, CancellationToken __) =>
            throw new NotSupportedException();

        public ValueTask<NativeGoLiveAclObservation> ObserveEffectiveAsync(NativeGoLivePlan _, CancellationToken __)
        {
            events.Add("validate-acls");
            return ValueTask.FromResult(ExactAcl(plan));
        }
    }

    private static NativeGoLiveAclObservation ExactAcl(NativeGoLivePlan plan)
    {
        const string AppPoolSid = "S-1-5-21-100-200-300-400";
        const string SqlServiceSid = "S-1-5-21-500-600-700-800";
        const long Read = 131209;
        const long ReadAndExecute = 131241;
        const long Modify = 197055;
        const long FullControl = 2032127;
        var layout = plan.Layout;
        var modifyPaths = new[]
        {
            Path.Combine(layout.ConfigRoot, "data-protection"), layout.IndexRoot, layout.RetainedRoot,
            layout.SpoolRoot, layout.TempRoot, layout.LogsRoot
        };
        var boundaryPaths = new[]
        {
            layout.Root, layout.DataRoot, layout.SqlRoot, layout.RuntimeRoot, layout.CodexPluginRoot, layout.RecoveryRoot
        };
        NativeGoLiveAclPathObservation PathWithRules(string path, params (string Sid, long Rights)[] additions) =>
            new(path, true,
            [
                Ace("S-1-5-18", FullControl),
                Ace("S-1-5-32-544", FullControl),
                .. additions.Select(item => Ace(item.Sid, item.Rights))
            ]);
        var paths = boundaryPaths.Select(path => PathWithRules(path))
            .Append(PathWithRules(layout.ApplicationRoot, (AppPoolSid, ReadAndExecute)))
            .Append(PathWithRules(layout.ConfigRoot, (AppPoolSid, Read)))
            .Concat(modifyPaths.Select(path => PathWithRules(path, (AppPoolSid, Modify))))
            .Append(PathWithRules(layout.SqlDataRoot, (SqlServiceSid, Modify)))
            .Append(PathWithRules(layout.SqlLogRoot, (SqlServiceSid, Modify)))
            .ToArray();
        return new NativeGoLiveAclObservation(
            [layout.SqlDataRoot, layout.SqlLogRoot],
            [layout.ApplicationRoot],
            [layout.ConfigRoot],
            modifyPaths,
            false, false, false, false, true,
            AppPoolSid,
            SqlServiceSid,
            paths);
    }

    private static NativeGoLiveAclAceObservation Ace(string sid, long rights) =>
        new(sid, rights, true, false, 3, 0, true, true, true);
}
