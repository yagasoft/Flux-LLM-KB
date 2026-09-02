using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Operations;
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
using Microsoft.Extensions.Hosting;
using FluxKnowledge.Web.Components.OperatorActions;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

public static class WebHostComposition
{
    private static LiveRootLayout? _isolatedTestLayout;

    public static IServiceCollection AddFluxKnowledgeServices(
        IServiceCollection services,
        IConfiguration configuration) =>
        AddFluxKnowledgeServicesCore(
            services,
            configuration,
            _isolatedTestLayout ?? LiveRootLayout.Production,
            strictProductionPaths: _isolatedTestLayout is null,
            productionStorageBindings: null);

    internal static IServiceCollection AddProductionFluxKnowledgeServicesForTests(
        IServiceCollection services,
        IConfiguration configuration) =>
        AddFluxKnowledgeServicesCore(
            services,
            configuration,
            LiveRootLayout.Production,
            strictProductionPaths: true,
            productionStorageBindings: null);

    internal static IServiceCollection AddProductionFluxKnowledgeServicesForTests(
        IServiceCollection services,
        IConfiguration configuration,
        ProductionStorageTestBindings productionStorageBindings) =>
        AddFluxKnowledgeServicesCore(
            services,
            configuration,
            LiveRootLayout.Production,
            strictProductionPaths: true,
            productionStorageBindings);

    internal static void ConfigureIsolatedTestLayout(LiveRootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsProduction)
        {
            throw new ArgumentException("The isolated Web test layout cannot be production.", nameof(layout));
        }

        Interlocked.CompareExchange(ref _isolatedTestLayout, layout, null);
    }

    internal static NativeGoLiveRuntimeConfiguration ReadNativeGoLiveRuntimeOptions(
        IConfiguration configuration) => NativeGoLiveRuntimeOptions.Read(configuration);

    internal static IConfigurationRoot LoadCanonicalProductionConfiguration(
        INoFollowPathOpener opener) =>
        NoFollowJsonConfigurationProvider.LoadCanonicalProduction(
            NoFollowJsonConfigurationProvider.CanonicalProductionPath,
            opener);

    internal static bool IsIsolatedTestComposition => _isolatedTestLayout is not null;

    /// <summary>
    /// Runs before the published Web host is built. It permits only the provisioned source-worker
    /// and Outlook-capture hosted services, while rejecting every unprovisioned runtime provider.
    /// It starts no listener.
    /// </summary>
    internal static void ValidateNativeGoLiveComposition(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var runtime = ReadNativeGoLiveRuntimeOptions(configuration);
        NativeGoLiveRuntimeOptions.ValidateEffective(runtime);
        var allowedHostedServiceTypes = new HashSet<Type>();
        if (runtime.WorkerEnabled)
        {
            allowedHostedServiceTypes.Add(typeof(SourceReconciliationService));
            allowedHostedServiceTypes.Add(typeof(LocalSourceRootWatchHostedService));
        }
        if (runtime.OutlookEnabled)
            allowedHostedServiceTypes.Add(typeof(OutlookCaptureRecoveryService));
        var hostedServiceTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();
        if (hostedServiceTypes.Length != allowedHostedServiceTypes.Count ||
            hostedServiceTypes.Any(type => type is null || !allowedHostedServiceTypes.Remove(type)))
            throw new InvalidOperationException("native-go-live-hosted-service-registered");
        var prohibited = new[]
        {
            typeof(GpuSchedulerService),
            typeof(CanonicalIndexStageWorker),
            typeof(EmbedStageWorker),
            typeof(PublishStageWorker)
        };
        if (services.Any(descriptor => prohibited.Contains(descriptor.ServiceType) ||
                                       descriptor.ImplementationType is not null && prohibited.Contains(descriptor.ImplementationType)))
            throw new InvalidOperationException("native-go-live-prohibited-service-registered");
    }

    internal static ValueTask InitialiseStrictProductionRecoveryAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<DerivedIndexRecoveryCoordinator>()
            .RunOnceAsync(cancellationToken);
    }

    private static IServiceCollection AddFluxKnowledgeServicesCore(
        IServiceCollection services,
        IConfiguration configuration,
        LiveRootLayout liveRoot,
        bool strictProductionPaths,
        ProductionStorageTestBindings? productionStorageBindings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        NativeGoLiveRuntimeConfiguration? nativeRuntimeOptions = null;
        if (strictProductionPaths)
        {
            nativeRuntimeOptions = ReadNativeGoLiveRuntimeOptions(configuration);
        }

        var indexRoot = strictProductionPaths
            ? LiveRootLayout.RequireExactProductionPathOverride(
                configuration[$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"],
                liveRoot.IndexRoot,
                $"{UsearchIndexOptions.ConfigurationSectionName}:RootPath")
            : configuration[$"{UsearchIndexOptions.ConfigurationSectionName}:RootPath"] ?? liveRoot.IndexRoot;
        var artifactRoot = strictProductionPaths
            ? LiveRootLayout.RequireExactProductionPathOverride(
                configuration["SourceArtifactStore:Root"],
                liveRoot.RetainedRoot,
                "SourceArtifactStore:Root")
            : configuration["SourceArtifactStore:Root"] ?? liveRoot.RetainedRoot;
        if (strictProductionPaths)
        {
            _ = LiveRootLayout.RequireExactProductionPathOverride(
                configuration[PrivatePcDataProtectionProviderFactory.LocalApplicationDataRootConfigurationKey],
                liveRoot.ConfigRoot,
                PrivatePcDataProtectionProviderFactory.LocalApplicationDataRootConfigurationKey);
            _ = LiveRootLayout.RequireExactProductionPathOverride(
                configuration[$"{SqlServerOptions.SectionName}:DataFilePath"],
                liveRoot.SqlDataFilePath,
                $"{SqlServerOptions.SectionName}:DataFilePath");
            _ = LiveRootLayout.RequireExactProductionPathOverride(
                configuration[$"{SqlServerOptions.SectionName}:LogFilePath"],
                liveRoot.SqlLogFilePath,
                $"{SqlServerOptions.SectionName}:LogFilePath");
        }
        var configuredSpoolRoots = ReadRoots(configuration, "Outlook:AllowedSpoolRoots").ToArray();
        if (strictProductionPaths && configuredSpoolRoots.Length > 1)
        {
            throw new InvalidOperationException(
                $"Outlook:AllowedSpoolRoots must contain only {liveRoot.SpoolRoot}.");
        }
        if (strictProductionPaths && configuredSpoolRoots.Length == 1)
        {
            _ = LiveRootLayout.RequireExactProductionPathOverride(
                configuredSpoolRoots[0],
                liveRoot.SpoolRoot,
                "Outlook:AllowedSpoolRoots:0");
        }

        var allowedRoots = configuration
            .GetSection("LocalIngress:AllowedRoots")
            .GetChildren()
            .Select(child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        var ingressOptions = nativeRuntimeOptions?.LocalIngress ?? new LocalIngressOptions(allowedRoots);
        _ = LocalIngressOptionsValidator.ValidateAndCanonicalise(ingressOptions);
        var operatorOrigin = configuration["LocalOperator:CanonicalOrigin"] ?? "http://127.0.0.1:5137";
        var productionStorageSafety = strictProductionPaths
            ? new LiveRootStorageSafety(
                liveRoot,
                productionStorageBindings?.PathInspector ?? FileSystemLiveRootPathInspector.Instance)
            : null;
        var persistedOutlookSpoolPolicy = new PersistedOutlookSpoolRootPolicy(
            liveRoot,
            productionStorageSafety ?? new LiveRootStorageSafety(liveRoot, FileSystemLiveRootPathInspector.Instance));

        services.AddSingleton(liveRoot);
        services.AddSingleton(persistedOutlookSpoolPolicy);
        services.AddFluxKnowledgeSqlServer(configuration);
        services.AddSingleton(new LocalOperatorOriginPolicy(operatorOrigin));
        services.AddSingleton<ILocalPrivateContentDisclosure, LocalPrivateContentDisclosure>();
        services.AddHttpContextAccessor();
        if (strictProductionPaths)
        {
            services.AddProductionFluxKnowledgeUsearch(
                new UsearchIndexOptions(indexRoot),
                productionStorageSafety!,
                productionStorageBindings?.UsearchDirectoryCreator);
        }
        else
        {
            services.AddFluxKnowledgeUsearch(configuration, liveRoot.IndexRoot);
        }
        var sqlDataRoot = Path.GetDirectoryName(SqlServerOptions.ProductionDataFilePath)!;
        var sqlLogRoot = Path.GetDirectoryName(SqlServerOptions.ProductionLogFilePath)!;
        var ussearchRoot = indexRoot;
        var configuredSafetyRoots = ReadConfiguredSafetyRoots(configuration);
        var protectedRoots = new[]
        {
            AppContext.BaseDirectory,
            sqlDataRoot,
            sqlLogRoot,
            ussearchRoot
        }.Concat(configuredSafetyRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!strictProductionPaths)
        {
            artifactRoot = ContentAddressedSourceArtifactStore.ValidateRoot(artifactRoot, protectedRoots);
        }
        services.AddSingleton(ingressOptions);
        if (strictProductionPaths)
        {
            var dataProtectionStore = productionStorageBindings?.DataProtectionStore ??
                FileSystemPrivatePcDataProtectionStore.Instance;
            services.AddSingleton(_ => PrivatePcDataProtectionProviderFactory.CreateCursorCodec(
                liveRoot,
                productionStorageSafety!,
                dataProtectionStore));
            services.AddSingleton<INativeV1CursorCodec>(_ => PrivatePcDataProtectionProviderFactory.CreateNativeV1CursorCodec(
                liveRoot,
                productionStorageSafety!,
                dataProtectionStore));
        }
        else
        {
            services.AddSingleton(_ => PrivatePcDataProtectionProviderFactory.CreateCursorCodec(liveRoot));
            services.AddSingleton<INativeV1CursorCodec>(_ => PrivatePcDataProtectionProviderFactory.CreateNativeV1CursorCodec(liveRoot));
        }
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
            artifactRoot,
            provider.GetRequiredService<PersistedOutlookSpoolRootPolicy>()));
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
            protectedRoots,
            provider.GetRequiredService<PersistedOutlookSpoolRootPolicy>()));
        services.AddSingleton(ReadRetainedProcessorOptions(configuration));
        services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
        services.AddScoped<ISearchService, HybridSearchService>();
        services.AddScoped<SqlNativeOperationStore>();
        services.AddScoped<INativeOperationStore>(provider => provider.GetRequiredService<SqlNativeOperationStore>());
        services.AddScoped<SqlKnowledgeStore>();
        services.AddScoped<IKnowledgeStore>(provider => provider.GetRequiredService<SqlKnowledgeStore>());
        services.AddScoped<IKnowledgeCommandService, KnowledgeCommandService>();
        services.AddScoped<IKnowledgeQueryService, KnowledgeQueryService>();
        services.AddScoped<INativeCorpusActionStore, SqlNativeCorpusActionStore>();
        services.AddScoped<INativeV1ProjectionReader, SqlNativeV1ProjectionReader>();
        services.AddScoped<NativeCorpusQueryService>();
        services.AddScoped<NativeCorpusCommandService>();
        services.AddScoped<NativeCodeQueryService>();
        services.AddScoped<NativeCodeFeedbackService>();
        services.AddScoped<NativeOperationsStatusService>();
        services.AddScoped<NativeAuditQueryService>();
        services.AddScoped<INativeV1Facade, NativeV1Facade>();
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
        services.AddScoped<SourceRootService>();
        services.AddScoped<SourceScanControlService>();
        var workersEnabled = configuration.GetValue<bool>("Worker:Enabled");
        if (workersEnabled)
        {
            services.AddSingleton<SqlSourceRootWatchStore>();
            services.AddSingleton<ISourceRootWatchStore>(provider => provider.GetRequiredService<SqlSourceRootWatchStore>());
            services.AddSingleton<SourceWatchCoordinator>();
            services.AddSingleton<IHostedService, SourceReconciliationService>();
            services.AddSingleton<IHostedService, LocalSourceRootWatchHostedService>();
        }
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
        services.AddScoped<IOutlookOperatorPolicy, LocalOutlookOperatorPolicy>();
        services.AddScoped<LocalOutlookConnectionContext>();
        services.AddScoped<IOutlookProjectionReader, SqlOutlookProjectionReader>();
        services.AddScoped<OutlookPageState>();
        services.AddSingleton(new OutlookSpoolPolicyOptions(
            strictProductionPaths || configuredSpoolRoots.Length == 0
                ? [liveRoot.SpoolRoot]
                : configuredSpoolRoots,
            ReadPositiveLong(configuration["Outlook:MinimumSpoolAvailableBytes"], OutlookSpoolPolicyOptions.DefaultMinimumAvailableBytes)));
        services.AddSingleton<LocalOutlookSpoolValidator>();
        services.AddSingleton<IOutlookSpoolValidator>(provider => provider.GetRequiredService<LocalOutlookSpoolValidator>());
        services.AddSingleton<IOutlookSpoolHealthReader>(provider => provider.GetRequiredService<LocalOutlookSpoolValidator>());
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
        if (!strictProductionPaths)
        {
            services.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
            services.AddScoped<IStageWorker, EmbedStageWorker>();
            services.AddScoped<IStageWorker, PublishStageWorker>();
        }
        if (strictProductionPaths)
        {
            RemoveUnapprovedHostedServiceRegistrations(services, nativeRuntimeOptions!);
        }
        return services;
    }

    private static void RemoveUnapprovedHostedServiceRegistrations(
        IServiceCollection services,
        NativeGoLiveRuntimeConfiguration runtime)
    {
        var unapprovedHostedServiceTypes = new HashSet<Type>
        {
            typeof(OutboxPumpService),
            typeof(RetainedProcessorActivationHostedService),
            typeof(GpuSchedulerService),
            typeof(GpuExecutorDispatchRecoveryService)
        };
        if (!runtime.OutlookEnabled)
            unapprovedHostedServiceTypes.Add(typeof(OutlookCaptureRecoveryService));
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if ((unapprovedHostedServiceTypes.Contains(descriptor.ServiceType) ||
                 descriptor.ServiceType == typeof(IHostedService) &&
                 (descriptor.ImplementationType is { } implementationType && unapprovedHostedServiceTypes.Contains(implementationType) ||
                 descriptor.ImplementationFactory?.Method.Name.Contains(
                     nameof(OutboxWorkerServiceCollectionExtensions.AddFluxKnowledgeOutboxWorkers),
                     StringComparison.Ordinal) == true)))
            {
                services.RemoveAt(index);
            }
        }
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
            MediaMetadataEnabled = bool.TryParse(configuration[$"{section}:MediaMetadataEnabled"], out var mediaMetadataEnabled) && mediaMetadataEnabled,
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

internal sealed record ProductionStorageTestBindings(
    ILiveRootPathInspector PathInspector,
    IUsearchDirectoryCreator UsearchDirectoryCreator,
    IPrivatePcDataProtectionStore DataProtectionStore);
