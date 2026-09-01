using System.Net;
using System.Net.Sockets;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

[Collection(NativeGoLiveMachineWideLeaseCollection.Name)]
public sealed class NativeGoLiveLoopbackDenialTests
{
    [Fact]
    public async Task Connection_refused_by_a_non_loopback_address_is_accepted_without_an_HTTP_response()
    {
        var transport = new LoopbackOnlyTransport();
        var tcpProbe = new StaticTcpProbe(
            NativeGoLiveTcpConnectionOutcome.ConnectionRefused,
            NativeGoLiveTcpConnectionOutcome.ConnectionRefused);
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("198.51.100.20")],
            DisabledRuntime(),
            tcpProbe);

        var observation = await port.ObserveAsync(CancellationToken.None);

        Assert.Equal(8, transport.Requests.Count);
        Assert.All(transport.Requests, request =>
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
        Assert.Equal(2, observation.NonLoopbackDenial.CandidateCount);
        Assert.Equal(2, observation.NonLoopbackDenial.ConnectionRefusedCount);
        Assert.Equal(0, observation.NonLoopbackDenial.ConnectionSucceededCount);
        Assert.Equal(0, observation.NonLoopbackDenial.IndeterminateCount);
        Assert.Equal(2, tcpProbe.ProbedAddresses.Count);
        Assert.All(tcpProbe.ProbedAddresses, address => Assert.False(IPAddress.IsLoopback(address)));
        Assert.False(observation.UsedProxy);
        Assert.False(observation.FollowedRedirect);
    }

    [Fact]
    public async Task MCP_validation_requests_accept_JSON_and_event_stream()
    {
        var transport = new LoopbackOnlyTransport();
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [],
            DisabledRuntime(),
            new StaticTcpProbe());

        _ = await port.ObserveAsync(CancellationToken.None);

        var mcpRequests = transport.Requests.Where(request => request.Uri.AbsolutePath == "/mcp").ToArray();
        Assert.Equal(2, mcpRequests.Length);
        Assert.All(mcpRequests, request =>
            Assert.Equal("application/json, text/event-stream", request.Headers["Accept"]));
    }

    [Fact]
    public async Task HTTP_sys_handshakes_with_a_complete_non_application_response_are_treated_as_non_loopback_denial()
    {
        var transport = new LoopbackOnlyTransport(returnHttpSysDenialForNonLoopback: true);
        var port = new NativeGoLiveWindowsLoopbackPort(
            transport,
            () => [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("198.51.100.20")],
            DisabledRuntime(),
            new StaticTcpProbe(
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded,
                NativeGoLiveTcpConnectionOutcome.ConnectionSucceeded));

        var observation = await port.ObserveAsync(CancellationToken.None);

        Assert.Equal(12, transport.Requests.Count);
        Assert.Equal(4, transport.Requests.Count(request => !IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host))));
        Assert.Equal(2, observation.NonLoopbackDenial.ConnectionRefusedCount);
        Assert.Equal(0, observation.NonLoopbackDenial.ConnectionSucceededCount);
        Assert.Equal(0, observation.NonLoopbackDenial.IndeterminateCount);
    }

    internal sealed class LoopbackOnlyTransport(
        bool includeNativeProofMarker = true,
        bool returnHttpSysDenialForNonLoopback = false) : INativeGoLiveHttpTransport
    {
        public List<NativeGoLiveHttpRequestSpec> Requests { get; } = [];

        public ValueTask<NativeGoLiveRawHttpResponse> SendAsync(
            NativeGoLiveHttpRequestSpec request,
            CancellationToken _)
        {
            Requests.Add(request);
            if (!IPAddress.IsLoopback(IPAddress.Parse(request.Uri.Host)))
            {
                if (!returnHttpSysDenialForNonLoopback)
                    throw new SocketException((int)SocketError.ConnectionRefused);
                return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                    400,
                    new NativeGoLiveHttpPeer(request.Uri.Host, "127.0.0.1"),
                    "Bad Request",
                    UsedProxy: false,
                    FollowedRedirect: false,
                    Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Server"] = "Microsoft-HTTPAPI/2.0"
                    }));
            }

            return ValueTask.FromResult(new NativeGoLiveRawHttpResponse(
                request.Uri.AbsolutePath == "/health/live" && request.Headers.Count != 0 ? 403 : 200,
                new NativeGoLiveHttpPeer("127.0.0.1", "127.0.0.1"),
                BodyFor(request),
                UsedProxy: false,
                FollowedRedirect: false,
                Headers: includeNativeProofMarker && request.Uri.AbsolutePath is "/health/live" or "/health/ready"
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [NativeGoLiveLoopbackContract.NativeProofHeader] = NativeGoLiveLoopbackContract.NativeProofValue
                    }
                    : null));
        }

        private static string BodyFor(NativeGoLiveHttpRequestSpec request) => request.Uri.AbsolutePath switch
        {
            "/api/index-health" =>
                "{\"state\":\"Healthy\",\"activeGeneration\":null,\"failureCategory\":null,\"cleanedCandidateCount\":0}",
            "/api/gpu-status" =>
                "{\"readyCount\":0,\"activeCount\":0,\"deferredCount\":0,\"outcomeUncertainCount\":0,\"hasActiveBatch\":false}",
            "/api/v1/knowledge/search" => "{\"ok\":true,\"result\":{\"items\":[]}}",
            "/mcp" when request.JsonBody!.Contains("\"id\":1", StringComparison.Ordinal) =>
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"FluxKnowledge\",\"version\":\"1\"},\"capabilities\":{}}}",
            "/mcp" =>
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"knowledge.search\"},{\"name\":\"knowledge.write\"},{\"name\":\"knowledge.graph\"},{\"name\":\"code.query\"},{\"name\":\"code.write\"},{\"name\":\"corpus.query\"},{\"name\":\"corpus.write\"},{\"name\":\"operations.status\"},{\"name\":\"operations.audit\"}]}}",
            _ => "{}"
        };
    }

    internal sealed class StaticTcpProbe : INativeGoLiveTcpProbe
    {
        private readonly Queue<NativeGoLiveTcpConnectionOutcome> _outcomes;
        private readonly List<string>? _events;

        public StaticTcpProbe(params NativeGoLiveTcpConnectionOutcome[] outcomes)
        {
            _outcomes = new(outcomes);
        }

        public StaticTcpProbe(List<string> events, params NativeGoLiveTcpConnectionOutcome[] outcomes)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _outcomes = new(outcomes);
        }

        public List<IPAddress> ProbedAddresses { get; } = [];

        public ValueTask<NativeGoLiveTcpConnectionOutcome> ProbeAsync(
            IPAddress address,
            int port,
            CancellationToken _)
        {
            Assert.Equal(NativeGoLivePlan.NativeLoopbackPort, port);
            _events?.Add("probe-non-loopback-tcp");
            ProbedAddresses.Add(address);
            return ValueTask.FromResult(_outcomes.Dequeue());
        }
    }

    private static NativeGoLiveRuntimeObservation DisabledRuntime() => new(false, false, false, false, false, false);
}
