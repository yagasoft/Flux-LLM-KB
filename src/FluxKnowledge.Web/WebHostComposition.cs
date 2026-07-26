using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Files;

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
        services.AddSingleton(ingressOptions);
        services.AddSingleton<IUtf8FileSourceReader, Utf8FileSourceReader>();
        services.AddFluxKnowledgeOutboxWorkers();
        return services;
    }
}
