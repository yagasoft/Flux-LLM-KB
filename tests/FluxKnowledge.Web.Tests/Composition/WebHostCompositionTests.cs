using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Web;
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
                    ["LocalIngress:AllowedRoots:0"] = _ingressRoot
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
            worker => Assert.IsType<ExtractUtf8StageWorker>(worker),
            worker => Assert.IsType<NormaliseTextStageWorker>(worker));
        _ = scope.ServiceProvider.GetRequiredService<RegisterUtf8FileHandler>();
        _ = scope.ServiceProvider.GetRequiredService<
            IDbContextFactory<FluxKnowledgeDbContext>>();
    }

    public void Dispose()
    {
        Directory.Delete(_ingressRoot, recursive: true);
    }
}
