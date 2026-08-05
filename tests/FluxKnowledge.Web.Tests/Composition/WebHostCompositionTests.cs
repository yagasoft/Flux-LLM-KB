using System.Net;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class WebHostCompositionTests : IDisposable
{
    private readonly string _ingressRoot =
        Path.Combine(Path.GetTempPath(), $"FluxKnowledgeWebHost_{Guid.NewGuid():N}");

    public WebHostCompositionTests()
    {
        Directory.CreateDirectory(_ingressRoot);
    }

    [Fact]
    public void Program_composition_resolves_the_hosted_pump_and_both_stage_workers_without_connecting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FluxKnowledge"] =
                        "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                        "Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                    ["LocalIngress:AllowedRoots:0"] = _ingressRoot,
                    ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}")
                })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        WebHostComposition.AddFluxKnowledgeServices(services, configuration);
        services.AddFluxKnowledgeGpuScheduler();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        Assert.Equal(
            "FluxKnowledge",
            provider.GetRequiredService<SqlServerOptions>()
                .ConnectionString
                .Split("Initial Catalog=", StringSplitOptions.None)[1]
                .Split(';')[0]);
        Assert.IsType<Utf8FileSourceReader>(
            provider.GetRequiredService<IUtf8FileSourceReader>());
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is OutboxPumpService);
        using var scope = provider.CreateScope();
        var workers = scope.ServiceProvider.GetServices<IStageWorker>().ToArray();
        Assert.Collection(
            workers.OrderBy(worker => worker.Operation, StringComparer.Ordinal),
            worker => Assert.IsType<CanonicalIndexStageWorker>(worker),
            worker => Assert.IsType<EmbedStageWorker>(worker),
            worker => Assert.IsType<ExtractUtf8StageWorker>(worker),
            worker => Assert.IsType<NormaliseTextStageWorker>(worker),
            worker => Assert.IsType<PublishStageWorker>(worker));
        _ = scope.ServiceProvider.GetRequiredService<RegisterUtf8FileHandler>();
        _ = scope.ServiceProvider.GetRequiredService<
            IDbContextFactory<FluxKnowledgeDbContext>>();
        Assert.IsType<SqlGpuSchedulerStore>(
            scope.ServiceProvider.GetRequiredService<IGpuSchedulerStore>());
        Assert.IsType<SqlGpuSchedulerStore>(
            scope.ServiceProvider.GetRequiredService<IGpuExecutorDispatchStore>());
        Assert.IsType<NoGpuAdmissionGate>(
            scope.ServiceProvider.GetRequiredService<IGpuAdmissionGate>());
        Assert.IsType<GpuExecutorLifecycleCoordinator>(
            scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>());
        Assert.IsType<ChannelGpuExecutorDispatchSignal>(
            provider.GetRequiredService<IGpuExecutorDispatchSignal>());
        Assert.Empty(scope.ServiceProvider.GetServices<IGpuExecutorAdapter>());
        Assert.Single(
            provider.GetServices<IHostedService>().OfType<GpuExecutorDispatchRecoveryService>());
        Assert.IsType<SqlProjectionReader>(
            scope.ServiceProvider.GetRequiredService<IProjectionReader>());
    }

    [Fact]
    public void Actual_program_entrypoint_registers_the_pump_reader_and_workers_without_sql_io()
    {
        using var factory = new ConfiguredWebApplicationFactory(_ingressRoot);

        var services = factory.Services;

        Assert.IsType<Utf8FileSourceReader>(
            services.GetRequiredService<IUtf8FileSourceReader>());
        Assert.Contains(
            services.GetServices<IHostedService>(),
            service => service is OutboxPumpService);
        using var scope = services.CreateScope();
        Assert.Collection(
            scope.ServiceProvider
                .GetServices<IStageWorker>()
                .OrderBy(worker => worker.Operation, StringComparer.Ordinal),
            worker => Assert.IsType<CanonicalIndexStageWorker>(worker),
            worker => Assert.IsType<EmbedStageWorker>(worker),
            worker => Assert.IsType<ExtractUtf8StageWorker>(worker),
            worker => Assert.IsType<NormaliseTextStageWorker>(worker),
            worker => Assert.IsType<PublishStageWorker>(worker));
    }

    [Theory]
    [MemberData(nameof(InvalidUsearchRootPaths))]
    public async Task Invalid_Usearch_configuration_starts_the_native_host_and_exposes_only_safe_recovery_status(
        string? configuredRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Usearch:RootPath"] = configuredRoot
            })
            .Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddFluxKnowledgeUsearch(configuration);
        builder.Services.AddSingleton<ISqlServerReadinessValidator>(new ReadyReadinessValidator());
        builder.Services.AddSingleton(SqlServerOptions.ForProduction(
            "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
            "Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath));
        await using var application = builder.Build();
        application.MapFluxKnowledgeHealth();
        application.MapFluxKnowledgeIndexHealth();

        await application.StartAsync();

        var recovery = await WaitForRecoveryStateAsync(application.Services);
        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, recovery.State);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, recovery.FailureCategory);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await application.GetTestClient().GetAsync("/health/ready")).StatusCode);

        using var statusResponse = await application.GetTestClient().GetAsync("/api/index-health");
        var status = await statusResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Contains("OperatorActionRequired", status, StringComparison.Ordinal);
        Assert.Contains("ConfigurationInvalid", status, StringComparison.Ordinal);
        Assert.DoesNotContain("rootPath", status, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object?[]> InvalidUsearchRootPaths =>
    [
        [null],
        [Path.Combine(AppContext.BaseDirectory, "unsafe-usearch-root")],
        ["\0"]
    ];

    [Fact]
    public async Task Invalid_Usearch_configuration_records_sanitised_operator_evidence_when_the_recovery_store_is_available()
    {
        var recoveryStore = new CapturingRecoveryStore();
        var services = new ServiceCollection();
        services.AddSingleton<IDerivedIndexRecoveryStore>(recoveryStore);
        using var provider = services.BuildServiceProvider();
        var configuration = UsearchIndexConfiguration.FromConfiguredRoot(null);
        var coordinator = new DerivedIndexRecoveryCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            TimeProvider.System);

        await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
        Assert.Collection(
            recoveryStore.AuditEvents,
            detected =>
            {
                Assert.Equal("recovery_detected", detected.EventType);
                Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, detected.FailureCategory);
                Assert.Null(detected.ActiveGenerationId);
                Assert.Equal(TimeSpan.Zero, detected.Elapsed);
                Assert.Null(detected.NextRetryAtUtc);
                Assert.Equal(0, detected.CleanedCandidateCount);
            },
            operatorRequired =>
            {
                Assert.Equal("recovery_operator_required", operatorRequired.EventType);
                Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, operatorRequired.FailureCategory);
                Assert.Null(operatorRequired.ActiveGenerationId);
                Assert.Equal(TimeSpan.Zero, operatorRequired.Elapsed);
                Assert.Null(operatorRequired.NextRetryAtUtc);
                Assert.Equal(0, operatorRequired.CleanedCandidateCount);
            });
    }

    private static async Task<DerivedIndexRecoverySnapshot> WaitForRecoveryStateAsync(IServiceProvider services)
    {
        var recoveryStatus = services.GetRequiredService<IDerivedIndexRecoveryStatus>();
        var snapshot = recoveryStatus.Snapshot;
        for (var attempt = 0; attempt < 100 && snapshot.State == DerivedIndexRecoveryState.Starting; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
            snapshot = recoveryStatus.Snapshot;
        }

        return snapshot;
    }

    public void Dispose()
    {
        Directory.Delete(_ingressRoot, recursive: true);
    }

    private sealed class ConfiguredWebApplicationFactory(string ingressRoot)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(
                "ConnectionStrings:FluxKnowledge",
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                "Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", ingressRoot);
            builder.UseSetting("Usearch:RootPath", Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}"));
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:FluxKnowledge"] =
                                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                                "Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                            ["LocalIngress:AllowedRoots:0"] = ingressRoot,
                            ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}")
                        }));
            builder.ConfigureTestServices(
                services => services.AddSingleton<IOutboxStore, EmptyOutboxStore>());
        }
    }

    private sealed class EmptyOutboxStore : IOutboxStore
    {
        public ValueTask EnqueueAsync(
            DispatchMessage message,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<ClaimedDispatchMessage?> ClaimNextDueAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            IReadOnlyCollection<string> registeredOperations,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ClaimedDispatchMessage?>(null);

        public ValueTask ReleaseAsync(
            ClaimedDispatchMessage claim,
            DateTimeOffset dueAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ReadyReadinessValidator : ISqlServerReadinessValidator
    {
        public Task<SqlServerReadinessResult> ValidateAsync(
            SqlServerOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqlServerReadinessResult(true, []));
    }

    private sealed class CapturingRecoveryStore : IDerivedIndexRecoveryStore
    {
        public List<DerivedIndexRecoveryAuditEvent> AuditEvents { get; } = [];

        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid USearch configuration must not read the recovery store.");

        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid USearch configuration must not acquire a recovery lease.");

        public ValueTask<bool> TryUpdateRecoveryPathAsync(
            Guid expectedActiveGenerationId,
            string expectedIndexPath,
            string replacementIndexPath,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid USearch configuration must not update recovery paths.");

        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            AuditEvents.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
