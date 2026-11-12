using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

[Collection("sql-hosted-recovery")]
public sealed class StagingRootedFixturePreparationTests : IAsyncLifetime
{
    private readonly NativeSqlServerFixture _sql=new();
    public Task InitializeAsync()=>_sql.InitializeAsync();
    public Task DisposeAsync()=>_sql.DisposeAsync();

    [NativeSqlServerFact]
    public async Task Normal_gpu_fixture_queues_and_joins_checkpoints_through_the_hosted_scheduler()
    {
        var token=Guid.NewGuid().ToString("N");
        var root=@"I:\Codex Workspaces\Staging\FluxKnowledge\FixtureGpu-"+token;
        var output=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\indexing-fixture-unit-"+token;
        var factory=new Factory(_sql.ConnectionString);var models=new SyntheticGpuModels();
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"Config"));
            var file=Path.Combine(root,"Config","staging.json");
            File.WriteAllText(file,JsonSerializer.Serialize(new {SqlConnectionString="Server=localhost;Database=FluxKnowledge_Staging_FixtureGpu;User ID=stage;Password=synthetic",
                Origin="http://127.0.0.1:5147",CpuSemantic=true,DashboardReadBenchmark=true,IndexPublicationMode="Segmented",CpuRerankerThreads=1}));
            var settings=StagingHostSettings.Load(file);Directory.CreateDirectory(settings.SourcesRoot);
            var generation=Guid.NewGuid();
            await using(var db=factory.CreateDbContext())
            {
                db.IndexGenerations.Add(new IndexGenerationEntity {Id=generation,FormatVersion=2,Dimensions=1024,ModelFingerprint=BgeOfflineModels.EmbeddingFingerprint,
                    IndexPath="synthetic",MetadataChecksum=new string('b',64),CreatedAtUtc=DateTimeOffset.UtcNow});
                db.GpuCapacitySlots.Add(new(){SlotKey=FluxKnowledge.Application.Documents.PaddleOcrVlmRuntimeContract.CapacitySlotKey,State=0,UpdatedAtUtc=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
                var state=await db.IndexState.SingleAsync(s=>s.Id==1);state.RequiredIndexFormat=2;state.ActiveIndexGenerationId=generation;state.CorpusVersion=1;await db.SaveChangesAsync();
            }
            var services=new ServiceCollection();services.AddLogging();services.AddSingleton<IHostApplicationLifetime,FixtureLifetime>();
            WebHostComposition.AddStagingFluxKnowledgeServices(services,settings);StagingNormalGpuComposition.Add(services,settings);
            services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<FluxKnowledgeDbContext>>(factory));
            services.Replace(ServiceDescriptor.Singleton<IBgeGpuModelFactory>(models));
            // This fixture has synthetic ANN paths; index readiness is exercised separately with actual ANN files.
            services.Replace(ServiceDescriptor.Singleton<IDatabaseStartupReadiness>(new SyntheticIndexReady()));
            await using var provider=services.BuildServiceProvider();
            var hosted=provider.GetServices<IHostedService>().Where(s=>s is GpuSchedulerService or GpuExecutorDispatchRecoveryService).ToArray();
            foreach(var service in hosted)await service.StartAsync(default);
            try
            {
                await using var fixture=new StagingRootedIndexFixture(provider.GetRequiredService<IServiceScopeFactory>(),settings,()=>true,(generation,1),gpuIndexing:true);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await fixture.PrepareAsync(Guid.NewGuid(),output,deadline.Token);
                await fixture.PreparePublicationAsync(deadline.Token);
                Assert.True(models.Loads>0);Assert.Equal(models.Loads,models.Unloads);
                await using var db=factory.CreateDbContext();
                Assert.True(await db.EmbeddingGpuRequests.AnyAsync());
                Assert.False(await db.EmbeddingGpuRequests.AnyAsync(r=>r.State!=2 || !r.NativeCleanupConfirmed || r.ResultDigest==null));
                Assert.Equal(0,(await db.GpuCapacitySlots.SingleAsync()).State);
                Assert.True(await db.OutboxMessages.AnyAsync(d=>d.Stage==(int)FluxKnowledge.Domain.Pipeline.PipelineStage.Publish));
            }
            finally
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                foreach(var service in hosted.Reverse())await service.StopAsync(stop.Token);
            }
        }
        finally
        {
            foreach(var owned in new[]{root,output})
            {
                if(Path.GetFullPath(owned)!=owned || !owned.EndsWith(token,StringComparison.Ordinal))throw new InvalidOperationException("owned-gpu-fixture-test-cleanup");
                if(Directory.Exists(owned))Directory.Delete(owned,true);
            }
        }
    }

    private sealed class FixtureLifetime:IHostApplicationLifetime
    {public CancellationToken ApplicationStarted=>default;public CancellationToken ApplicationStopping=>default;public CancellationToken ApplicationStopped=>default;public void StopApplication() {}}
    private sealed class SyntheticIndexReady:IDatabaseStartupReadiness
    {public bool IsReady=>true;public ValueTask WaitAsync(CancellationToken cancellationToken)=>ValueTask.CompletedTask;}
    private sealed class SyntheticGpuModels:IBgeGpuModelFactory
    {
        public int Loads,Unloads;
        public ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext ownership)
        {
            var allocation=ownership.BeginNativeAllocation();Interlocked.Increment(ref Loads);
            return ValueTask.FromResult(new BgeGpuModelLease<IBatchedEmbeddingProvider>(new SyntheticEmbedding(),()=>{allocation.ConfirmReleased();Interlocked.Increment(ref Unloads);}));
        }
        public ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext ownership)=>throw new InvalidOperationException("indexing-must-not-rank");
    }
    private sealed class SyntheticEmbedding:IBatchedEmbeddingProvider
    {
        public int MaximumBatchSize=>4;public EmbeddingProfile Profile=>new(BgeOfflineModels.EmbeddingFingerprint,1024);
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text,CancellationToken ct)=>ValueTask.FromResult(Result());
        public ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();return ValueTask.FromResult<IReadOnlyList<EmbeddingResult>>(texts.Select(_=>Result()).ToArray());}
        private static EmbeddingResult Result(){var values=new float[1024];values[0]=1;return new(values,BgeOfflineModels.EmbeddingFingerprint);}
    }

    [NativeSqlServerFact]
    public async Task Closed_benchmark_host_can_prepare_only_its_new_root_through_the_native_pipeline()
    {
        var token=Guid.NewGuid().ToString("N");
        var root=@"I:\Codex Workspaces\Staging\FluxKnowledge\FixturePrepare-"+token;
        var output=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\indexing-fixture-unit-"+token;
        var factory=new Factory(_sql.ConnectionString);
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"Config"));
            var file=Path.Combine(root,"Config","staging.json");
            File.WriteAllText(file,JsonSerializer.Serialize(new
            {SqlConnectionString="Server=localhost;Database=FluxKnowledge_Staging_FixturePrepare;User ID=stage;Password=synthetic",
                Origin="http://127.0.0.1:5147",CpuSemantic=true,DashboardReadBenchmark=true,IndexPublicationMode="Segmented",CpuRerankerThreads=1}));
            var settings=StagingHostSettings.Load(file);
            Directory.CreateDirectory(settings.SourcesRoot);
            var foreign=Guid.NewGuid();
            await using(var db=factory.CreateDbContext())
            {
                var generation=Guid.Parse("760238d5-0f4b-39de-81cc-4b30b3706a79");
                db.IndexGenerations.Add(new IndexGenerationEntity
                {Id=generation,FormatVersion=2,Dimensions=1024,ModelFingerprint=new string('a',64),IndexPath="synthetic",MetadataChecksum=new string('b',64),CreatedAtUtc=DateTimeOffset.UtcNow});
                await db.SaveChangesAsync();
                var state=await db.IndexState.SingleAsync(s=>s.Id==1);
                state.RequiredIndexFormat=2;state.ActiveIndexGenerationId=generation;state.CorpusVersion=14311;
                db.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {Id=foreign,CanonicalPath=Path.Combine(root,"foreign-paused"),DisplayName="Untouched paused root",State=1,
                    MaximumFileBytes=1024,ReconciliationCadenceSeconds=604800,ConfigurationRevision=1,CreatedAtUtc=DateTimeOffset.UtcNow,UpdatedAtUtc=DateTimeOffset.UtcNow});
                await db.SaveChangesAsync();
            }
            var services=new ServiceCollection();services.AddLogging();
            WebHostComposition.AddStagingFluxKnowledgeServices(services,settings);
            services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<FluxKnowledgeDbContext>>(factory));
            await using var provider=services.BuildServiceProvider();
            Assert.Empty(provider.GetRequiredService<ISourceWorkAdmission>().ReadAdmittedRoots());
            await using var fixture=new StagingRootedIndexFixture(provider.GetRequiredService<IServiceScopeFactory>(),settings,()=>true);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var prepared=JsonSerializer.SerializeToElement(await fixture.PrepareAsync(Guid.NewGuid(),output,deadline.Token));
            Assert.Equal("canonical-ready",prepared.GetProperty("phase").GetString());
            Assert.False(prepared.GetProperty("actualEmbeddingExecuted").GetBoolean());
            var owned=prepared.GetProperty("rootId").GetGuid();
            Assert.Equal(owned,Assert.Single(provider.GetRequiredService<ISourceWorkAdmission>().ReadAdmittedRoots()).RootId);
            await using var verify=factory.CreateDbContext();
            Assert.Equal(2,await verify.PipelineRecords.CountAsync());
            Assert.Equal(1,(await verify.SourceRootConfigurations.SingleAsync(r=>r.Id==foreign)).State);
            Assert.Equal(2,Directory.GetFiles(output,"*-canonical.json").Length);
            using var admitted=JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(output,"*-source-admission.json"))));
            Assert.Equal(0,admitted.RootElement.GetProperty("recordsBeforeAdmission").GetInt32());
            Assert.Equal(2,admitted.RootElement.GetProperty("unlinkedActivities").GetInt32());
            Assert.Equal(0,admitted.RootElement.GetProperty("foreignRootsAdmitted").GetInt32());
        }
        finally
        {
            // All services and native stage calls have settled before disposable SQL teardown.
            // These generated directories are unique to this test; never clean another run.
            foreach(var owned in new[]{root,output})
            {
                if(Path.GetFullPath(owned)!=owned || !owned.EndsWith(token,StringComparison.Ordinal))
                    throw new InvalidOperationException("owned-preparation-test-cleanup-target");
                if(Directory.Exists(owned))Directory.Delete(owned,true);
            }
        }
    }

    private sealed class Factory(string connection):IDbContextFactory<FluxKnowledgeDbContext>
    {public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);}
}
