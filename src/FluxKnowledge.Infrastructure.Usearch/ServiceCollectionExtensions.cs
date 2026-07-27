using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.Usearch.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.Usearch;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeUsearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(provider => UsearchIndexOptions.FromConfiguredRoot(
            configuration[$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"],
            provider.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddSingleton(DerivedIndexRecoveryOptions.Default);
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        services.AddSingleton<IDerivedIndexRecoveryStatus>(provider => provider.GetRequiredService<DerivedIndexRecoveryCoordinator>());
        services.AddSingleton<IDerivedIndexRecoverySignal>(provider => provider.GetRequiredService<DerivedIndexRecoveryCoordinator>());
        services.AddHostedService<DerivedIndexRecoveryService>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddScoped<IIndexGenerationPublisher>(provider => provider.GetRequiredService<UsearchGenerationBuilder>());
        services.AddSingleton<UsearchAnnIndex>();
        services.AddSingleton<IAnnIndex>(provider => provider.GetRequiredService<UsearchAnnIndex>());
        services.AddScoped<ISemanticSearch, UsearchNearestNeighbourQuery>();
        return services;
    }
}
