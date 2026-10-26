using System.Net;
using System.Net.Http.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class SourceJournalHelperHealthTests
{
    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    [Theory]
    [InlineData("remote", HttpStatusCode.Forbidden)]
    [InlineData("forwarded", HttpStatusCode.Forbidden)]
    [InlineData("unheld", HttpStatusCode.Conflict)]
    [InlineData("direct-reader", HttpStatusCode.ServiceUnavailable)]
    [InlineData("missing-repair", HttpStatusCode.ServiceUnavailable)]
    public async Task Ineligible_requests_do_not_touch_either_pipe(string mode, HttpStatusCode expected)
    {
        var fake = new ProbeClients();
        await using var app = await StartAsync(fake, mode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/source-journal-helper");
        if (mode == "forwarded") request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        using var response = await app.GetTestClient().SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, fake.Queries + fake.Reads + fake.Repairs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Held_probe_uses_a_bounded_query_and_read_and_preserves_the_repair_mode(bool repairEnabled)
    {
        var fake = new ProbeClients();
        await using var app = await StartAsync(fake, repairEnabled ? "valid" : "repair-disabled");
        using var response = await app.GetTestClient().GetAsync("/health/source-journal-helper");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ProbeResult>();
        Assert.NotNull(result);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, result.VolumesChecked);
        Assert.Equal(repairEnabled ? "NotApproved" : "Disabled", result.RepairTransport);
        Assert.Equal((1, 1, repairEnabled ? 1 : 0), (fake.Queries, fake.Reads, fake.Repairs));
        if (repairEnabled) Assert.Equal(new SourceAccessRepairRequest("", null, "probe", false), fake.LastRepair);
        else Assert.Null(fake.LastRepair);
        Assert.Equal(new JournalCursor(Volume, 17, 100), fake.LastCursor);
        Assert.Equal(65536, fake.MaximumBytes);
        Assert.True(fake.Deadline.CanBeCanceled);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("wrong-volume")]
    [InlineData("wrong-cursor")]
    [InlineData("backwards-cursor")]
    [InlineData("repair-unavailable")]
    [InlineData("repair-readable")]
    [InlineData("deadline")]
    public async Task Unconfirmed_transport_never_reports_success(string fault)
    {
        var fake = new ProbeClients { Fault = fault };
        await using var app = await StartAsync(fake);
        using var response = await app.GetTestClient().GetAsync("/health/source-journal-helper");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        if (fault is "unavailable" or "wrong-volume" or "wrong-cursor" or "backwards-cursor" or "deadline")
            Assert.Equal(0, fake.Repairs);
    }

    [Fact]
    public async Task Hold_disappearance_during_probe_refuses_completion()
    {
        var fake = new ProbeClients();
        var hold = new ProbeHold();
        fake.AfterRead = () => hold.Held = false;
        await using var app = await StartAsync(fake, hold: hold);
        using var response = await app.GetTestClient().GetAsync("/health/source-journal-helper");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, fake.Repairs);
    }

    private static async Task<WebApplication> StartAsync(ProbeClients fake, string mode = "valid", ProbeHold? hold = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["SourceAccess:Enabled"] = mode == "repair-disabled" ? "false" : "true" });
        builder.Services.AddSingleton(new SourceJournalOptions
        {
            Enabled = true, PipeName = mode == "direct-reader" ? null : "FluxKnowledge.SourceJournal.test",
            ApprovedVolumes = [Volume]
        });
        builder.Services.AddSingleton<ISourceChangeJournalReader>(fake);
        if (mode is not ("missing-repair" or "repair-disabled")) builder.Services.AddSingleton<ISourceAccessRepairClient>(fake);
        builder.Services.AddSingleton<IDeploymentValidationHold>(hold ?? new ProbeHold { Held = mode != "unheld" });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = mode == "remote" ? IPAddress.Parse("192.0.2.1") : IPAddress.Loopback;
            await next(context);
        });
        app.MapFluxKnowledgeSourceJournalHelperHealth();
        await app.StartAsync();
        return app;
    }

    private sealed record ProbeResult(int Version, int VolumesChecked, string RepairTransport, string? CallerSid);
    private sealed class ProbeHold : IDeploymentValidationHold
    {
        public bool Held { get; set; } = true;
        public bool IsHeld => Held;
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class ProbeClients : ISourceChangeJournalReader, ISourceAccessRepairClient
    {
        public string? Fault { get; init; }
        public int Queries, Reads, Repairs, MaximumBytes;
        public CancellationToken Deadline;
        public JournalCursor? LastCursor;
        public SourceAccessRepairRequest? LastRepair;
        public Action? AfterRead;
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken)
        {
            Queries++;
            if (Fault == "unavailable") throw new IOException("Synthetic unavailable journal");
            return ValueTask.FromResult(new JournalCapability(Fault == "wrong-volume" ? "wrong" : volumeIdentity, 17, 10, 100, 10, 1000, 100));
        }
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken)
        {
            Reads++; LastCursor = cursor; MaximumBytes = maximumBytes; Deadline = cancellationToken;
            if (Fault == "deadline") throw new OperationCanceledException("Synthetic helper deadline");
            AfterRead?.Invoke();
            return ValueTask.FromResult(new JournalPage(Fault == "wrong-cursor" ? cursor with { JournalId = 18 } : cursor,
                cursor with { NextUsn = Fault == "backwards-cursor" ? 99 : 101 }, []));
        }
        public SourceAccessRepairOutcome TryRepair(SourceAccessRepairRequest request, CancellationToken cancellationToken)
        {
            Repairs++; LastRepair = request;
            return Fault switch
            {
                "repair-unavailable" => SourceAccessRepairOutcome.Unavailable,
                "repair-readable" => SourceAccessRepairOutcome.AlreadyReadable,
                _ => SourceAccessRepairOutcome.NotApproved
            };
        }
    }
}
