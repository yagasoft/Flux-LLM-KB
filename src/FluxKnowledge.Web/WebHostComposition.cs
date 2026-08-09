using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;

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
        var sqlDataRoot = Path.GetDirectoryName(SqlServerOptions.ProductionDataFilePath)!;
        var sqlLogRoot = Path.GetDirectoryName(SqlServerOptions.ProductionLogFilePath)!;
        var ussearchRoot = configuration[$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"]
            ?? throw new InvalidOperationException("Usearch:RootPath must be configured before source artifact storage.");
        var configuredArtifactRoot = configuration["SourceArtifactStore:Root"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FluxKnowledge", "source-artifacts");
        var configuredSafetyRoots = ReadConfiguredSafetyRoots(configuration);
        var protectedRoots = new[]
        {
            AppContext.BaseDirectory,
            sqlDataRoot,
            sqlLogRoot,
            ussearchRoot
        }.Concat(configuredSafetyRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var artifactRoot = ContentAddressedSourceArtifactStore.ValidateRoot(
            configuredArtifactRoot,
            protectedRoots);
        services.AddSingleton(ingressOptions);
        services.AddSingleton<ISourceRootPathPolicy>(provider =>
        {
            var sqlOptions = provider.GetRequiredService<SqlServerOptions>();
            var ussearchOptions = provider.GetRequiredService<UsearchIndexOptions>();
            var resolvedProtectedRoots = new[]
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(sqlOptions.DataFilePath)!,
                Path.GetDirectoryName(sqlOptions.LogFilePath)!,
                ussearchOptions.RootPath,
                artifactRoot
            }.Concat(configuredSafetyRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new SourceRootPathPolicy(
                ingressOptions,
                new SourceRootPathPolicyOptions(resolvedProtectedRoots));
        });
        services.AddSingleton<IUtf8FileSourceReader, Utf8FileSourceReader>();
        services.AddScoped<IRetainedSourceReader>(provider => new SqlRetainedSourceReader(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            artifactRoot));
        services.AddSingleton<ISourceArtifactStore>(_ => new ContentAddressedSourceArtifactStore(
            artifactRoot,
            protectedRoots));
        services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
        services.AddScoped<ISearchService, HybridSearchService>();
        services.AddFluxKnowledgeOutboxWorkers();
        services.AddScoped<SqlSourceRootStore>();
        services.AddScoped<ISourceRootStore>(provider => provider.GetRequiredService<SqlSourceRootStore>());
        services.AddScoped<SqlSourceActivityStore>();
        services.AddScoped<ISourceActivityStore>(provider => provider.GetRequiredService<SqlSourceActivityStore>());
        services.AddScoped<ISourceCapabilityStore>(provider => provider.GetRequiredService<SqlSourceActivityStore>());
        services.AddScoped<SqlSourceScanStore>();
        services.AddScoped<ISourceScanStore>(provider => provider.GetRequiredService<SqlSourceScanStore>());
        services.AddScoped<ISourceScanControlStore>(provider => provider.GetRequiredService<SqlSourceScanStore>());
        services.AddScoped<ISourceFileEnumerator, LocalSourceEnumerator>();
        services.AddScoped<ISourceScanner, SourceScanWorker>();
        services.AddSingleton<ChannelSourceScanWakeSignal>();
        services.AddSingleton<ISourceScanWakeSignal>(provider => provider.GetRequiredService<ChannelSourceScanWakeSignal>());
        services.AddSingleton<SourceReconciliationService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SourceReconciliationService>());
        services.AddScoped<SourceRootService>();
        services.AddScoped<SourceScanControlService>();
        services.AddScoped<ISourceRootProjectionReader, SourceRootProjectionReader>();
        services.AddScoped<SourceRootPageState>();
        services.AddScoped<SourceRootDetailPageState>(provider => new SourceRootDetailPageState(
            provider.GetRequiredService<ISourceRootProjectionReader>(),
            provider.GetService<IDeferredContentReprocessor>()));
        services.AddFluxKnowledgeGpuScheduler();
        services.AddScoped<IProjectionReader, SqlProjectionReader>();
        services.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
        services.AddScoped<IStageWorker, EmbedStageWorker>();
        services.AddScoped<IStageWorker, PublishStageWorker>();
        return services;
    }

    private static IReadOnlyList<string> ReadConfiguredSafetyRoots(IConfiguration configuration) =>
        [
            .. ReadRoots(configuration, "SourceRootPolicy:ProtectedRoots"),
            .. ReadRoots(configuration, "SourceRootPolicy:SecretRoots"),
            .. ReadRoots(configuration, "SourceRootPolicy:CacheRoots"),
            .. ReadRoots(configuration, "SourceArtifactStore:ProtectedRoots")
        ];

    private static IEnumerable<string> ReadRoots(IConfiguration configuration, string section) =>
        configuration.GetSection(section).GetChildren()
            .Select(child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!);
}
