using System.Reflection;
using System.Reflection.Emit;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
    public void Production_csharp_preflight_fails_closed_when_a_compiler_service_is_registered_after_worker_composition()
    {
        var cleanServices = new ServiceCollection();
        cleanServices.AddSingleton<IRetainedSourceReader, UnusedRetainedReader>();
        cleanServices.AddSingleton<ILocalPrivateContentDisclosure, AllowingDisclosure>();
        cleanServices.AddFluxKnowledgeOutboxWorkers();
        using var cleanProvider = cleanServices.BuildServiceProvider();
        using var cleanScope = cleanProvider.CreateScope();
        Assert.True(cleanScope.ServiceProvider.GetRequiredService<RetainedCsharpCodeProcessor>()
            .Preflight()
            .IsAvailable);

        foreach (var forbiddenServiceType in new[]
                 {
                     typeof(ISourceGenerator),
                     typeof(DiagnosticAnalyzer),
                     CreateSyntheticRoslynWorkspaceType()
                 })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IRetainedSourceReader, UnusedRetainedReader>();
            services.AddSingleton<ILocalPrivateContentDisclosure, AllowingDisclosure>();
            services.AddFluxKnowledgeOutboxWorkers();
            services.AddSingleton(forbiddenServiceType, _ => null!);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var preflight = scope.ServiceProvider.GetRequiredService<RetainedCsharpCodeProcessor>()
                .Preflight();

            Assert.False(preflight.IsAvailable);
            Assert.Equal("processor-parser-unavailable", preflight.ReasonCode);
        }
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

    private sealed class UnusedRetainedReader : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Preflight must not read retained bytes.");

        public ValueTask<Utf8FileSource> ReadUtf8Async(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Preflight must not read source originals.");
    }

    private sealed class AllowingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }

    private static Type CreateSyntheticRoslynWorkspaceType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"FluxKnowledge.PreflightTests.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule("Preflight")
            .DefineType(
                "Microsoft.CodeAnalysis.SyntheticWorkspaceService",
                TypeAttributes.Public | TypeAttributes.Class)
            .CreateType()!;
    }
}
