using System.Diagnostics;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal sealed partial class StagingRootedIndexFixture
{
    private Task? _background;
    private int _stopRequested;
    private bool _publicationReady,_published;
    private readonly StagingIndexNativeEvidence _native=new(gpuIndexing);

    internal async Task<object> PreparePublicationAsync(CancellationToken ct)
    {
        await EnterControlAsync(ct);
        try
        {
            if(_records.Length!=2 || _publicationReady)throw new InvalidOperationException("canonical-fixture-not-ready");
            await using var scope=scopes.CreateAsyncScope();var provider=scope.ServiceProvider;
            if(gpuIndexing ? provider.GetService<IEmbeddingGpuHandoff>() is null :
                provider.GetService<IEmbeddingGpuHandoff>() is not null || provider.GetRequiredService<IEmbeddingProvider>() is not Configuration.StagingCpuEmbeddingProvider)
                throw new InvalidOperationException("representative-staging-cpu-index-provider-required");
            var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
            var publication=_records.Single(r=>Path.GetFileName(r.Path)=="publication.txt");
            for(var batch=0;batch<32;batch++)
            {
                var dispatch=await NextAsync(factory,publication,ct);
                if(dispatch.Stage==PipelineStage.Publish)break;
                if(dispatch.Stage!=PipelineStage.Embed)throw new InvalidOperationException("publication-embedding-dispatch-required");
                await ExecuteIndexBatchAsync(provider,publication,dispatch,ct);
            }
            if((await NextAsync(factory,publication,ct)).Stage!=PipelineStage.Publish)throw new InvalidOperationException("publication-embedding-not-complete");
            _publicationReady=true;
            Journal("publication-ready",new {publicationRecord=publication.Id,stamp=_stamp});
            return new {phase="publication-ready",publicationRecord=publication.Id,actualCpuIndexing=!gpuIndexing,normalGpuIndexing=gpuIndexing};
        }
        finally{_control.Release();}
    }

    internal async Task<object> StartBacklogAsync(DateTimeOffset deadline,CancellationToken hostStopping)
    {
        await EnterControlAsync(hostStopping);
        try
        {
            if(!_publicationReady || _background is not null || deadline<=DateTimeOffset.UtcNow || deadline-DateTimeOffset.UtcNow>TimeSpan.FromMinutes(6))
                throw new InvalidOperationException("one-bounded-index-backlog-required");
            _background=Task.Run(()=>RunBacklogAsync(deadline,hostStopping),CancellationToken.None);
            return new {phase="indexing",deadline,route=gpuIndexing?"normal-gpu-handoff":"staging-cpu-batched-four",gpuIndexingQualified=false};
        }
        finally{_control.Release();}
    }

    private async Task RunBacklogAsync(DateTimeOffset deadline,CancellationToken ct)
    {
        var record=_records.Single(r=>Path.GetFileName(r.Path)=="backlog.txt");
        var checkpoints=0;
        try
        {
            while(Volatile.Read(ref _stopRequested)==0 && DateTimeOffset.UtcNow<deadline-TimeSpan.FromSeconds(30))
            {
                ct.ThrowIfCancellationRequested();
                await _control.WaitAsync(ct);
                try
                {
                    if(Volatile.Read(ref _stopRequested)!=0 || Volatile.Read(ref _closing)!=0)break;
                    await using var scope=scopes.CreateAsyncScope();var provider=scope.ServiceProvider;
                    var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
                    var dispatch=await NextAsync(factory,record,ct);
                    if(dispatch.Stage!=PipelineStage.Embed)throw new InvalidOperationException("index-backlog-exhausted-before-cohort-stop");
                    var before=await PersistedAsync(factory,dispatch.Job,ct);
                    var quantum=gpuIndexing?EmbeddingGpuRuntime.MaximumRequestChunks:4;
                    if(_backlogChunks<=0 || before<0 || _backlogChunks-before<=quantum)
                        throw new InvalidOperationException("index-backlog-remaining-budget-insufficient-before-dispatch");
                    await ExecuteIndexBatchAsync(provider,record,dispatch,ct);
                    var after=await PersistedAsync(factory,dispatch.Job,ct);
                    if(after!=before+quantum)throw new InvalidOperationException("exact-vector-checkpoint-progress-required");
                    await using var db=await factory.CreateDbContextAsync(ct);
                    var job=await db.Jobs.AsNoTracking().SingleAsync(j=>j.Id==dispatch.Job,ct);
                    var delivery=await db.OutboxMessages.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id,ct);
                    if(job.PublicState!=0 || job.Reason!=(gpuIndexing?null:"embedding-batch-persisted") || job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null ||
                        delivery.DispatchedAtUtc is not null || delivery.LeaseOwner is not null || delivery.LeaseExpiresAtUtc is not null ||
                        await db.OutboxMessages.AnyAsync(d=>d.PipelineRecordId==record.Id && d.Stage==(int)PipelineStage.Publish,ct))
                        throw new InvalidOperationException("index-checkpoint-not-cleared-or-backlog-exhausted");
                    checkpoints++;
                    Journal("backlog-checkpoint",new {recordId=record.Id,checkpoints,jobId=dispatch.Job,before,after,total=_backlogChunks,leasesClear=true,utc=DateTimeOffset.UtcNow});
                }
                finally{_control.Release();}
            }
            Journal("backlog-stopped",new {recordId=record.Id,checkpoints,actualTaskSettled=true,stoppedAtCheckpoint=true,utc=DateTimeOffset.UtcNow});
        }
        catch(Exception failure)
        {
            try{Journal("backlog-failed",new {recordId=record.Id,checkpoints,type=failure.GetType().Name,actualTaskSettled=true,utc=DateTimeOffset.UtcNow});}
            catch(Exception observation){failure.Data["staging.index.failureJournal"]=observation.GetType().Name;}
            throw;
        }
    }

    internal async Task<object> StopBacklogAsync()
    {
        Interlocked.Exchange(ref _stopRequested,1);
        if(_background is not null)await _background;
        var evidence=_native.Snapshot();
        if(_output is not null && Volatile.Read(ref _closing)==0)Journal("index-native-evidence",evidence);
        return new {phase="indexing-stopped",actualTaskSettled=true,native=evidence,gpuIndexingQualified=false};
    }

    internal async Task<object> PublishAsync(CancellationToken ct)
    {
        await EnterControlAsync(ct);
        try
        {
            if(!_publicationReady || _published || _background is null || _background.IsCompleted || Volatile.Read(ref _stopRequested)!=0)
                throw new InvalidOperationException("active-indexing-publication-required");
            await using var scope=scopes.CreateAsyncScope();var provider=scope.ServiceProvider;
            var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
            var before=_stamp??throw new InvalidOperationException("missing-fixture-stamp");
            Guid[] oldLeases;
            // This is checked after acquiring the checkpoint lock, immediately before mutation.
            using(var barrier=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                barrier.CancelAfter(TimeSpan.FromSeconds(5));
                using var self=Process.GetCurrentProcess();
                while(true)
                {
                    await using var db=await factory.CreateDbContextAsync(barrier.Token);
                    var leases=await db.CorpusQueryLeases.AsNoTracking().Where(q=>q.OwnerProcessId==self.Id && q.OwnerStartedAtUtc==self.StartTime.ToUniversalTime()).Take(3).ToArrayAsync(barrier.Token);
                    if(leases.Length>2 || leases.Any(q=>q.GenerationId!=before.Generation || q.CorpusEpoch!=before.Epoch || q.CorpusVersion!=before.Version))
                        throw new InvalidOperationException("foreign-or-changed-current-host-query-lease");
                    if(leases.Length==2)
                    {
                        oldLeases=leases.Select(q=>q.Id).ToArray();
                        Journal("publication-overlap-barrier",new {before,leases=leases.Select(q=>new {q.Id,q.GenerationId,q.CorpusEpoch,q.CorpusVersion,q.OwnerProcessId,q.OwnerStartedAtUtc}),utc=DateTimeOffset.UtcNow});
                        break;
                    }
                    await Task.Delay(10,barrier.Token);
                }
            }
            var publication=_records.Single(r=>Path.GetFileName(r.Path)=="publication.txt");
            var dispatch=await NextAsync(factory,publication,ct);
            if(dispatch.Stage!=PipelineStage.Publish)throw new InvalidOperationException("exact-publish-dispatch-required");
            await ExecuteAsync(provider,publication,dispatch,ct);
            var after=await StampAsync(factory,ct);
            Journal("publication-transition-observed",new {before,after,recordId=publication.Id,utc=DateTimeOffset.UtcNow,overlapProofPending=true});
            if(after.Epoch!=before.Epoch || after.Version!=before.Version+1 || after.Generation is null || after.Generation==before.Generation)
                throw new InvalidOperationException("exact-single-publication-transition-required");
            await using(var db=await factory.CreateDbContextAsync(ct))
            {
                var retained=await db.CorpusQueryLeases.AsNoTracking().Where(q=>oldLeases.Contains(q.Id)).ToArrayAsync(ct);
                if(retained.Length!=2 || retained.Any(q=>q.GenerationId!=before.Generation || q.CorpusEpoch!=before.Epoch || q.CorpusVersion!=before.Version))
                    throw new InvalidOperationException("old-query-leases-did-not-span-publication");
                Journal("publication-overlap-after-commit",new {before,after,oldLeases,utc=DateTimeOffset.UtcNow});
            }
            // No later checkpoint can observe the new fence until this durable transition receipt is flushed.
            Journal("publication-transition",new {before,after,recordId=publication.Id,utc=DateTimeOffset.UtcNow});
            _stamp=after;_published=true;
            return new {phase="published",before,after,publicationRecord=publication.Id};
        }
        finally{_control.Release();}
    }

    private async Task ExecuteIndexBatchAsync(IServiceProvider provider,FixtureRecord record,FixtureDispatch dispatch,CancellationToken ct)
    {
        var trace=ActivityTraceId.CreateRandom().ToString();var span=ActivitySpanId.CreateRandom().ToString();
        _native.Register(trace+"/"+span);
        await HybridSearchDiagnostics.WithRequestCorrelationAsync(trace,span,async()=>
        {
            var queuedAfter=DateTimeOffset.UtcNow;
            await ExecuteAsync(provider,record,dispatch,ct);
            if(gpuIndexing)await JoinGpuCheckpointAsync(provider,record,dispatch,queuedAfter,ct);
            return 0;
        });
    }

    // The fixture is only a producer. Admission, execution, native disposal and requeue belong to the normal hosted services.
    private async Task JoinGpuCheckpointAsync(IServiceProvider provider,FixtureRecord record,FixtureDispatch dispatch,DateTimeOffset queuedAfter,CancellationToken ct)
    {
        var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(90));
        while(true)
        {
            await using var db=await factory.CreateDbContextAsync(deadline.Token);
            var requests=await db.EmbeddingGpuRequests.AsNoTracking().Where(r=>r.ParentJobId==dispatch.Job && r.CreatedAtUtc>=queuedAfter).Take(2).ToArrayAsync(deadline.Token);
            if(requests.Length>1)throw new InvalidOperationException("multiple-fixture-gpu-handoffs");
            if(requests.Length==0)
            {
                if(await db.OutboxMessages.AnyAsync(d=>d.PipelineRecordId==record.Id && d.Stage==(int)PipelineStage.Publish && d.DispatchedAtUtc==null,deadline.Token))return;
                throw new InvalidOperationException("exact-fixture-gpu-handoff-missing");
            }
            var request=requests[0];
            var job=await db.Jobs.AsNoTracking().SingleAsync(j=>j.Id==dispatch.Job,deadline.Token);
            if(request.State==2 && request.NativeCleanupConfirmed && request.CleanupConfirmedAtUtc is not null && request.ResultDigest is not null &&
                job.PublicState==0 && job.LeaseOwner is null && job.LeaseExpiresAtUtc is null)
            {
                var delivery=await db.OutboxMessages.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id,deadline.Token);
                if(delivery.DispatchedAtUtc is not null || delivery.LeaseOwner is not null || delivery.LeaseExpiresAtUtc is not null)
                    throw new InvalidOperationException("fixture-gpu-requeue-incomplete");
                Journal("gpu-checkpoint-settled",new {recordId=record.Id,jobId=job.Id,miniTaskId=request.MiniTaskId,request.GenerationId,
                    request.NativeCleanupConfirmed,request.CleanupConfirmedAtUtc,normalSchedulerExecuted=true,utc=DateTimeOffset.UtcNow});
                return;
            }
            if(request.State==2 && request.ResultDigest is null)throw new InvalidOperationException("fixture-gpu-result-unconfirmed");
            await Task.Delay(25,deadline.Token);
        }
    }

    private static async Task<int> PersistedAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,Guid job,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var drafts=await db.IndexGenerations.AsNoTracking().Where(g=>g.EmbeddingJobId==job).Select(g=>g.Id).Take(2).ToArrayAsync(ct);
        if(drafts.Length>1)throw new InvalidOperationException("multiple-owned-embedding-drafts");
        return drafts.Length==0?0:await db.Vectors.CountAsync(v=>v.IndexGenerationId==drafts[0],ct);
    }
}
