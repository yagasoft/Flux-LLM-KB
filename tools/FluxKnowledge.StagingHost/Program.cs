using System.Net;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Components;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Configuration;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Web.NativeV1;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using FluxKnowledge.Cli.Commands;

var runCli = args.Length >= 5 && args[2] == "--cli";
if (args.Length < 2 || args[0] != "--settings-file" ||
    !runCli && (args.Length > 3 || args.Length == 3 && args[2] != "--migrate"))
    throw new ArgumentException("Use --settings-file <owned Config\\staging.json> [--migrate | --cli <native command>].");
var settings = StagingHostSettings.Load(args[1]);
settings.ValidateBeforeStart();
using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await settings.VerifyDatabaseBindingAsync(startupDeadline.Token);
if (runCli)
{
    using var handler = NativeV1Command.CreateLoopbackTransport();
    using var client = new HttpClient(handler) { BaseAddress = settings.Origin, Timeout = TimeSpan.FromSeconds(25) };
    Environment.ExitCode = await NativeV1Command.ExecuteStagingAsync(args.Skip(3).ToArray(), Console.In,
        client, settings.Origin, Console.Out, Console.Error);
    return;
}
Environment.SetEnvironmentVariable("TEMP", settings.Layout.TempRoot);
Environment.SetEnvironmentVariable("TMP", settings.Layout.TempRoot);
if (args.Length == 3)
{
    // Provisioning is explicitly separate from the runtime login. This connection can only
    // name the already verified staging catalogue; no production configuration is loaded.
    var migrationConnection = new SqlConnectionStringBuilder(settings.RuntimeConnectionString)
    {
        IntegratedSecurity = true, UserID = "", Password = "",
        ApplicationName = "FluxKnowledge.StagingProvisioner"
    };
    var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
        .UseSqlServer(migrationConnection.ConnectionString).Options;
    await using var database = new FluxKnowledgeDbContext(options);
    await database.Database.MigrateAsync();
    var runtimeLogin = new SqlConnectionStringBuilder(settings.RuntimeConnectionString).UserID;
    await database.Database.ExecuteSqlRawAsync("""
        DECLARE @grant nvarchar(max) = N'GRANT VIEW DEFINITION ON FULLTEXT CATALOG::[FluxKnowledge] TO ' + QUOTENAME(@login) +
            N'; GRANT CREATE FULLTEXT CATALOG TO ' + QUOTENAME(@login);
        EXEC sys.sp_executesql @grant;
        """, new SqlParameter("@login", runtimeLogin));
    if (Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_STAGING_SHOWPLAN") == "1")
        await database.Database.ExecuteSqlRawAsync("""
            DECLARE @grant nvarchar(max) = N'GRANT SHOWPLAN TO ' + QUOTENAME(@login);
            EXEC sys.sp_executesql @grant;
            """,new SqlParameter("@login",runtimeLogin));
    var indexState = await database.IndexState.SingleAsync(state => state.Id == 1);
    if (indexState.ActiveIndexGenerationId is null && indexState.EmptyCatalogueValidatedAtUtc is null &&
        !await database.PipelineRecords.AnyAsync() && !await database.Artifacts.AnyAsync() &&
        !await database.Vectors.AnyAsync() && !await database.IndexGenerations.AnyAsync() &&
        !await database.IndexGenerationVectors.AnyAsync())
        await new EmptyCatalogueBootstrapper().ProveAndMarkAsync(database, CancellationToken.None);
    if (settings.IndexPublicationMode == "Segmented" && indexState.RequiredIndexFormat != 2)
    {
        if (indexState.RequiredIndexFormat != 1 || await database.Vectors.AnyAsync() || await database.IndexGenerations.AnyAsync() ||
            await database.PipelineRecords.AnyAsync()) throw new InvalidOperationException("Staging format admission requires an empty catalogue or explicit conversion.");
        indexState.RequiredIndexFormat = 2;
        indexState.ProjectionChangeFloorVersion = indexState.CorpusVersion;
        await database.SaveChangesAsync();
    }
    Console.WriteLine("Staging schema migration completed.");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [], ApplicationName = typeof(App).Assembly.GetName().Name,
    EnvironmentName = Environments.Staging, ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.Sources.Clear();
builder.Configuration.AddConfiguration(settings.Configuration);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, settings.Origin.Port));
WebHostComposition.AddStagingFluxKnowledgeServices(builder.Services, settings);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<StatusEventFeed>();
builder.Services.AddSingleton<FluxKnowledge.Application.Ports.IStatusEventPublisher>(
    provider => provider.GetRequiredService<StatusEventFeed>());
builder.Services.AddScoped<OverviewProjectionState>();
builder.Services.AddScoped<CircuitHandler, StatusEventCircuitHandler>();
builder.Services.AddFluxKnowledgeMcp();
builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<NativeV1McpTools>();
var app = builder.Build();
app.UseNativeRequestResources();
app.Use(async (context, next) =>
{
    if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) ||
        context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != settings.Origin.Port)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers["X-FluxKnowledge-Environment"] = "isolated-staging-functional";
    await next(context);
});
app.UseLocalOperatorLoopbackGate();
app.UseAntiforgery();
app.MapStaticAssets(Path.Combine(AppContext.BaseDirectory, "FluxKnowledge.StagingHost.staticwebassets.endpoints.json"));
app.MapRazorComponents<StagingApp>().AddInteractiveServerRenderMode();
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
app.MapGet("/staging", () => Results.Ok(new
{
    environment = "isolated-staging", validation = "functional-deterministic-embedding",
    catalogue = settings.CatalogName, sourcesRoot = settings.SourcesRoot,
    gpu = "disabled", ocr = "disabled", mail = "disabled"
}));
await app.RunAsync();
