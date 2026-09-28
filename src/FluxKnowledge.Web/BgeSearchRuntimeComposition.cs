using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FluxKnowledge.Web;

internal static class BgeSearchRuntimeComposition
{
    // Provisional conservative admission estimate; real peak/latency gates still apply before activation.
    private const long EstimatedGpuBytes = 8L * 1024 * 1024 * 1024;
    internal static void Add(IServiceCollection services)
    {
        var policy = new GpuWorkloadPolicy([
            new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
            new(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, GpuWorkloadKind.Retrieval)]);
        var runtime = new EmbeddingGpuRuntime(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint,
            new(BgeOfflineModels.EmbeddingFingerprint, 1024), EstimatedGpuBytes);
        services.AddSingleton(runtime);
        services.Replace(ServiceDescriptor.Singleton(new GpuSchedulerOptions(1, EstimatedGpuBytes,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), policy)));
        services.Replace(ServiceDescriptor.Singleton<IGpuAdmissionGate>(new SharedGpuAdmissionGate(policy,
            EmbeddingGpuExecutor.Name, EstimatedGpuBytes)));
        services.AddSingleton(_ => CreateModelStores());
        services.AddSingleton<IBgeGpuModelFactory, BgeGpuModelFactory>();
        services.AddSingleton<BgeGpuInferenceSession>();
        services.AddSingleton<ScopedGpuExecutorLifecycleSink>();
        services.AddSingleton(provider =>
        {
            var store = NewSchedulerStore(provider);
            var wake = provider.GetRequiredService<IGpuSchedulerWakeSignal>();
            var clock = provider.GetRequiredService<TimeProvider>();
            var recovery = new GpuInteractiveOwnerRecovery(store, store, store,
                provider.GetRequiredService<IGpuInteractiveOwnerProbe>(), wake, clock);
            return new GpuInteractiveExecutor(store, provider.GetRequiredService<ScopedGpuExecutorLifecycleSink>(), store, wake,
                clock, runtime.RuntimeKey, runtime.SettingsFingerprint, runtime.EstimatedBytes,
                provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, recovery);
        });
        services.AddSingleton<IGpuExecutorAdapter>(provider => provider.GetRequiredService<GpuInteractiveExecutor>());
        services.AddSingleton(provider => new SqlEmbeddingGpuRequestStore(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), NewSchedulerStore(provider),
            provider.GetRequiredService<IGpuSchedulerWakeSignal>(), runtime, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IEmbeddingGpuRequestStore>(provider => provider.GetRequiredService<SqlEmbeddingGpuRequestStore>());
        services.AddScoped<IEmbeddingGpuHandoff>(provider => provider.GetRequiredService<SqlEmbeddingGpuRequestStore>());
        services.AddSingleton(provider => new EmbeddingGpuExecutor(provider.GetRequiredService<IEmbeddingGpuRequestStore>(),
            provider.GetRequiredService<ScopedGpuExecutorLifecycleSink>(), NewSchedulerStore(provider),
            provider.GetRequiredService<BgeGpuInferenceSession>(), provider.GetRequiredService<IGpuInteractiveOwnerProbe>(), runtime,
            provider.GetRequiredService<IOutboxWakeSignal>(), provider.GetRequiredService<IGpuSchedulerWakeSignal>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        services.AddSingleton<IGpuExecutorAdapter>(provider => provider.GetRequiredService<EmbeddingGpuExecutor>());
        services.AddSingleton<IScheduledPassageInference, BgeScheduledPassageInference>();
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingProvider, ScheduledBgeEmbeddingProvider>());
        services.AddSingleton<BgePassageTokenizer>();
        services.AddSingleton<PassageBuilder>(provider => new(provider.GetRequiredService<BgePassageTokenizer>()));
        services.AddSingleton(new CorpusRetrievalOptions(true));
        services.AddScoped<IHybridPassageCandidateReader>(provider =>
            (IHybridPassageCandidateReader)provider.GetRequiredService<ICorpusRetrievalReader>());
        services.AddScoped<IHybridPassageRetrieval, HybridPassageRetrievalEngine>();
        services.Replace(ServiceDescriptor.Scoped<ISearchService, PassageSearchService>());
    }

    private static SqlGpuSchedulerStore NewSchedulerStore(IServiceProvider provider) =>
        new(provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), timeProvider: provider.GetRequiredService<TimeProvider>(),
            deploymentValidationHold: provider.GetService<IDeploymentValidationHold>());

    private static BgeGpuModelStores CreateModelStores() => new(
        Store(["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]),
        Store(["bundles", "bge-reranker-v2-m3-onnx", BgeOfflineModels.RerankerRevision, BgeOfflineModels.RerankerExport]),
        Store(["bundles", "bge-reranker-v2-m3", BgeOfflineModels.RerankerRevision]),
        Store(["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
    private static PinnedLocalModelStore Store(string[] components) =>
        new(new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(components)));
}
