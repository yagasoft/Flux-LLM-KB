using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FluxKnowledge.Web;

internal static class BgeSearchRuntimeComposition
{
    // Provisional conservative admission estimate; real peak/latency gates still apply before activation.
    private const long EstimatedGpuBytes = 8L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SearchExecutionTimeout = TimeSpan.FromSeconds(20);
    internal static void Add(IServiceCollection services)
    {
        var policy = new GpuWorkloadPolicy([
            new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
            new(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, GpuWorkloadKind.Retrieval),
            new(BgeExpandedGpuRankBackend.Profile.RuntimeKey, BgeExpandedGpuRankBackend.Profile.SettingsFingerprint, GpuWorkloadKind.Retrieval)]);
        var runtime = new EmbeddingGpuRuntime(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint,
            new(BgeOfflineModels.EmbeddingFingerprint, 1024), EstimatedGpuBytes);
        services.AddSingleton(runtime);
        services.Replace(ServiceDescriptor.Singleton(new GpuSchedulerOptions(1, EstimatedGpuBytes,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), policy)));
        services.Replace(ServiceDescriptor.Singleton<IGpuAdmissionGate>(new SharedGpuAdmissionGate(policy,
            EmbeddingGpuExecutor.Name, EstimatedGpuBytes)));
        services.TryAddSingleton(_ => CreateModelStores());
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
                new[] { BgeExpandedGpuRankBackend.Profile },
                provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, recovery,
                executionTimeout: SearchExecutionTimeout);
        });
        services.AddSingleton<IGpuExecutorAdapter>(provider => provider.GetRequiredService<GpuInteractiveExecutor>());
        services.AddSingleton(provider => new SqlEmbeddingGpuRequestStore(
            provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), NewSchedulerStore(provider),
            provider.GetRequiredService<IGpuSchedulerWakeSignal>(), runtime, provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IDeploymentValidationHold>(), provider.GetRequiredService<ISourceWorkAdmission>()));
        services.AddSingleton<IEmbeddingGpuRequestStore>(provider => provider.GetRequiredService<SqlEmbeddingGpuRequestStore>());
        services.AddScoped<IEmbeddingGpuHandoff>(provider => provider.GetRequiredService<SqlEmbeddingGpuRequestStore>());
        services.AddSingleton(provider => new EmbeddingGpuExecutor(provider.GetRequiredService<IEmbeddingGpuRequestStore>(),
            provider.GetRequiredService<ScopedGpuExecutorLifecycleSink>(), NewSchedulerStore(provider),
            provider.GetRequiredService<BgeGpuInferenceSession>(), provider.GetRequiredService<IGpuInteractiveOwnerProbe>(), runtime,
            provider.GetRequiredService<IOutboxWakeSignal>(), provider.GetRequiredService<IGpuSchedulerWakeSignal>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        services.AddSingleton<IGpuExecutorAdapter>(provider => provider.GetRequiredService<EmbeddingGpuExecutor>());
        services.AddSingleton<BgeScheduledPassageInference>();
        services.AddSingleton<IConditionalGpuPassageInference>(provider => provider.GetRequiredService<BgeScheduledPassageInference>());
        services.TryAddSingleton(provider => new ResidentCpuPassageInference(
            ct => BgeCpuResidentModelFactory.LoadAsync(provider.GetRequiredService<BgeGpuModelStores>(),
                async ownerToken => (IDisposable)await CpuSearchOwnerLease.AcquireProductionAsync(ownerToken).ConfigureAwait(false), ct),
            new EmbeddingProfile(BgeOfflineModels.EmbeddingFingerprint, 1024), BgeOfflineModels.RerankerFingerprint));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BgeCpuWarmupService>());
        services.AddSingleton<IScheduledPassageInference>(provider => new GpuFirstPassageInference(
            provider.GetRequiredService<IConditionalGpuPassageInference>(),
            provider.GetRequiredService<ResidentCpuPassageInference>()));
        // Publication changes can occur after the strict startup probe, including while a deployment hold is active.
        services.AddHostedService<DerivedIndexRecoveryService>();
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingProvider, ScheduledBgeEmbeddingProvider>());
        services.AddSingleton<BgePassageTokenizer>();
        services.AddSingleton<PassageBuilder>(provider => new(provider.GetRequiredService<BgePassageTokenizer>()));
        services.AddSingleton(new CorpusRetrievalOptions(true));
        services.AddScoped<IHybridPassageCandidateReader>(provider =>
            (IHybridPassageCandidateReader)provider.GetRequiredService<ICorpusRetrievalReader>());
        services.AddKeyedSingleton("expanded-ranking", (_, _) => CreateFp16ModelStores());
        services.AddKeyedSingleton<IBgeGpuModelFactory>("expanded-ranking", (provider, _) =>
            new BgeGpuModelFactory(provider.GetRequiredKeyedService<BgeGpuModelStores>("expanded-ranking"), BgeRerankerPrecision.Fp16Internal));
        services.AddSingleton(provider => new BgeExpandedGpuRankBackend(provider.GetRequiredService<GpuInteractiveExecutor>(),
            provider.GetRequiredKeyedService<IBgeGpuModelFactory>("expanded-ranking"),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IExpandedGpuRankBackend>(provider => provider.GetRequiredService<BgeExpandedGpuRankBackend>());
        services.AddSingleton<IExpandedPassageRanker>(provider => new ExpandedGpuRankCoordinator(provider.GetRequiredService<IExpandedGpuRankBackend>(),
            clock: provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<HybridPassageRetrievalEngine>(provider => new HybridPassageRetrievalEngine(
            provider.GetRequiredService<ICorpusRetrievalReader>(),
            provider.GetRequiredService<IHybridPassageCandidateReader>(),
            provider.GetRequiredService<ICorpusGenerationLeaseStore>(),
            provider.GetRequiredService<ICorpusAnnLeaseFactory>(),
            provider.GetRequiredService<IScheduledPassageInference>(),
            provider.GetRequiredService<IGpuInteractiveOwnerProbe>(),
            provider.GetRequiredService<ICorpusEvidenceCodec>(),
            provider.GetRequiredService<ILocalPrivateContentDisclosure>(),
            provider.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(25),
            provider.GetRequiredService<ICorpusAnnPreparationSignal>()));
        services.Replace(ServiceDescriptor.Scoped<IHybridPassageRetrieval>(provider => new ExpandedPassageRetrieval(
            provider.GetRequiredService<HybridPassageRetrievalEngine>(), provider.GetRequiredService<IExpandedPassageRanker>(),
            provider.GetRequiredService<ResidentCpuPassageInference>(), () => RequestArrival(provider),
            provider.GetRequiredService<IHostApplicationLifetime>().StopApplication)));
        services.Replace(ServiceDescriptor.Scoped<ISearchService, PassageSearchService>());
        CorpusAnnPreparationService.Add(services);
    }

    private static DateTimeOffset RequestArrival(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items[NativeV1.NativeRequestResourceGuard.RetrievalArrivalKey] is DateTimeOffset arrival
            ? arrival : provider.GetRequiredService<TimeProvider>().GetUtcNow();

    private static SqlGpuSchedulerStore NewSchedulerStore(IServiceProvider provider) =>
        new(provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), timeProvider: provider.GetRequiredService<TimeProvider>(),
            deploymentValidationHold: provider.GetService<IDeploymentValidationHold>(),
            interactiveExecutionTimeout: SearchExecutionTimeout, sourceAdmission: provider.GetRequiredService<ISourceWorkAdmission>(),interactiveQueueTimeout:TimeSpan.FromSeconds(4));

    internal static BgeGpuModelStores CreateModelStores() => new(
        Store(["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]),
        Store(["bundles", "bge-reranker-v2-m3-onnx", BgeOfflineModels.RerankerRevision, BgeOfflineModels.RerankerExport]),
        Store(["bundles", "bge-reranker-v2-m3", BgeOfflineModels.RerankerRevision]),
        Store(["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
    internal static BgeGpuModelStores CreateFp16ModelStores() => new(
        Store(["bundles", "bge-m3-onnx", BgeOfflineModels.EmbeddingRevision, "onnx"]),
        Store(["bundles", "bge-reranker-v2-m3-onnx-fp16", BgeOfflineModels.RerankerRevision, BgeOfflineModels.Fp16RerankerRecipe]),
        Store(["bundles", "bge-reranker-v2-m3", BgeOfflineModels.RerankerRevision]),
        Store(["runtimes", "bge-onnx-net-tokenizer-1.4.0-win-x64"]));
    private static PinnedLocalModelStore Store(string[] components) =>
        new(new LocalModelStore(() => WindowsModelVerificationFiles.OpenProductionBundle(components)));
}
