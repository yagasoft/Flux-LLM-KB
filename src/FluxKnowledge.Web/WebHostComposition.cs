using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web.Components.Status;

namespace FluxKnowledge.Web;

public static class WebHostComposition
{
    public static IServiceCollection AddFluxKnowledgeServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var allowedRoots = configuration
            .GetSection("LocalIngress:AllowedRoots")
            .GetChildren()
            .Select(child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        var ingressOptions = new LocalIngressOptions(allowedRoots);
        _ = LocalIngressOptionsValidator.ValidateAndCanonicalise(ingressOptions);

        services.AddFluxKnowledgeSqlServer(configuration);
        services.AddFluxKnowledgeUsearch(configuration);
        services.AddSingleton(ingressOptions);
        services.AddSingleton<IUtf8FileSourceReader, Utf8FileSourceReader>();
        services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
        services.AddScoped<ISearchService, HybridSearchService>();
        services.AddFluxKnowledgeOutboxWorkers();
        services.AddFluxKnowledgeGpuScheduler();
        services.AddScoped<IProjectionReader, SqlProjectionReader>();
        services.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
        services.AddScoped<IStageWorker, EmbedStageWorker>();
        services.AddScoped<IStageWorker, PublishStageWorker>();
        return services;
    }
}
