using FluxKnowledge.Web;
using FluxKnowledge.Web.Components;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using System.Text.Json;

const string OutlookConfigurationProjectionSwitch = "--project-outlook-capture-configuration";
var projectOutlookConfiguration = args.Contains(
    OutlookConfigurationProjectionSwitch,
    StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(
    args.Where(argument => !string.Equals(
        argument,
        OutlookConfigurationProjectionSwitch,
        StringComparison.OrdinalIgnoreCase)).ToArray());
if (projectOutlookConfiguration)
{
    Console.WriteLine(JsonSerializer.Serialize(
        OutlookCaptureConfigurationProjection.Create(builder.Configuration)));
    return;
}

builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
WebHostComposition.AddFluxKnowledgeServices(builder.Services, builder.Configuration);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<StatusEventFeed>();
builder.Services.AddSingleton<FluxKnowledge.Application.Ports.IStatusEventPublisher>(
    provider => provider.GetRequiredService<StatusEventFeed>());
builder.Services.AddScoped<OverviewProjectionState>();
builder.Services.AddScoped<CircuitHandler, StatusEventCircuitHandler>();
builder.Services.AddFluxKnowledgeMcp();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<KnowledgeMcpTools>();
var app = builder.Build();

app.UseOutlookOperatorAuthentication();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapFluxKnowledgeHealth();
app.MapFluxKnowledgeIndexHealth();
app.MapFluxKnowledgeGpuStatus();
app.MapFluxKnowledgeSearch();
app.MapFluxKnowledgePipelineRecords();
app.MapMcp("/mcp");

app.Run();

public partial class Program;
