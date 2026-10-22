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

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingHostCompositionTests : IDisposable
{
    private const string Root = @"E:\Codex Workspaces\Staging\FluxKnowledge\CompositionTests";
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

    private sealed class ExistingHostService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

    private sealed class LinkedInspector : ILiveRootPathInspector
    {
        public LiveRootPathInspection Inspect(string path) => new(true, true, path);
    }
}
