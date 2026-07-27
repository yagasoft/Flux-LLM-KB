using FluxKnowledge.Web;
using FluxKnowledge.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);
WebHostComposition.AddFluxKnowledgeServices(builder.Services, builder.Configuration);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());
app.MapFluxKnowledgeSearch();

app.Run();

public partial class Program;
