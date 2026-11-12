using System.Net;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Application.Ports;
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
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference.Search;
using System.Text.Json;

var mode = StagingHostArguments.ParseMode(args);
var normalRuntime = mode == StagingHostMode.NormalRuntime;
var checkNormalRuntime=mode==StagingHostMode.CheckNormalRuntime;
var candidateQuality = mode == StagingHostMode.CandidateQuality;
var indexingTrial=mode==StagingHostMode.ExpandedGpuIndexing || normalRuntime;
var fixtureCleanup=mode==StagingHostMode.IndexFixtureCleanup;
var expandedGpu = mode is StagingHostMode.ExpandedGpu or StagingHostMode.ExpandedGpuQualification or StagingHostMode.ExpandedGpuIndexing;
var maximumGpuCohorts=mode is StagingHostMode.ExpandedGpuQualification or StagingHostMode.ExpandedGpuIndexing or StagingHostMode.NormalRuntime?21:1;
var payloadReadiness = mode == StagingHostMode.PayloadReadiness;
if ((expandedGpu || normalRuntime || payloadReadiness || fixtureCleanup) && Console.ReadLine() != "approved-bounded-staging-start")
    throw new InvalidOperationException("Expanded GPU staging requires its owned-job startup handshake.");
var controllerNonce=Guid.Empty;
if((indexingTrial || fixtureCleanup) && (!Guid.TryParse(Console.ReadLine(),out controllerNonce) || controllerNonce==Guid.Empty))
    throw new InvalidOperationException("Private indexing requires its bound controller nonce.");
var settings = StagingHostSettings.Load(args[1]);
settings.ValidateBeforeStart();
if ((candidateQuality || payloadReadiness || indexingTrial || fixtureCleanup) && (!settings.CpuSemantic || !settings.DashboardReadBenchmark))
    throw new ArgumentException("Candidate quality requires isolated CPU semantic staging with application writers disabled.");
using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await settings.VerifyDatabaseBindingAsync(startupDeadline.Token);
var trialScheduler = expandedGpu ? StagingGpuSchedulerBinding.Load(settings) : null;
if (trialScheduler is not null) await trialScheduler.VerifyAsync(startupDeadline.Token);
var normalBinding = normalRuntime || checkNormalRuntime ? await StagingNormalRuntimeBinding.LoadVerifiedAsync(settings, args[4], startupDeadline.Token,args.Length==7?args[6]:null) : null;
if(checkNormalRuntime)
{
    Console.WriteLine(JsonSerializer.Serialize(new {status="verified",catalogue=settings.CatalogName,normalBinding!.Generation,normalBinding.Version,modelsStarted=false,httpStarted=false,productionOperations=0}));
    return;
}
if (mode == StagingHostMode.Cli)
{
    using var handler = NativeV1Command.CreateLoopbackTransport();
    using var client = new HttpClient(handler) { BaseAddress = settings.Origin, Timeout = TimeSpan.FromSeconds(25) };
    if (Guid.TryParse(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_STAGING_OPERATION"), out var cliOperation) && cliOperation != Guid.Empty)
        client.DefaultRequestHeaders.Add("X-Flux-Staging-Operation", cliOperation.ToString("D"));
    Environment.ExitCode = await NativeV1Command.ExecuteStagingAsync(args.Skip(3).ToArray(), Console.In,
        client, settings.Origin, Console.Out, Console.Error);
    return;
}
Environment.SetEnvironmentVariable("TEMP", settings.Layout.TempRoot);
Environment.SetEnvironmentVariable("TMP", settings.Layout.TempRoot);
if (mode == StagingHostMode.Migrate)
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
if (fixtureCleanup) StagingNormalGpuComposition.AddCleanupProfile(builder.Services);
if (expandedGpu) StagingExpandedGpuComposition.Add(builder.Services,settings,trialScheduler,maximumGpuCohorts);
if (normalBinding is not null)
{
    StagingNormalGpuComposition.Add(builder.Services, settings);
    StagingNormalGpuGuards.Add(builder.Services, settings, normalBinding);
}
if(indexingTrial || fixtureCleanup)
    builder.Services.AddSingleton(provider=>new StagingRootedIndexFixture(provider.GetRequiredService<IServiceScopeFactory>(),settings,
        ()=>controllerNonce!=Guid.Empty && settings.DashboardReadBenchmark && !settings.SourceJournalEnabled &&
            !provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested,
        normalBinding is null ? null : (normalBinding.Generation, normalBinding.Version), gpuIndexing: normalRuntime));
var sourceDiagnostics = StagingSourceDiagnostics.Register(builder.Services, builder.Logging);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<OverviewProjectionState>();
builder.Services.AddScoped<CircuitHandler, StatusEventCircuitHandler>();
builder.Services.AddFluxKnowledgeMcp();
builder.Services.AddScoped<NativeV1McpTools>();
if (normalRuntime) builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<StagingSearchMcpTools>();
else builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<NativeV1McpTools>();
var app = builder.Build();
if (normalRuntime)
{
    var capture=app.Services.GetRequiredService<StagingSearchPhaseCapture>();
    app.Lifetime.ApplicationStopping.Register(()=>
    {
        try
        {
            app.Services.GetRequiredService<StagingRootedIndexFixture>().CaptureRuntimeStop(new
            {utc=DateTimeOffset.UtcNow,receipts=app.Services.GetRequiredService<BgeExpandedGpuRankBackend>().Snapshot(),phases=capture.Snapshot()});
        }
        catch(Exception error){app.Logger.LogError(error,"Bounded staging shutdown evidence could not be saved.");}
    });
}
if (settings.DashboardReadBenchmark)
    await app.Services.GetRequiredService<IDatabaseStartupReadiness>().WaitAsync(startupDeadline.Token);
if(fixtureCleanup)
{
    // Build the ordinary native stores, but never start hosted models, workers or an HTTP endpoint.
    var legacyCleanup=args.Length==9;
    using var cleanupDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(legacyCleanup?600:120));
    Exception? cleanupFailure=null;
    try
    {
        var fixture=app.Services.GetRequiredService<StagingRootedIndexFixture>();
        await fixture.RecoverAsync(args[4],args[6],cleanupDeadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(legacyCleanup?
            await StagingLegacyFixtureCleanup.RunAsync(app.Services,settings,fixture,args[8],cleanupDeadline.Token):
            await fixture.CleanupAsync(cleanupDeadline.Token)));
    }
    catch(Exception error){cleanupFailure=error;throw;}
    finally
    {
        try{await app.DisposeAsync();}
        catch(Exception error)when(cleanupFailure is not null){cleanupFailure.Data["CleanupHostDisposalFailure"]=error;}
    }
    return;
}
if(indexingTrial)
    app.Use(async(context,next)=>
    {
        if(!StagingIndexWriteGate.Allowed(controllerNonce,context.Request.Headers["X-Flux-Staging-Operation"].ToString(),context.Request.Method,context.Request.Path.Value??string.Empty,normalRuntime))
        {context.Response.StatusCode=StatusCodes.Status403Forbidden;return;}
        await next(context);
    });
app.UseNativeRequestResources();
app.Use(async (context, next) =>
{
    if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) ||
        context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != settings.Origin.Port)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers["X-FluxKnowledge-Environment"] = settings.CpuSemantic ? "isolated-staging-cpu-semantic" : "isolated-staging-functional";
    context.Items["staging-api-arrival"] = DateTimeOffset.UtcNow;
    if (normalRuntime) context.Response.Headers["X-Flux-Staging-Trace"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty;
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
if (settings.CpuSemantic)
{
    app.MapPost("/staging/query-embedding", (StagingSemanticDiagnostics.QueryRequest request,
        IScheduledPassageInference inference, CancellationToken ct) => StagingSemanticDiagnostics.EmbedAsync(request,
            normalRuntime ? app.Services.GetRequiredService<ResidentCpuPassageInference>() : inference, ct));
    app.MapPost("/staging/rerank-diagnostic", (StagingSemanticDiagnostics.RerankRequest request,
        IScheduledPassageInference inference, CancellationToken ct) => StagingSemanticDiagnostics.RerankAsync(request, inference, ct));
    if (candidateQuality)
        app.MapPost("/staging/candidate-quality", (JsonElement input, IHybridPassageRetrieval retrieval,
            IScheduledPassageInference inference, CancellationToken ct) =>
        {
            if (inference is not ResidentCpuPassageInference || retrieval is not HybridPassageRetrievalEngine engine)
                throw new InvalidOperationException("Candidate quality requires the isolated resident CPU engine.");
            return StagingSemanticDiagnostics.CandidateQualityAsync(input, engine, ct);
        });
}
if (expandedGpu)
{
    app.MapPost("/staging/expanded-gpu", (JsonElement input, IHybridPassageRetrieval retrieval, IExpandedPassageRanker ranker,
        HttpContext context,CancellationToken ct) => StagingSemanticDiagnostics.ExpandedGpuAsync(input,
            (HybridPassageRetrievalEngine)retrieval,ranker,(DateTimeOffset)context.Items["staging-api-arrival"]!,app.Lifetime,ct));
    app.MapGet("/staging/gpu-trial/receipts",(StagingSqlGpuRankBackend backend) => Results.Ok(backend.Snapshot()));
    app.MapPost("/staging/gpu-trial/stop",() => { app.Lifetime.StopApplication(); return Results.Ok(new { stopping=true }); });
}
if (normalRuntime)
{
    app.MapGet("/staging/gpu-trial/receipts", (BgeExpandedGpuRankBackend backend) => Results.Ok(backend.Snapshot()));
    app.MapGet("/staging/search-phases", (StagingSearchPhaseCapture phases) => Results.Ok(phases.Snapshot()));
    app.MapPost("/staging/gpu-trial/stop", () => { app.Lifetime.StopApplication(); return Results.Ok(new { stopping = true }); });
}
if(payloadReadiness)
{
    app.MapPost("/staging/payload-readiness",(JsonElement input,IHybridPassageRetrieval retrieval,HttpContext context,TimeProvider clock,CancellationToken ct) =>
        StagingSemanticDiagnostics.PayloadReadinessAsync(input,(HybridPassageRetrievalEngine)retrieval,
            (DateTimeOffset)context.Items["staging-api-arrival"]!,clock,ct));
    app.MapPost("/staging/trial/stop",() => {app.Lifetime.StopApplication();return Results.Ok(new {stopping=true});});
}
if(indexingTrial)
{
    StagingGpuTrialPermit RequireIndexPermit(StagingGpuTrialGate gate)
    {
        gate.Require();
        var permit=JsonSerializer.Deserialize<StagingGpuTrialPermit>(File.ReadAllText(gate.PermitPath))
            ??throw new InvalidOperationException("missing-index-trial-permit");
        if(permit.OperationId!=controllerNonce)throw new InvalidOperationException("index-controller-permit-mismatch");
        return permit;
    }
    app.MapPost("/staging/indexing/prepare",async (StagingIndexPrepareInput input,StagingRootedIndexFixture fixture,StagingGpuTrialGate gate,CancellationToken ct)=>
    {
        RequireIndexPermit(gate);
        if(input.Operation!=controllerNonce) return Results.BadRequest();
        return Results.Ok(await fixture.PrepareAsync(input.Operation,input.Output,ct));
    });
    app.MapPost("/staging/indexing/prepare-publication",async(StagingRootedIndexFixture fixture,StagingGpuTrialGate gate,CancellationToken ct)=>
    {RequireIndexPermit(gate);return Results.Ok(await fixture.PreparePublicationAsync(ct));});
    app.MapPost("/staging/indexing/start",async(StagingRootedIndexFixture fixture,StagingGpuTrialGate gate,IHostApplicationLifetime lifetime)=>
    {
        var permit=RequireIndexPermit(gate);
        return Results.Ok(await fixture.StartBacklogAsync(permit.DeadlineUtc,lifetime.ApplicationStopping));
    });
    app.MapPost("/staging/indexing/publish",async(StagingRootedIndexFixture fixture,StagingGpuTrialGate gate,CancellationToken ct)=>
    {RequireIndexPermit(gate);return Results.Ok(await fixture.PublishAsync(ct));});
    app.MapPost("/staging/indexing/stop",async(StagingRootedIndexFixture fixture)=>Results.Ok(await fixture.StopBacklogAsync()));
}
app.MapGet("/staging/source-diagnostics", sourceDiagnostics.Read);
app.MapGet("/staging", async (ISqlServerReadinessValidator validator, SqlServerOptions options, CancellationToken ct) =>
{
    var readiness = await validator.ValidateAsync(options, ct);
    return Results.Ok(new
    {
        environment = "isolated-staging", validation = payloadReadiness ? "offline-bge-payload-readiness-diagnostic" : candidateQuality ? "offline-bge-cpu-quality-diagnostic" : settings.CpuSemantic ? "offline-bge-cpu-semantic" : "functional-deterministic-embedding",
        candidateQualityEnabled = candidateQuality, expandedGpuTrialEnabled=expandedGpu, normalRuntimeEnabled=normalRuntime, servingQualified = false,
        gpuTrialMaximumCohorts=expandedGpu || normalRuntime?maximumGpuCohorts:0,indexingTrialEnabled=indexingTrial,
        payloadReadinessEnabled=payloadReadiness,
        catalogue = settings.CatalogName, sourcesRoot = settings.SourcesRoot,
        gpu = expandedGpu || normalRuntime ? "explicit-bounded-trial-permit-required" : "disabled", ocr = "disabled", mail = "disabled", dashboardReadBenchmark = settings.DashboardReadBenchmark,
        cpuSearchLanes = settings.CpuSemantic ? 2 : 0, cpuRerankerShardsPerLane = settings.CpuSemantic ? settings.CpuRerankerShards : 0,
        cpuRerankerThreadsPerShard = settings.CpuSemantic ? settings.CpuRerankerThreads : 0,
        annSearchExpansion = settings.AnnSearchExpansion,
        cpuPendingRequests = app.Services.GetService<FluxKnowledge.Infrastructure.Inference.Search.ResidentCpuPassageInference>()?.PendingSearchCount ?? 0,
        cpuMaximumPendingRequests = app.Services.GetService<FluxKnowledge.Infrastructure.Inference.Search.ResidentCpuPassageInference>()?.MaximumPendingRequests ?? 0,
        annPreparation = app.Services.GetService<CorpusAnnPreparationService>()?.State,
        sqlReady = readiness.IsReady
    });
});
await app.RunAsync();

internal sealed record StagingIndexPrepareInput(Guid Operation,string Output);
