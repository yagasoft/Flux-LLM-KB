using FluxKnowledge.Web;
using FluxKnowledge.Web.Components;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

const string OutlookConfigurationProjectionSwitch = "--project-outlook-capture-configuration";
const string NativeGoLiveCompositionValidationSwitch = "--validate-native-go-live-composition";
const string NativeGoLiveMigrationSwitch = "--apply-native-go-live-migrations";
var projectOutlookConfiguration = args.Contains(
    OutlookConfigurationProjectionSwitch,
    StringComparer.OrdinalIgnoreCase);
var validateNativeGoLiveComposition = args.Contains(
    NativeGoLiveCompositionValidationSwitch,
    StringComparer.OrdinalIgnoreCase);
var applyNativeGoLiveMigrations = args.Contains(
    NativeGoLiveMigrationSwitch,
    StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(
    args.Where(argument =>
        !string.Equals(argument, OutlookConfigurationProjectionSwitch, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(argument, NativeGoLiveCompositionValidationSwitch, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(argument, NativeGoLiveMigrationSwitch, StringComparison.OrdinalIgnoreCase)).ToArray());
if (applyNativeGoLiveMigrations)
{
    var connection = Environment.GetEnvironmentVariable(
        "FLUXKNOWLEDGE_NATIVE_GO_LIVE_MIGRATION_CONNECTION",
        EnvironmentVariableTarget.Process);
    if (string.IsNullOrWhiteSpace(connection)) return;
    try
    {
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection)
            .Options;
        await using var context = new FluxKnowledgeDbContext(options);
        await context.Database.MigrateAsync();
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            "FLUXKNOWLEDGE_NATIVE_GO_LIVE_MIGRATION_CONNECTION",
            null,
            EnvironmentVariableTarget.Process);
    }
    return;
}
if (!WebHostComposition.IsIsolatedTestComposition)
{
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddConfiguration(
        WebHostComposition.LoadCanonicalProductionConfiguration(
            FluxKnowledge.Web.Configuration.FileSystemNoFollowPathOpener.Instance));
}
if (projectOutlookConfiguration)
{
    Console.WriteLine(JsonSerializer.Serialize(
        OutlookCaptureConfigurationProjection.Create(builder.Configuration)));
    return;
}

WebHostComposition.AddFluxKnowledgeServices(builder.Services, builder.Configuration);
if (validateNativeGoLiveComposition)
{
    WebHostComposition.ValidateNativeGoLiveComposition(builder.Services, builder.Configuration);
    return;
}
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
    .WithTools<NativeV1McpTools>();
var app = builder.Build();
if (!WebHostComposition.IsIsolatedTestComposition)
{
    await WebHostComposition.InitialiseStrictProductionRecoveryAsync(
        app.Services,
        app.Lifetime.ApplicationStopping);
}

app.UseLocalOperatorLoopbackGate();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapFluxKnowledgeHealth();
app.MapFluxKnowledgeIndexHealth();
app.MapFluxKnowledgeGpuStatus();
app.MapFluxKnowledgeSearch();
app.MapFluxKnowledgePipelineRecords();
app.MapFluxKnowledgeOperatorActions();
app.MapFluxKnowledgeLocalRetainedDetails();
app.MapFluxKnowledgeLocalRetainedCsharpCode();
app.MapFluxKnowledgeNativeV1();
app.MapMcp("/mcp");

app.Run();

public partial class Program;
