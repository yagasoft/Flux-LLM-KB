using System.Net;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Sources;
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
using FluxKnowledge.Integrations.Outlook;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Outlook;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public void Disabled_options_register_no_com_host_or_external_capture_service()
    {
        var configuration = CreateOutlookRecoveryConfiguration(enabled: null);
        var services = new ServiceCollection();
        services.AddLogging();

        WebHostComposition.AddFluxKnowledgeServices(services, configuration);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.False(provider.GetRequiredService<OutlookCaptureRecoveryOptions>().Enabled);
        Assert.Empty(provider.GetServices<IHostedService>().OfType<OutlookCaptureRecoveryService>());
        Assert.DoesNotContain(
            typeof(WebHostComposition).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.Contains("OutlookHost", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Assembly.GetName().Name?.Contains(
                "OutlookHost",
                StringComparison.Ordinal) == true ||
                descriptor.ImplementationType?.Assembly.GetName().Name?.Contains(
                    "OutlookHost",
                    StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("invalid")]
    public void Retained_processor_configuration_rejects_an_out_of_range_automatic_batch(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(CreateOutlookRecoveryConfiguration(enabled: null))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RetainedProcessors:AutomaticReplayBatchSize"] = configured
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            WebHostComposition.AddFluxKnowledgeServices(new ServiceCollection(), configuration));
    }

    [Fact]
    public void Retained_processor_configuration_defaults_to_sixteen_and_accepts_the_shared_upper_bound()
    {
        var defaultServices = new ServiceCollection();
        WebHostComposition.AddFluxKnowledgeServices(
            defaultServices,
            CreateOutlookRecoveryConfiguration(enabled: null));
        using var defaultProvider = defaultServices.BuildServiceProvider();

        var configuredServices = new ServiceCollection();
        WebHostComposition.AddFluxKnowledgeServices(
            configuredServices,
            new ConfigurationBuilder()
                .AddConfiguration(CreateOutlookRecoveryConfiguration(enabled: null))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RetainedProcessors:AutomaticReplayBatchSize"] = "16",
                ["RetainedProcessors:MediaMetadataEnabled"] = "true"
            }).Build());
        using var configuredProvider = configuredServices.BuildServiceProvider();

        Assert.Equal(16, defaultProvider.GetRequiredService<RetainedProcessorOptions>().AutomaticReplayBatchSize);
        Assert.Equal(16, configuredProvider.GetRequiredService<RetainedProcessorOptions>().AutomaticReplayBatchSize);
        Assert.False(defaultProvider.GetRequiredService<RetainedProcessorOptions>().MediaMetadataEnabled);
        Assert.True(configuredProvider.GetRequiredService<RetainedProcessorOptions>().MediaMetadataEnabled);
    }

    [Fact]
    public void Enabled_options_register_only_durable_Outlook_recovery()
    {
        var configuration = CreateOutlookRecoveryConfiguration(enabled: true);
        var services = new ServiceCollection();
        services.AddLogging();

        WebHostComposition.AddFluxKnowledgeServices(services, configuration);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var options = provider.GetRequiredService<OutlookCaptureRecoveryOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HintDebounce);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RecoveryCadence);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StaleLeaseAge);
        Assert.Single(provider.GetServices<IHostedService>().OfType<OutlookCaptureRecoveryService>());
        using var scope = provider.CreateScope();
        Assert.IsType<SqlOutlookCaptureStore>(
            scope.ServiceProvider.GetRequiredService<IOutlookCaptureRecoveryStore>());
        Assert.DoesNotContain(
            provider.GetServices<IHostedService>(),
            service => service.GetType().Assembly.GetName().Name?.Contains(
                "OutlookHost",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Outlook_validation_projection_uses_environment_and_command_line_precedence()
    {
        var prefix = $"FLUX_TEST_OUTLOOK_{Guid.NewGuid():N}_";
        var environmentName = $"{prefix}OutlookCapture__Enabled";
        Environment.SetEnvironmentVariable(environmentName, "true");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OutlookCapture:Enabled"] = "false"
                })
                .AddEnvironmentVariables(prefix)
                .AddCommandLine(["--OutlookCapture:Enabled=false"])
                .Build();

            var projection = OutlookCaptureConfigurationProjection.Create(configuration);

            Assert.False(projection.OutlookEnabled);
            Assert.Single(projection.GetType().GetProperties());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Fact]
    public void Outlook_validation_projection_observes_an_environment_override()
    {
        var prefix = $"FLUX_TEST_OUTLOOK_{Guid.NewGuid():N}_";
        var environmentName = $"{prefix}OutlookCapture__Enabled";
        Environment.SetEnvironmentVariable(environmentName, "true");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OutlookCapture:Enabled"] = "false"
                })
                .AddEnvironmentVariables(prefix)
                .Build();

            Assert.True(OutlookCaptureConfigurationProjection.Create(configuration).OutlookEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Fact]
    public void Composition_registers_the_SQL_only_Outlook_UI_without_a_COM_or_process_service()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FluxKnowledge"] = "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                ["LocalIngress:AllowedRoots:0"] = _ingressRoot,
                ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}"),
                ["Outlook:AllowedSpoolRoots:0"] = _ingressRoot
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        WebHostComposition.AddFluxKnowledgeServices(services, configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.IsType<SqlOutlookCaptureStore>(scope.ServiceProvider.GetRequiredService<IOutlookCaptureStore>());
        Assert.IsType<SqlOutlookProjectionReader>(scope.ServiceProvider.GetRequiredService<IOutlookProjectionReader>());
        Assert.IsType<LocalOutlookSpoolValidator>(scope.ServiceProvider.GetRequiredService<IOutlookSpoolValidator>());
        Assert.IsType<LocalOutlookOperatorPolicy>(scope.ServiceProvider.GetRequiredService<IOutlookOperatorPolicy>());
        _ = scope.ServiceProvider.GetRequiredService<OutlookPageState>();
        Assert.DoesNotContain(
            provider.GetServices<IHostedService>(),
            service => service.GetType().Assembly.GetName().Name?.Contains("OutlookHost", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Native_outlook_adds_no_REST_MCP_or_CLI_mutation_surface()
    {
        var endpointTypes = typeof(WebHostComposition).Assembly.GetTypes()
            .Where(type => string.Equals(type.Namespace, "FluxKnowledge.Web.Endpoints", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(endpointTypes, type => type.Name.Contains("Outlook", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(KnowledgeMcpTools).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Outlook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Force_request_foundation_adds_no_public_endpoint_or_MCP_mutation_surface()
    {
        var endpointTypes = typeof(WebHostComposition).Assembly.GetTypes()
            .Where(type => string.Equals(type.Namespace, "FluxKnowledge.Web.Endpoints", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(endpointTypes, type => type.Name.Contains("OoxmlForce", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(KnowledgeMcpTools).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Force", StringComparison.OrdinalIgnoreCase));

        using var factory = new ConfiguredWebApplicationFactory(_ingressRoot);
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/operator-actions/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/force-process";
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Response.StatusCode);
        Assert.DoesNotContain(HttpMethods.Post, response.Response.Headers.Allow.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Operator_actions_add_no_MCP_mutation_tool()
    {
        var publicMethods = typeof(KnowledgeMcpTools).GetMethods(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(publicMethods, method =>
            method.Name.Contains("Operator", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Override", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Retry", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Ignore", StringComparison.OrdinalIgnoreCase));
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
        Assert.IsType<SourceRootPathPolicy>(
            provider.GetRequiredService<ISourceRootPathPolicy>());
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is OutboxPumpService);
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is LocalSourceRootWatchHostedService);
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
        _ = scope.ServiceProvider.GetRequiredService<IRetainedArtifactWriter>();
        Assert.IsType<SqlGpuSchedulerStore>(
            scope.ServiceProvider.GetRequiredService<IGpuSchedulerStore>());
        Assert.IsType<SqlGpuSchedulerStore>(
            scope.ServiceProvider.GetRequiredService<IGpuExecutorDispatchStore>());
        Assert.IsType<NoGpuAdmissionGate>(
            scope.ServiceProvider.GetRequiredService<IGpuAdmissionGate>());
        var localHandlers = provider.GetRequiredService<ILocalSourceCapabilityHandlerRegistry>();
        Assert.True(localHandlers.TryResolve(new Guid("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df"), out var localHandler));
        Assert.Equal("pipeline:extract-utf8", localHandler.OutputContract);
        Assert.True(localHandlers.TryResolve(new Guid("b4a06e5d-6f01-4f73-9722-79b6df4e85c3"), out var zipHandler));
        Assert.Equal("retained:archive-zip-expand", zipHandler.OutputContract);
        Assert.True(localHandlers.TryResolve(new Guid("3d8e4b4e-8d16-45c7-aa02-c4e546ba997d"), out var tarHandler));
        Assert.Equal("retained:archive-tar-expand", tarHandler.OutputContract);
        Assert.True(localHandlers.TryResolve(MediaMetadataRetainedProcessor.Capability.Id, out var mediaMetadataHandler));
        Assert.Equal("retained:media-metadata-v1", mediaMetadataHandler.OutputContract);
        Assert.IsType<GpuExecutorLifecycleCoordinator>(
            scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>());
        Assert.IsType<ChannelGpuExecutorDispatchSignal>(
            provider.GetRequiredService<IGpuExecutorDispatchSignal>());
        Assert.Empty(scope.ServiceProvider.GetServices<IGpuExecutorAdapter>());
        Assert.Single(
            provider.GetServices<IHostedService>().OfType<GpuExecutorDispatchRecoveryService>());
        Assert.IsType<SqlProjectionReader>(
            scope.ServiceProvider.GetRequiredService<IProjectionReader>());
        Assert.IsType<SqlSourceRootStore>(
            scope.ServiceProvider.GetRequiredService<ISourceRootStore>());
        _ = scope.ServiceProvider.GetRequiredService<SourceRootService>();
        _ = scope.ServiceProvider.GetRequiredService<SourceScanControlService>();
        Assert.IsType<SqlSourceRootWatchStore>(scope.ServiceProvider.GetRequiredService<ISourceRootWatchStore>());
    }

    [Fact]
    public void Composition_rejects_an_artifact_root_configured_inside_a_protected_root()
    {
        var protectedRoot = Path.Combine(_ingressRoot, "protected");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FluxKnowledge"] = "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                ["LocalIngress:AllowedRoots:0"] = _ingressRoot,
                ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}"),
                ["SourceRootPolicy:ProtectedRoots:0"] = protectedRoot,
                ["SourceArtifactStore:Root"] = Path.Combine(protectedRoot, "artifacts")
            })
            .Build();

        Assert.Throws<ArgumentException>(() =>
            WebHostComposition.AddFluxKnowledgeServices(new ServiceCollection(), configuration));
    }

    [Fact]
    public async Task Composition_wires_the_local_reprocessor_without_connecting_to_SQL()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FluxKnowledge"] = "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                ["LocalIngress:AllowedRoots:0"] = _ingressRoot,
                ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}")
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        WebHostComposition.AddFluxKnowledgeServices(services, configuration);
        services.AddScoped<ISourceRootProjectionReader, ReplayAvailableProjectionReader>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<SourceRootDetailPageState>();

        await state.LoadAsync(Guid.Parse("33333333-3333-3333-3333-333333333333"), CancellationToken.None);
        Assert.True(state.CanReprocessDeferredContent);
    }

    [Fact]
    public async Task Actual_program_entrypoint_registers_no_authentication_surface_and_rejects_remote_Outlook_requests()
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

        Assert.Null(scope.ServiceProvider.GetService<IAuthenticationService>());
        Assert.Null(scope.ServiceProvider.GetService<IAuthenticationSchemeProvider>());
        Assert.False(services.GetRequiredService<AuthenticationMiddlewareProbe>().WasConfigured);

        foreach (var path in new[] { "/outlook", "/operator-actions", "/api/operator-actions", "/search/csharp-code", "/_blazor", "/mcp" })
        {
            var remote = await factory.Server.SendAsync(context =>
            {
                context.Request.Path = path;
                context.Request.Headers["Forwarded"] = "for=127.0.0.1";
                context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
            });

            Assert.Equal(StatusCodes.Status403Forbidden, remote.Response.StatusCode);
        }

        var loopback = await factory.Server.SendAsync(context =>
        {
            context.Request.Path = "/_blazor";
            context.Request.Headers["Forwarded"] = "for=198.51.100.50";
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.50";
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status403Forbidden, loopback.Response.StatusCode);

        var forwardedMcpReconnect = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/mcp";
            context.Request.Headers["Forwarded"] = "for=198.51.100.50";
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status403Forbidden, forwardedMcpReconnect.Response.StatusCode);
    }

    [Fact]
    public void Web_assembly_has_no_Negotiate_authentication_dependency()
    {
        Assert.DoesNotContain(
            typeof(Program).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(
                reference.Name,
                "Microsoft.AspNetCore.Authentication.Negotiate",
                StringComparison.Ordinal));
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

    private IConfiguration CreateOutlookRecoveryConfiguration(bool? enabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:FluxKnowledge"] = "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            ["LocalIngress:AllowedRoots:0"] = _ingressRoot,
            ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}"),
            ["OutlookCapture:HintDebounceSeconds"] = "5",
            ["OutlookCapture:RecoveryCadenceSeconds"] = "60",
            ["OutlookCapture:StaleLeaseSeconds"] = "600"
        };
        if (enabled is not null)
        {
            values["OutlookCapture:Enabled"] = enabled.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
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
                services =>
                {
                    // The entrypoint authority check needs the real local gate but no background
                    // SQL activity. Keep only the pump backed by EmptyOutboxStore; all other
                    // production hosted services would otherwise contact the deliberately
                    // unreachable placeholder catalogue during this non-SQL test.
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<IHostedService>(provider =>
                        provider.GetRequiredService<OutboxPumpService>());
                    services.AddSingleton<IOutboxStore, EmptyOutboxStore>();
                    services.AddSingleton<AuthenticationMiddlewareProbe>();
                    services.AddSingleton<IStartupFilter>(provider =>
                        provider.GetRequiredService<AuthenticationMiddlewareProbe>());
                });
        }
    }

    private sealed class AuthenticationMiddlewareProbe : IStartupFilter
    {
        public bool WasConfigured { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            builder =>
            {
                next(builder);
                WasConfigured = builder.Properties.ContainsKey("__AuthenticationMiddlewareSet");
            };
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

    private sealed class ReplayAvailableProjectionReader : ISourceRootProjectionReader
    {
        private static readonly SourceRootDetailProjection Detail = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Root",
            "E:\\Corpus",
            "Enabled",
            "Completed",
            null,
            0,
            0,
            1,
            0,
            0,
            [],
            [new DeferredContentReplayRequest(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "44444444444444444444444444444444|2|11:phase-3a-v1|64:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "local-source-capability",
                Guid.Parse("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df"),
                "phase-3a-v1",
                "phase-3a-inprocess-text-metadata-v1")]);

        public ValueTask<IReadOnlyList<SourceRootListProjection>> ReadRootsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceRootListProjection>>([]);

        public ValueTask<SourceRootDetailProjection?> ReadRootAsync(Guid rootId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SourceRootDetailProjection?>(Detail);

        public ValueTask<SourceRootPreview> PreviewAsync(SourceRootDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
