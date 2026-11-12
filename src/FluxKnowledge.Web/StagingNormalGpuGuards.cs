using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FluxKnowledge.Web;

internal static class StagingNormalGpuGuards
{
    internal static void Add(IServiceCollection services, Configuration.StagingHostSettings settings, StagingNormalRuntimeBinding binding)
    {
        services.AddSingleton(_ => new StagingGpuTrialGate(settings, 21, binding.ReceiptPath));
        services.Replace(ServiceDescriptor.Singleton<IBgeGpuModelFactory>(provider => new GuardedModels(
            new BgeGpuModelFactory(provider.GetRequiredService<BgeGpuModelStores>()), provider.GetRequiredService<StagingGpuTrialGate>())));
        foreach (var descriptor in services.Where(s => s.ServiceType == typeof(IBgeGpuModelFactory) && s.IsKeyedService && Equals(s.ServiceKey, "expanded-ranking")).ToArray())
            services.Remove(descriptor);
        services.AddKeyedSingleton<IBgeGpuModelFactory>("expanded-ranking", (provider, _) => new GuardedModels(
            new BgeGpuModelFactory(provider.GetRequiredKeyedService<BgeGpuModelStores>("expanded-ranking"), BgeRerankerPrecision.Fp16Internal),
            provider.GetRequiredService<StagingGpuTrialGate>()));
        services.Replace(ServiceDescriptor.Singleton<IExpandedGpuRankBackend>(provider =>
        {
            var gate=provider.GetRequiredService<StagingGpuTrialGate>();
            return new GuardedBackend(provider.GetRequiredService<BgeExpandedGpuRankBackend>(),()=>{gate.Require();gate.ClaimCohort();});
        }));
        services.AddSingleton<StagingSearchPhaseCapture>();
    }

    private sealed class GuardedModels(IBgeGpuModelFactory inner, StagingGpuTrialGate gate) : IBgeGpuModelFactory
    {
        public BgeRerankerPrecision Precision => inner.Precision;
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext ownership)
        { gate.Require(); return inner.OpenEmbeddingAsync(ownership); }
        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext ownership)
        { gate.Require(); return inner.OpenRerankerAsync(ownership); }
    }
    internal sealed class GuardedBackend(IExpandedGpuRankBackend inner, Action admissionWitness) : IExpandedGpuRankBackend
    {
        public string RerankerFingerprint => inner.RerankerFingerprint;
        public Task<ExpandedGpuRankOutcome> RunAsync(Guid cohortId, Func<IPassageReranker, CancellationToken, ValueTask<IReadOnlyDictionary<Guid, RerankResult>>> score, CancellationToken cancellationToken)
        {
            try { admissionWitness(); }
            catch(PassageRetrievalRefusalException refusal)
            { return Task.FromResult(new ExpandedGpuRankOutcome(new Dictionary<Guid,RerankResult>(),ExpandedGpuRankSettlement.Settled,refusal)); }
            return inner.RunAsync(cohortId, score, cancellationToken);
        }
    }
}
