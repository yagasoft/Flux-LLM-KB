using FluxKnowledge.Web;
using FluxKnowledge.Web.Components;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using Microsoft.AspNetCore.Components.Server.Circuits;

var builder = WebApplication.CreateBuilder(args);
WebHostComposition.AddFluxKnowledgeServices(builder.Services, builder.Configuration);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<StatusEventFeed>();
builder.Services.AddSingleton<FluxKnowledge.Application.Ports.IStatusEventPublisher>(
    provider => provider.GetRequiredService<StatusEventFeed>());
builder.Services.AddScoped<IProjectionReader, SqlProjectionReader>();
builder.Services.AddScoped<OverviewProjectionState>();
builder.Services.AddScoped<CircuitHandler, StatusEventCircuitHandler>();
builder.Services.AddFluxKnowledgeMcp();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<KnowledgeMcpTools>();
var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapFluxKnowledgeHealth();
app.MapFluxKnowledgeSearch();
app.MapFluxKnowledgePipelineRecords();
app.MapMcp("/mcp");

app.Run();

public partial class Program;
