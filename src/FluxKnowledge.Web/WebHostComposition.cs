using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Outlook;
using FluxKnowledge.Web.Components.OperatorActions;
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
        var operatorOrigin = configuration["LocalOperator:CanonicalOrigin"] ?? "http://127.0.0.1:5137";

        services.AddFluxKnowledgeSqlServer(configuration);
        services.AddSingleton(new LocalOperatorOriginPolicy(operatorOrigin));
        services.AddSingleton<ILocalPrivateContentDisclosure, LocalPrivateContentDisclosure>();
        services.AddHttpContextAccessor();
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
        services.AddSingleton(_ => PrivatePcDataProtectionProviderFactory.CreateCursorCodec(
            configuration[PrivatePcDataProtectionProviderFactory.LocalApplicationDataRootConfigurationKey]));
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
        services.AddScoped<ILocalRetainedDetailReader>(provider => new SqlLocalRetainedDetailReader(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            provider.GetRequiredService<IRetainedSourceReader>(),
            provider.GetRequiredService<ILocalPrivateContentDisclosure>()));
        services.AddScoped<ILocalRetainedCsharpCodeReader>(provider => new SqlLocalRetainedCsharpCodeReader(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            provider.GetRequiredService<ILocalRetainedDetailReader>(),
            provider.GetRequiredService<ILocalPrivateContentDisclosure>(),
            provider.GetRequiredService<LocalRetainedCsharpCodeSearchCursorCodec>()));
        services.AddSingleton<ISourceArtifactStore>(_ => new ContentAddressedSourceArtifactStore(
            artifactRoot,
            protectedRoots));
        services.AddScoped<IRetainedArtifactWriter>(provider => new SqlRetainedArtifactWriter(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
            artifactRoot,
            protectedRoots));
        services.AddSingleton(ReadRetainedProcessorOptions(configuration));
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
        services.AddSingleton<SqlSourceRootWatchStore>();
        services.AddSingleton<ISourceRootWatchStore>(provider => provider.GetRequiredService<SqlSourceRootWatchStore>());
        services.AddSingleton<SourceWatchCoordinator>();
        services.AddSingleton<SourceReconciliationService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SourceReconciliationService>());
        services.AddSingleton<IHostedService, LocalSourceRootWatchHostedService>();
        services.AddScoped<SourceRootService>();
        services.AddScoped<SourceScanControlService>();
        services.AddScoped<ISourceRootProjectionReader, SourceRootProjectionReader>();
        services.AddScoped<SourceRootPageState>();
        services.AddScoped<SourceRootDetailPageState>(provider => new SourceRootDetailPageState(
            provider.GetRequiredService<ISourceRootProjectionReader>(),
            provider.GetService<IDeferredContentReprocessor>(),
            provider.GetRequiredService<IOperatorEventProjectionReader>()));
        services.AddScoped<RetainedBranchDetailPageState>();
        services.AddScoped<RetainedCsharpCodeDetailPageState>();
        services.AddScoped<RetainedCsharpCodeSearchPageState>();
        services.AddScoped<SqlOutlookCaptureStore>();
        services.AddScoped<IOutlookCaptureStore>(provider => provider.GetRequiredService<SqlOutlookCaptureStore>());
        services.AddScoped<IOutlookCaptureRecoveryStore>(provider => provider.GetRequiredService<SqlOutlookCaptureStore>());
        var outlookRecoveryOptions = ReadOutlookRecoveryOptions(configuration);
        services.AddSingleton(outlookRecoveryOptions);
        if (outlookRecoveryOptions.Enabled)
        {
            services.AddSingleton<IHostedService, OutlookCaptureRecoveryService>();
        }
        services.AddSingleton(new OutlookSpoolPolicyOptions(
            ReadRoots(configuration, "Outlook:AllowedSpoolRoots").ToArray(),
            ReadPositiveLong(configuration["Outlook:MinimumSpoolAvailableBytes"], OutlookSpoolPolicyOptions.DefaultMinimumAvailableBytes)));
        services.AddSingleton<LocalOutlookSpoolValidator>();
        services.AddSingleton<IOutlookSpoolValidator>(provider => provider.GetRequiredService<LocalOutlookSpoolValidator>());
        services.AddSingleton<IOutlookSpoolHealthReader>(provider => provider.GetRequiredService<LocalOutlookSpoolValidator>());
        services.AddScoped<IOutlookOperatorPolicy, LocalOutlookOperatorPolicy>();
        services.AddScoped<LocalOutlookConnectionContext>();
        services.AddScoped<IOutlookProjectionReader, SqlOutlookProjectionReader>();
        services.AddScoped<OutlookPageState>();
        services.AddScoped<IOperatorActionStore, SqlOperatorActionStore>();
        services.AddScoped<OperatorActionService>();
        services.AddScoped<LocalOperatorConnectionContext>();
        services.AddScoped<ILocalOperatorPolicy, LocalOperatorPolicy>();
        services.AddScoped<OperatorActionPageState>();
        services.AddFluxKnowledgeGpuScheduler();
        services.AddScoped<IProjectionReader, SqlProjectionReader>();
        services.AddScoped<ICorpusProjectionReader, SqlCorpusProjectionReader>();
        services.AddScoped<IOperatorEventProjectionReader, SqlOperatorEventProjectionReader>();
        services.AddScoped<Components.Corpus.CorpusPageState>();
        services.AddScoped<Components.Corpus.CorpusDetailPageState>();
        services.AddScoped<Components.Events.EventsPageState>();
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

    private static long ReadPositiveLong(string? configured, long defaultValue) =>
        long.TryParse(configured, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : defaultValue;

    internal static RetainedProcessorOptions ReadRetainedProcessorOptions(IConfiguration configuration)
    {
        var section = RetainedProcessorOptions.ConfigurationSectionName;
        var configuredBatchSize = configuration[$"{section}:AutomaticReplayBatchSize"];
        var batchSize = RetainedProcessorOptions.MaximumAutomaticReplayBatchSize;
        if (configuredBatchSize is not null &&
            (!int.TryParse(
                configuredBatchSize,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out batchSize) ||
             batchSize is < 1 or > RetainedProcessorOptions.MaximumAutomaticReplayBatchSize))
        {
            throw new InvalidOperationException(
                $"{section}:AutomaticReplayBatchSize must be between 1 and {RetainedProcessorOptions.MaximumAutomaticReplayBatchSize}.");
        }

        return new RetainedProcessorOptions
        {
            ArchiveZipExpandEnabled = bool.TryParse(configuration[$"{section}:ArchiveZipExpandEnabled"], out var zipEnabled) && zipEnabled,
            ArchiveTarExpandEnabled = bool.TryParse(configuration[$"{section}:ArchiveTarExpandEnabled"], out var tarEnabled) && tarEnabled,
            OoxmlDocumentStructuralExtractEnabled = bool.TryParse(configuration[$"{section}:OoxmlDocumentStructuralExtractEnabled"], out var ooxmlEnabled) && ooxmlEnabled,
            CsharpCodeEnabled = !bool.TryParse(configuration[$"{section}:CsharpCodeEnabled"], out var csharpEnabled) || csharpEnabled,
            AutomaticReplayBatchSize = batchSize
        };
    }

    internal static OutlookCaptureRecoveryOptions ReadOutlookRecoveryOptions(IConfiguration configuration)
    {
        var section = OutlookCaptureRecoveryOptions.ConfigurationSectionName;
        var configuredEnabled = configuration[$"{section}:Enabled"];
        if (configuredEnabled is not null && !bool.TryParse(configuredEnabled, out _))
        {
            throw new InvalidOperationException($"{section}:Enabled must be true or false.");
        }

        var options = new OutlookCaptureRecoveryOptions
        {
            Enabled = bool.TryParse(configuredEnabled, out var enabled) && enabled,
            HintDebounce = TimeSpan.FromSeconds(ReadBoundedSeconds(
                configuration[$"{section}:HintDebounceSeconds"],
                $"{section}:HintDebounceSeconds",
                5,
                1,
                60)),
            RecoveryCadence = TimeSpan.FromSeconds(ReadBoundedSeconds(
                configuration[$"{section}:RecoveryCadenceSeconds"],
                $"{section}:RecoveryCadenceSeconds",
                60,
                30,
                900)),
            StaleLeaseAge = TimeSpan.FromSeconds(ReadBoundedSeconds(
                configuration[$"{section}:StaleLeaseSeconds"],
                $"{section}:StaleLeaseSeconds",
                600,
                60,
                3600))
        };
        options.Validate();
        return options;
    }

    private static int ReadBoundedSeconds(
        string? configured,
        string configurationKey,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (configured is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(
                configured,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) || value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{configurationKey} must be between {minimum} and {maximum} seconds.");
        }

        return value;
    }
}
