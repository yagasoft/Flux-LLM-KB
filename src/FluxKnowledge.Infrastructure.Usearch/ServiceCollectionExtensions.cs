using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.Usearch;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeUsearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(
            configuration[$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"]);
        services.AddSingleton(options);
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddScoped<IIndexGenerationPublisher>(provider => provider.GetRequiredService<UsearchGenerationBuilder>());
        services.AddSingleton<UsearchAnnIndex>();
        services.AddSingleton<IAnnIndex>(provider => provider.GetRequiredService<UsearchAnnIndex>());
        return services;
    }
}
