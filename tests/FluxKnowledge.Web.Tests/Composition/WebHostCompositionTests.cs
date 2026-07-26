using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web;
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
}
