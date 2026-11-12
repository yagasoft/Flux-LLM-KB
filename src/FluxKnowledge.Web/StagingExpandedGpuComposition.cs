using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal static class StagingExpandedGpuComposition
{
    internal static void Add(IServiceCollection services, StagingHostSettings settings, StagingGpuSchedulerBinding? schedulerBinding = null,int maximumCohorts=1)
    {
        if (!settings.CpuSemantic || !settings.DashboardReadBenchmark || settings.IndexPublicationMode != "Segmented" || settings.SourceJournalEnabled)
            throw new ArgumentException("Expanded GPU trial requires isolated segmented CPU staging with autonomous writers disabled.");
        ArgumentNullException.ThrowIfNull(schedulerBinding);
        services.AddSingleton(_ => new StagingGpuTrialGate(settings,maximumCohorts));
        services.AddKeyedSingleton("expanded-gpu-trial",(_,_) => BgeSearchRuntimeComposition.CreateFp16ModelStores());
        services.AddSingleton(provider => new StagingSqlGpuRankBackend(
            schedulerBinding,
            new BgeGpuModelFactory(provider.GetRequiredKeyedService<BgeGpuModelStores>("expanded-gpu-trial"),BgeRerankerPrecision.Fp16Internal),
            provider.GetRequiredService<StagingGpuTrialGate>().Require,provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
            beforeCohortAdmission:provider.GetRequiredService<StagingGpuTrialGate>().ClaimCohort));
        services.AddSingleton<IExpandedPassageRanker>(provider => new ExpandedGpuRankCoordinator(provider.GetRequiredService<StagingSqlGpuRankBackend>()));
    }
}
