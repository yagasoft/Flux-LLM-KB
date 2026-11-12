using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingHostCompositionTests : IDisposable
{
    private const string Root = @"I:\Codex Workspaces\Staging\FluxKnowledge\CompositionTests";
    private const string Connection = "Server=localhost;Initial Catalog=FluxKnowledge_Staging_CompositionTests;User ID=stage_test;Password=synthetic-only;Encrypt=True;TrustServerCertificate=True";
    private readonly string _ownedRoot = Root + "-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(@"I:\Codex Workspaces\Staging\FluxKnowledge\DashboardTests")]
    [InlineData(@"E:\Codex Workspaces\Staging\FluxKnowledge\DashboardTests")]
    public void Accepts_exact_supported_staging_bases(string root) =>
        Assert.Equal(root, StagingHostSettings.Create(root, Connection, "http://127.0.0.1:5147").Layout.Root);

    [Fact]
    public void Dashboard_benchmark_disables_application_writers_and_preserves_existing_host_services()
    {
        var settingsFile = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", DashboardReadBenchmark = true
        }));
        var settings = StagingHostSettings.Load(settingsFile);
        Directory.CreateDirectory(settings.SourcesRoot);
        Assert.Equal("false", settings.Configuration["Worker:Enabled"]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostedService, ExistingHostService>();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        Assert.Single(services, service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(ExistingHostService));
        Assert.All(services.Where(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType != typeof(ExistingHostService)),
            service => Assert.Equal(typeof(KeyManagementOptions).Assembly, service.ImplementationType?.Assembly));
        Assert.Contains(services, service => service.ServiceType == typeof(FluxKnowledge.Web.Components.Status.IProjectionReader));
        Assert.Contains(services, service => service.ServiceType == typeof(IDatabaseStartupReadiness));
    }

    [Fact]
    public void Incremental_staging_requires_explicit_volumes_and_keeps_the_local_staging_bindings()
    {
        const string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
        var settingsFile = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147",
            SourceJournalEnabled = true, SourceJournalVolumes = new[] { volume }
        }));
        var settings = StagingHostSettings.Load(settingsFile);
        Assert.Equal("true", settings.Configuration["SourceJournal:Enabled"]);
        Assert.Equal(volume.ToUpperInvariant(), settings.Configuration["SourceJournal:ApprovedVolumes:0"]);
        Assert.Null(settings.Configuration["SourceJournal:PipeName"]);
        Directory.CreateDirectory(settings.SourcesRoot);
        var services = new ServiceCollection(); services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<FluxKnowledge.Integrations.Files.WindowsSourceChangeJournalReader>(provider.GetRequiredService<ISourceChangeJournalReader>());
        Assert.True(provider.GetRequiredService<SourceJournalOptions>().Enabled);
        Assert.Equal(settings.RuntimeConnectionString, provider.GetRequiredService<SqlServerOptions>().ConnectionString);
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(FluxKnowledge.Application.Sources.SourceChangeJournalService));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Staging_rejects_implicit_or_noncanonical_journal_targets_before_runtime_access(bool noncanonical)
    {
        var settingsFile = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", SourceJournalEnabled = true,
            SourceJournalVolumes = noncanonical ? new[] { @"I:\" } : Array.Empty<string>()
        }));
        Assert.Throws<ArgumentException>(() => StagingHostSettings.Load(settingsFile));
    }

    [Fact]
    public void Incremental_staging_preserves_explicit_seal_qualification_and_metadata_rate()
    {
        const string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
        var qualification = new SourceJournalSealQualification("owned-root", "qualified-environment",
            "owned synthetic staging fixture", "guarded-close-v1") { ProviderName = "Test provider", ProviderVersion = "1.2.3" };
        var settingsFile = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147",
            SourceJournalEnabled = true, SourceJournalVolumes = new[] { volume },
            SourceJournalSealingEnabled = true, SourceJournalSealQualifications = new[] { qualification },
            SourceJournalMetadataEntriesPerSecond = 32
        }));
        var settings = StagingHostSettings.Load(settingsFile);
        Directory.CreateDirectory(settings.SourcesRoot);
        var services = new ServiceCollection(); services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SourceJournalOptions>();
        Assert.True(options.SealingEnabled);
        Assert.Equal(32, options.MetadataEntriesPerSecond);
        Assert.Equal(qualification, Assert.Single(options.SealQualifications));
        Assert.Equal(settings.RuntimeConnectionString, provider.GetRequiredService<SqlServerOptions>().ConnectionString);
        Assert.Null(settings.Configuration["SourceJournal:PipeName"]);
    }

    private sealed class ExistingHostService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void Expanded_gpu_trial_cannot_be_added_to_ordinary_or_writable_staging()
    {
        var settings=CreateCompositionSettings();
        var services=new ServiceCollection(); services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services,settings);
        Assert.DoesNotContain(services,s => s.ServiceType==typeof(IExpandedPassageRanker));
        Assert.Throws<ArgumentException>(() => StagingExpandedGpuComposition.Add(services,settings));
    }

    [Fact]
    public async Task Normal_gpu_staging_uses_one_shared_runtime_and_preserves_the_existing_cpu_pool()
    {
        var path = Path.Combine(_ownedRoot, "Config", "staging.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        { SqlConnectionString = Connection, Origin = "http://127.0.0.1:5140", CpuSemantic = true, DashboardReadBenchmark = true, IndexPublicationMode = "Segmented", CpuRerankerThreads = 1 }));
        var settings = StagingHostSettings.Load(path); Directory.CreateDirectory(settings.SourcesRoot);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IHostApplicationLifetime, Lifetime>();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        var cpuDescriptor = Assert.Single(services, d => d.ServiceType == typeof(ResidentCpuPassageInference));
        StagingNormalGpuComposition.Add(services, settings);
        Assert.Same(cpuDescriptor, Assert.Single(services, d => d.ServiceType == typeof(ResidentCpuPassageInference)));
        Assert.Single(services, d => d.ServiceType == typeof(GpuInteractiveExecutor));
        Assert.Single(services, d => d.ServiceType == typeof(EmbeddingGpuExecutor));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GpuSchedulerService));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GpuExecutorDispatchRecoveryService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(FluxKnowledge.Infrastructure.Usearch.DerivedIndexRecoveryService));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        Assert.IsType<ExpandedPassageRetrieval>(scope.ServiceProvider.GetRequiredService<IHybridPassageRetrieval>());
        Assert.IsType<HybridPassageRetrievalEngine>(scope.ServiceProvider.GetRequiredService<HybridPassageRetrievalEngine>());
        Assert.IsType<ExpandedGpuRankCoordinator>(provider.GetRequiredService<IExpandedPassageRanker>());
        Assert.Equal(BgeExpandedGpuRankBackend.Profile.RuntimeKey,
            Assert.Single(provider.GetRequiredService<GpuSchedulerOptions>().WorkloadPolicy!.Profiles,
                p => p.ModelRuntimeKey == BgeExpandedGpuRankBackend.Profile.RuntimeKey).ModelRuntimeKey);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    private StagingHostSettings CreateCompositionSettings()
    {
        var settings = StagingHostSettings.Create(_ownedRoot, Connection, "http://127.0.0.1:5140");
        Directory.CreateDirectory(settings.SourcesRoot);
        return settings;
    }

    public void Dispose()
    {
        if (Directory.Exists(_ownedRoot) &&
            LiveRootLayout.CreateForIsolatedTests(_ownedRoot).ValidateOwnedPath(_ownedRoot, FileSystemLiveRootPathInspector.Instance).IsValid)
            Directory.Delete(_ownedRoot, recursive: true);
    }

    [Theory]
    [InlineData(@"I:\FluxKnowledge")]
    [InlineData(@"I:\FluxKnowledge\Staging")]
    [InlineData(@"J:\Models\Staging")]
    [InlineData(@"E:\LLM KB")]
    [InlineData(@"E:\Codex Workspaces\Staging\FluxKnowledge")]
    [InlineData(@"E:\Codex Workspaces\Staging\FluxKnowledge\A\..\B")]
    public void Rejects_foreign_or_ambiguous_roots_before_access(string root) =>
        Assert.ThrowsAny<ArgumentException>(() => StagingHostSettings.Create(root, Connection, "http://127.0.0.1:5140"));

    [Theory]
    [InlineData("Server=localhost;Database=FluxKnowledge;User ID=stage;Password=synthetic-only")]
    [InlineData("Server=elsewhere;Database=FluxKnowledge_Staging_Test;User ID=stage;Password=synthetic-only")]
    [InlineData("Server=localhost;Database=FluxKnowledge_Staging_Test;Integrated Security=True")]
    [InlineData("Server=localhost;Database=FluxKnowledge_Staging_Test;User ID=stage;Password=synthetic-only;Failover Partner=elsewhere")]
    [InlineData("Server=localhost;Database=FluxKnowledge_Staging_Test;User ID=stage;Password=synthetic-only;AttachDbFilename=E:\\other.mdf")]
    public void Rejects_unrestricted_or_redirected_SQL_before_connect(string connection) =>
        Assert.ThrowsAny<ArgumentException>(() => StagingHostSettings.Create(Root, connection, "http://127.0.0.1:5140"));

    [Theory]
    [InlineData("http://127.0.0.1:5137")]
    [InlineData("http://0.0.0.0:5140")]
    [InlineData("http://localhost:5140")]
    [InlineData("http://127.0.0.1:5140/path")]
    public void Rejects_production_or_unpinned_origin(string origin) =>
        Assert.ThrowsAny<ArgumentException>(() => StagingHostSettings.Create(Root, Connection, origin));

    [Fact]
    public void Rejects_reparse_ancestor_before_composition()
    {
        Assert.ThrowsAny<ArgumentException>(() => StagingHostSettings.Create(
            Root, Connection, "http://127.0.0.1:5140", new LinkedInspector()));
    }

    [Fact]
    public void Uses_one_staging_SQL_binding_and_excludes_native_workers()
    {
        var settings = CreateCompositionSettings();
        var services = new ServiceCollection();
        services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SqlServerOptions>();
        using var context = provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>().CreateDbContext();
        Assert.Equal(options.ConnectionString, context.Database.GetConnectionString());
        Assert.Equal(settings.Layout.SqlDataFilePath, options.DataFilePath);
        Assert.Equal(settings.Layout.SqlLogFilePath, options.LogFilePath);
        Assert.IsType<StagingSqlServerReadinessValidator>(provider.GetRequiredService<ISqlServerReadinessValidator>());
        var browserKeys = Assert.IsType<FileSystemXmlRepository>(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Equal(Path.Combine(settings.Layout.ConfigRoot, "browser-data-protection"), browserKeys.Directory.FullName);
        using var scope = provider.CreateScope();
        Assert.IsType<FluxKnowledge.Web.Components.Status.SqlProjectionReader>(scope.ServiceProvider.GetRequiredService<FluxKnowledge.Web.Components.Status.IProjectionReader>());
        // Normal production validation must still refuse a staging binding.
        Assert.False(SqlServerOptionsValidator.Validate(options).IsValid);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IGpuInteractiveOwnerProbe) ||
            descriptor.ImplementationType == typeof(GpuSchedulerService) ||
            descriptor.ImplementationType?.Name.Contains("NativeWorker", StringComparison.Ordinal) == true ||
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType?.Name.Contains("Outlook", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType?.Name.Contains("Gpu", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Segmented_staging_uses_actual_process_ownership_and_synthetic_inference_without_model_loaders()
    {
        var settings = StagingHostSettings.Create(_ownedRoot, Connection, "http://127.0.0.1:5140", indexPublicationMode: "Segmented");
        Directory.CreateDirectory(settings.SourcesRoot);
        var services = new ServiceCollection(); services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe>(provider.GetRequiredService<IGpuInteractiveOwnerProbe>());
        Assert.IsType<StagingPassageInference>(provider.GetRequiredService<IScheduledPassageInference>());
        Assert.Same(provider.GetRequiredService<IScheduledPassageInference>(), provider.GetRequiredService<IEmbeddingProvider>());
        Assert.IsAssignableFrom<IBatchedEmbeddingProvider>(provider.GetRequiredService<IEmbeddingProvider>());
        using var scope = provider.CreateScope();
        Assert.IsType<FluxKnowledge.Infrastructure.Usearch.SegmentedIndexPublisher>(scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>());
        Assert.IsType<SqlSegmentRetirementOperations>(scope.ServiceProvider.GetRequiredService<IIndexSourceDeletionMaintenance>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<IIndexSourceDeletionMaintenance>(),
            scope.ServiceProvider.GetRequiredService<IIndexProjectionMaintenance>());
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType?.Name.StartsWith("Bge", StringComparison.Ordinal) == true ||
            descriptor.ImplementationType == typeof(GpuSchedulerService) || descriptor.ImplementationType?.Name.Contains("NativeWorker", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Real_semantic_staging_is_explicit_offline_and_has_no_autonomous_writers()
    {
        var file = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147",
            IndexPublicationMode = "Segmented", DashboardReadBenchmark = true, CpuSemantic = true
        }));
        var settings = StagingHostSettings.Load(file);
        Assert.True(settings.CpuSemantic);
        Assert.Equal("Information", settings.Configuration["Logging:LogLevel:FluxKnowledge.Web.BgeCpuWarmupService"]);
        Directory.CreateDirectory(settings.SourcesRoot);
        var services = new ServiceCollection(); services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        await using var provider = services.BuildServiceProvider();
        var pool = Assert.IsType<FluxKnowledge.Infrastructure.Inference.Search.ResidentCpuPassageInference>(
            provider.GetRequiredService<IScheduledPassageInference>());
        Assert.Equal(1, pool.MaximumPendingRequests);
        Assert.True(provider.GetRequiredService<FluxKnowledge.Application.Search.CorpusRetrievalOptions>().CoherentPassagesEnabled);
        Assert.IsAssignableFrom<IBatchedEmbeddingProvider>(provider.GetRequiredService<IEmbeddingProvider>());
        using var scope = provider.CreateScope();
        Assert.IsType<FluxKnowledge.Application.Search.PassageSearchService>(scope.ServiceProvider.GetRequiredService<ISearchService>());
        Assert.DoesNotContain(services, service => service.ImplementationType == typeof(StagingPassageInference) ||
            service.ImplementationType == typeof(GpuSchedulerService) ||
            service.ImplementationType?.Name.Contains("NativeWorker", StringComparison.Ordinal) == true);
        Assert.All(provider.GetServices<IHostedService>(),
            service => Assert.True(service is FluxKnowledge.Web.BgeCpuWarmupService or FluxKnowledge.Web.CorpusAnnPreparationService or FluxKnowledge.Infrastructure.Usearch.UsearchSegmentCacheMaintenance ||
                service.GetType().Assembly == typeof(KeyManagementOptions).Assembly));
    }

    [Theory]
    [InlineData("Legacy", true, false)]
    [InlineData("Segmented", false, false)]
    [InlineData("Segmented", true, true)]
    public void Real_semantic_staging_rejects_automatic_work_or_legacy_index(string mode, bool readOnlyWorkload, bool journal)
    {
        var file = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", IndexPublicationMode = mode,
            DashboardReadBenchmark = readOnlyWorkload, CpuSemantic = true, SourceJournalEnabled = journal,
            SourceJournalVolumes = journal ? new[] { @"\\?\Volume{00000000-0000-0000-0000-000000000001}\" } : []
        }));
        Assert.Throws<ArgumentException>(() => StagingHostSettings.Load(file));
    }

    [Fact]
    public void Real_semantic_staging_can_explicitly_reduce_reranker_parallelism_while_default_stays_four()
    {
        var file = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", IndexPublicationMode = "Segmented",
            DashboardReadBenchmark = true, CpuSemantic = true, CpuRerankerShards = 1
        }));
        Assert.Equal(1, StagingHostSettings.Load(file).CpuRerankerShards);
        Assert.Equal(4, StagingHostSettings.Create(_ownedRoot, Connection, "http://127.0.0.1:5147").CpuRerankerShards);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Real_semantic_staging_refuses_unsupported_reranker_pool_sizes(int shards)
    {
        var file = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", IndexPublicationMode = "Segmented",
            DashboardReadBenchmark = true, CpuSemantic = true, CpuRerankerShards = shards
        }));
        Assert.Throws<ArgumentException>(() => StagingHostSettings.Load(file));
    }

    [Fact]
    public void Stage_source_admission_refuses_external_workspaces_before_reading_them()
    {
        var settings = CreateCompositionSettings();
        var services = new ServiceCollection();
        services.AddLogging();
        WebHostComposition.AddStagingFluxKnowledgeServices(services, settings);
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<FluxKnowledge.Application.Contracts.ISourceRootPathPolicy>();
        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateAndCanonicalise(
            new(@"E:\LLM KB", "external", true, [], [], false, 1048576, ["internal"], TimeSpan.FromMinutes(5), "staging-test")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    public void Real_semantic_staging_requires_an_explicit_supported_reranker_thread_budget(int threads, bool accepted)
    {
        var file = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", IndexPublicationMode = "Segmented",
            DashboardReadBenchmark = true, CpuSemantic = true, CpuRerankerThreads = threads
        }));
        if (accepted) Assert.Equal(threads, StagingHostSettings.Load(file).CpuRerankerThreads);
        else Assert.Throws<ArgumentException>(() => StagingHostSettings.Load(file));
        Assert.Equal(4, StagingHostSettings.Create(_ownedRoot, Connection, "http://127.0.0.1:5147").CpuRerankerThreads);
    }

    [Theory]
    [InlineData(512, true, true)]
    [InlineData(1024, true, true)]
    [InlineData(2048, true, true)]
    [InlineData(1, true, true)]
    [InlineData(8192, true, true)]
    [InlineData(0, true, false)]
    [InlineData(8193, true, false)]
    [InlineData(512, false, true)]
    [InlineData(2048, false, false)]
    public void Staging_ann_expansion_is_bounded_and_nondefault_requires_real_semantic_mode(
        int expansion, bool semantic, bool accepted)
    {
        var file = Path.Combine(_ownedRoot, "Config", "staging.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            SqlConnectionString = Connection, Origin = "http://127.0.0.1:5147", IndexPublicationMode = "Segmented",
            DashboardReadBenchmark = true, CpuSemantic = semantic, AnnSearchExpansion = expansion
        }));
        if (accepted) Assert.Equal(expansion, StagingHostSettings.Load(file).AnnSearchExpansion);
        else Assert.Throws<ArgumentException>(() => StagingHostSettings.Load(file));
        Assert.Equal(512, StagingHostSettings.Create(_ownedRoot, Connection, "http://127.0.0.1:5147").AnnSearchExpansion);
    }

    private sealed class LinkedInspector : ILiveRootPathInspector
    {
        public LiveRootPathInspection Inspect(string path) => new(true, true, path);
    }
}
