using FluxKnowledge.Web.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web;

/// <summary>Isolated staging uses the normal GPU scheduler and executors, with autonomous source writers disabled.</summary>
internal static class StagingNormalGpuComposition
{
    // Metadata identity only: native deletion still proves task provenance and cleanup; no executor or model is constructed.
    internal static void AddCleanupProfile(IServiceCollection services) => services.AddSingleton(new EmbeddingGpuRuntime(
        BgeOfflineModels.GpuRuntimeKey,BgeOfflineModels.GpuSettingsFingerprint,new(BgeOfflineModels.EmbeddingFingerprint,1024),8L*1024*1024*1024));

    internal static void Add(IServiceCollection services, StagingHostSettings settings)
    {
        if (!settings.CpuSemantic || !settings.DashboardReadBenchmark || settings.SourceJournalEnabled || settings.IndexPublicationMode != "Segmented")
            throw new ArgumentException("Normal GPU staging requires isolated segmented staging with autonomous source writers disabled.");
        services.AddFluxKnowledgeGpuScheduler();
        BgeSearchRuntimeComposition.Add(services);
        foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(DerivedIndexRecoveryService)).ToArray())
            services.Remove(service);
    }
}
