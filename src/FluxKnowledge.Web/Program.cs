using FluxKnowledge.Web;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);
WebHostComposition.AddFluxKnowledgeServices(builder.Services, builder.Configuration);
builder.Services.AddFluxKnowledgeMcp();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<KnowledgeMcpTools>();
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());
app.MapFluxKnowledgeSearch();
app.MapMcp("/mcp");

app.Run();

public partial class Program;
