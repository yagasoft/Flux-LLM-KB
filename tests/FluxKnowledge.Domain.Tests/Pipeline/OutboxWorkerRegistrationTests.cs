using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Pipeline;

public sealed class OutboxWorkerRegistrationTests
{
    [Fact]
    public void Registration_exposes_one_shared_hosted_pump_instance()
    {
        var services = new ServiceCollection();

        services.AddFluxKnowledgeOutboxWorkers();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IOutboxPump>();
        var second = provider.GetRequiredService<IOutboxPump>();
        Assert.Same(first, second);
    }
}
