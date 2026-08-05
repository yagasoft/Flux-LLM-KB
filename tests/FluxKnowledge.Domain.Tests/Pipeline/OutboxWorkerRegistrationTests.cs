using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public void Scheduler_registration_adds_a_separate_hosted_service_with_the_safe_default_gate()
    {
        var services = new ServiceCollection();

        services.AddFluxKnowledgeGpuScheduler();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<NoGpuAdmissionGate>(provider.GetRequiredService<IGpuAdmissionGate>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is GpuSchedulerService);
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is GpuExecutorDispatchRecoveryService);
        Assert.Empty(provider.GetServices<IGpuExecutorAdapter>());
    }

    [Fact]
    public void Scheduler_registration_is_duplicate_safe()
    {
        var services = new ServiceCollection();

        services.AddFluxKnowledgeGpuScheduler();
        services.AddFluxKnowledgeGpuScheduler();
        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<GpuSchedulerService>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<GpuExecutorDispatchRecoveryService>());
    }
}
