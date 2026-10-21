using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.Usearch.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.Usearch;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeUsearch(
        this IServiceCollection services,
        IConfiguration configuration,
        string? defaultRootPath = null)
    {
        services.AddSingleton(provider =>
        {
            var configuredRoot = configuration[$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"] ?? defaultRootPath;
            return string.Equals(configuredRoot, LiveRootLayout.Production.IndexRoot, StringComparison.OrdinalIgnoreCase)
                ? new UsearchIndexConfiguration(new UsearchIndexOptions(LiveRootLayout.Production.IndexRoot), null)
                : UsearchIndexConfiguration.FromConfiguredRoot(
                    configuredRoot,
                    provider.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory(),
                    AppContext.BaseDirectory);
        });
        services.AddSingleton(provider => provider.GetRequiredService<UsearchIndexConfiguration>().GetOptionsOrThrow());
        return AddFluxKnowledgeUsearchCore(
            services,
            storageSafety: null,
            FileSystemUsearchDirectoryCreator.Instance,
            segmentedEnabled: UsearchIndexOptions.IsSegmentedPublicationEnabled(configuration["IndexPublication:Mode"]));
    }

    internal static IServiceCollection AddProductionFluxKnowledgeUsearch(
        this IServiceCollection services,
        UsearchIndexOptions validatedOptions,
        LiveRootStorageSafety storageSafety,
        IUsearchDirectoryCreator? directoryCreator = null,
        bool segmentedEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(validatedOptions);
        ArgumentNullException.ThrowIfNull(storageSafety);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(validatedOptions.RootPath)),
                LiveRootLayout.Production.IndexRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Production USearch must use {LiveRootLayout.Production.IndexRoot}.");
        }

        services.AddSingleton(new UsearchIndexConfiguration(validatedOptions, null));
        services.AddSingleton(validatedOptions);
        services.AddSingleton(storageSafety);
        return AddFluxKnowledgeUsearchCore(
            services,
            storageSafety,
            directoryCreator ?? FileSystemUsearchDirectoryCreator.Instance,
            registerRecoveryBackgroundService: true,
            segmentedEnabled: segmentedEnabled);
    }

    private static IServiceCollection AddFluxKnowledgeUsearchCore(
        IServiceCollection services,
        LiveRootStorageSafety? storageSafety,
        IUsearchDirectoryCreator directoryCreator,
        bool registerRecoveryBackgroundService = true,
        bool segmentedEnabled = false)
    {
        services.AddSingleton(_ => new UsearchGenerationValidator(storageSafety));
        services.AddSingleton(DerivedIndexRecoveryOptions.Default);
        services.AddSingleton(provider => new DerivedIndexFileSystem(
            provider.GetRequiredService<UsearchIndexOptions>(),
            existingComponentsSafetyCheck: null,
            storageSafety,
            directoryCreator));
        services.AddSingleton(provider => new DerivedIndexRecoveryCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<UsearchIndexConfiguration>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IStatusEventPublisher>(),
            provider.GetRequiredService<DerivedIndexRecoveryOptions>(),
            deploymentHold: provider.GetService<IDeploymentValidationHold>()));
        services.AddSingleton<IDerivedIndexRecoveryStatus>(provider => provider.GetRequiredService<DerivedIndexRecoveryCoordinator>());
        services.AddSingleton<IDerivedIndexRecoverySignal>(provider => provider.GetRequiredService<DerivedIndexRecoveryCoordinator>());
        if (registerRecoveryBackgroundService)
        {
            services.AddHostedService<DerivedIndexRecoveryService>();
        }
        services.AddScoped(provider => new UsearchGenerationBuilder(
            provider.GetRequiredService<IIndexGenerationStore>(),
            provider.GetRequiredService<UsearchIndexOptions>(),
            provider.GetRequiredService<UsearchGenerationValidator>(),
            storageSafety,
            directoryCreator,
            provider.GetService<Microsoft.Extensions.Logging.ILogger<UsearchGenerationBuilder>>(),
            provider.GetService<IIndexGenerationReadLeaseFactory>()));
        services.AddScoped<IIndexGenerationPublisher>(provider => segmentedEnabled && provider.GetService<ISegmentedIndexStore>() is { } segmentedStore
            ? new SegmentedIndexPublisher(provider.GetRequiredService<UsearchGenerationBuilder>(), segmentedStore,
                provider.GetRequiredService<IDerivedIndexRecoveryStore>(),
                new UsearchSegmentBuilder(provider.GetRequiredService<UsearchIndexOptions>(), provider.GetRequiredService<IIndexSegmentReadLeaseFactory>(),
                    provider.GetService<IndexSegmentBuildOptions>()),
                new UsearchManifestValidator(provider.GetRequiredService<UsearchIndexOptions>(), provider.GetRequiredService<IIndexSegmentReadLeaseFactory>()),
                segmentedEnabled)
            : provider.GetRequiredService<UsearchGenerationBuilder>());
        if (segmentedEnabled)
        {
            services.AddSingleton(provider => new UsearchSegmentCache(provider.GetRequiredService<UsearchIndexOptions>(),
                provider.GetRequiredService<IIndexSegmentReadLeaseFactory>(), provider.GetRequiredService<IIndexSegmentOwnerStore>(),
                provider.GetRequiredService<TimeProvider>()));
            services.AddHostedService<UsearchSegmentCacheMaintenance>();
        }
        services.AddSingleton<UsearchAnnIndex>();
        services.AddSingleton<IAnnIndex>(provider => provider.GetRequiredService<UsearchAnnIndex>());
        services.AddScoped<ICorpusAnnLeaseFactory, UsearchCorpusAnnLeaseFactory>();
        services.AddScoped<ISemanticSearch, UsearchNearestNeighbourQuery>();
        return services;
    }
}
