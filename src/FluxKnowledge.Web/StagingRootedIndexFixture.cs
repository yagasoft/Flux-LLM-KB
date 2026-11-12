using System.Data;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

/// <summary>Explicit private fixture; never registered by production composition.</summary>
internal sealed partial class StagingRootedIndexFixture(IServiceScopeFactory scopes,StagingHostSettings settings,Func<bool> exclusiveWriterWitness,
    (Guid Generation,long Version)? expectedBaseline=null,bool gpuIndexing=false) : IAsyncDisposable
{
    private readonly SemaphoreSlim _control=new(1,1);
    private readonly object _disposeSync=new();
    private Task? _disposeTask;
    private int _closing;
    private StagingIndexFixtureScope? _baseline;
    private Guid? _root;
    private string? _output;
    private Guid _operation;
    private DateTimeOffset _created;
    private FixtureStamp? _stamp;
    private FixtureRecord[] _records=[];
    private int _backlogChunks;
    private int _sequence,_prepareAttempted;

    internal void CaptureRuntimeStop(object evidence)
    {
        if(_output is not null)Journal("runtime-stopping",evidence);
    }

    internal async Task<object> PrepareAsync(Guid operation,string output,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing)!=0,this);
        if(operation==Guid.Empty || Interlocked.CompareExchange(ref _prepareAttempted,1,0)!=0)
            throw new InvalidOperationException("one-index-fixture-attempt-required");
        if(!exclusiveWriterWitness() || !settings.CpuSemantic || !settings.DashboardReadBenchmark || settings.SourceJournalEnabled || settings.IndexPublicationMode!="Segmented")
            throw new InvalidOperationException("isolated-cpu-index-fixture-required");
        var fullOutput=Path.GetFullPath(output);
        const string evidence=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\indexing-fixture-";
        if(!fullOutput.StartsWith(evidence,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("new-owned-index-fixture-output-required");
        NoLinks(fullOutput);
        await EnterControlAsync(ct);
        try
        {
            if(Directory.Exists(fullOutput))throw new InvalidOperationException("new-owned-index-fixture-output-required");
            _output=fullOutput;Directory.CreateDirectory(_output);_operation=operation;_created=DateTimeOffset.UtcNow;
            await using var scope=scopes.CreateAsyncScope();var provider=scope.ServiceProvider;
            var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
            var snapshot=await SnapshotAsync(factory,ct);
            _baseline=new(snapshot.Roots,snapshot.Deletions);
            _stamp=await StampAsync(factory,ct);
            var baseline = expectedBaseline ?? (Guid.Parse("760238d5-0f4b-39de-81cc-4b30b3706a79"), 14311L);
            if(_stamp.Generation!=baseline.Item1 || _stamp.Version!=baseline.Item2)
                throw new InvalidOperationException("frozen-published-fixture-baseline-required");
            var directory=Path.Combine(settings.SourcesRoot,"controlled-index-"+operation.ToString("N"));
            NoLinks(directory);if(Directory.Exists(directory))throw new InvalidOperationException("owned-source-already-exists");
            // Persist the unique path before intake so an interrupted commit can be recovered by that exact path.
            using var self=Process.GetCurrentProcess();
            Journal("before-intake",new {operation,createdUtc=_created,directory,stamp=_stamp,baseline=snapshot,hostProcessId=self.Id,hostStartedUtc=self.StartTime.ToUniversalTime()});
            Directory.CreateDirectory(directory);
            await using(var marker=new FileStream(Path.Combine(directory,".staging-fixture-owner.json"),FileMode.CreateNew))
            {await marker.WriteAsync(OwnerMarker(),ct);marker.Flush(true);}
            foreach(var item in new[]{("backlog.txt",2048),("publication.txt",32)})
            {
                var text=Source(operation,item.Item2);var bytes=Encoding.UTF8.GetBytes(text);
                if(bytes.Length>4*1024*1024)throw new InvalidOperationException("synthetic-source-byte-bound");
                await using var file=new FileStream(Path.Combine(directory,item.Item1),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                await file.WriteAsync(bytes,ct);file.Flush(true);
            }
            var request=NativeSourceRootCreation.Parse(JsonSerializer.SerializeToElement(new
            {path=directory,displayName="Controlled search indexing fixture",recursive=false,followLinks=false,maximumFileBytes=4*1024*1024,
                reconciliationSeconds=604800,indexSourceText=true,includePatterns=new[]{"backlog.txt","publication.txt"}}),"staging-index-fixture");
            var receipt=await provider.GetRequiredService<SourceRootService>().CreateAsync(request,ScanStartIntent.SaveAndScan,ct);
            _root=receipt.SourceRootId.Value;
            Journal("root-created",new {operation,rootId=_root,receipt});
            await ValidateClosedAsync(factory,ct);
            var scans=provider.GetRequiredService<ISourceScanControlStore>();
            var claim=await scans.ClaimNextReleasedAsync("staging-index-fixture:"+operation.ToString("N"),DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5),ct)
                ?? throw new InvalidOperationException("owned-scan-not-claimable");
            if(claim.SourceRoot.Id.Value!=_root || claim.ScanRequest.Id.Value!=receipt.SourceScanRequestId.Value)
                throw new InvalidOperationException("closed-fixture-scan-identity-violation");
            var result=await provider.GetRequiredService<ISourceScanner>().ScanAsync(claim.SourceRoot,claim.ScanRequest,ct);
            await scans.CompleteAsync(claim,result,null,ct);
            if(result.DiscoveredCount!=2 || !result.IsCompleteInventory)throw new InvalidOperationException("exact-two-file-intake-required");
            Journal("scan-settled",new {rootId=_root,scan=result});
            await AdmitScannedSourcesAsync(provider,directory,ct);
            await using(var db=await factory.CreateDbContextAsync(ct))
            {
                _records=await (from record in db.PipelineRecords.AsNoTracking()
                    join revision in db.SourceRevisions.AsNoTracking() on record.SourceRevisionId equals revision.Id
                    where revision.SourceRootId==_root && record.RegisteredAtUtc>=_created
                    orderby record.Id
                    select new FixtureRecord(record.Id,revision.Id,revision.CanonicalPath,record.ContentHash,record.Revision)).Take(3).ToArrayAsync(ct);
            }
            if(_records.Length!=2 || _records.Any(r=>r.Revision!=1 || !r.Path.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) ||
                !_records.Select(r=>Path.GetFileName(r.Path)).Order().SequenceEqual(new[]{"backlog.txt","publication.txt"}))
                throw new InvalidOperationException("rooted-record-provenance-not-proved");
            Journal("records-created",new {rootId=_root,records=_records,scan=result});
            foreach(var record in _records)
            {
                for(var step=0;step<4;step++)
                {
                    var dispatch=await NextAsync(factory,record,ct);
                    if(dispatch.Stage==PipelineStage.Embed)break;
                    if(dispatch.Stage is not (PipelineStage.Extract or PipelineStage.Normalise or PipelineStage.CanonicalIndex))
                        throw new InvalidOperationException("unexpected-fixture-preparation-stage");
                    await ExecuteAsync(provider,record,dispatch,ct);
                }
                if((await NextAsync(factory,record,ct)).Stage!=PipelineStage.Embed)throw new InvalidOperationException("fixture-embed-successor-missing");
                var chunks=await provider.GetRequiredService<IIndexGenerationStore>().ReadChunksAsync(new(record.Id),record.Revision,ct);
                var backlog=Path.GetFileName(record.Path)=="backlog.txt";
                if(chunks.Count<(backlog?512:10) || chunks.Count>(backlog?4096:128))throw new InvalidOperationException("fixture-chunk-budget");
                if(backlog)_backlogChunks=chunks.Count;
                Journal("canonical",new {recordId=record.Id,backlog,chunks=chunks.Count});
            }
            Journal("canonical-ready",new {rootId=_root,records=_records,stamp=_stamp});
            return new {rootId=_root,records=_records,stamp=_stamp,phase="canonical-ready",actualEmbeddingExecuted=false};
        }
        finally{_control.Release();}
    }

    private async Task ValidateClosedAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,CancellationToken ct)
    {
        var current=await SnapshotAsync(factory,ct);
        (_baseline??throw new InvalidOperationException("missing-fixture-baseline")).Validate(current.Roots,current.Deletions,_root,null,exclusiveWriterWitness());
    }
    private static async Task<FixtureSnapshot> SnapshotAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var roots=await db.SourceRootConfigurations.AsNoTracking().OrderBy(r=>r.Id).Take(65).ToArrayAsync(ct);
        var operations=await db.SourceDeletionOperations.AsNoTracking().OrderBy(r=>r.Id).Take(65).ToArrayAsync(ct);
        return new(roots.Select(r=>new StagingFixtureRoot(r.Id,r.State,Convert.ToHexString(r.RowVersion))).ToArray(),
            operations.Select(r=>new StagingFixtureDeletion(r.Id,r.SourceRootId,r.State,r.LeaseExpiresAtUtc,Convert.ToHexString(r.RowVersion))).ToArray());
    }
    private static async Task<FixtureStamp> StampAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var state=await db.IndexState.AsNoTracking().SingleAsync(s=>s.Id==1,ct);
        if(state.RequiredIndexFormat!=2 || state.CorpusRebuildOperationId is not null)throw new InvalidOperationException("segmented-stamp-required");
        return new(state.ActiveIndexGenerationId,state.CorpusEpoch,state.CorpusVersion);
    }
    private async Task<FixtureDispatch> NextAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,FixtureRecord record,CancellationToken ct)
    {
        await ValidateClosedAsync(factory,ct);
        if(await StampAsync(factory,ct)!=_stamp)throw new InvalidOperationException("unobserved-fixture-stamp-transition");
        await using var db=await factory.CreateDbContextAsync(ct);
        var rows=await db.OutboxMessages.AsNoTracking().Where(d=>d.PipelineRecordId==record.Id && d.SourceRevision==record.Revision && d.DispatchedAtUtc==null)
            .OrderBy(d=>d.DispatchGeneration).Take(2).ToArrayAsync(ct);
        if(rows.Length!=1 || rows[0].JobId is null || rows[0].CreatedAtUtc<_created)throw new InvalidOperationException("exact-fixture-dispatch-required");
        var row=rows[0];return new(row.Id,row.JobId??throw new InvalidOperationException("fixture-job-missing"),(PipelineStage)row.Stage,row.Operation,row.DispatchGeneration,row.IdempotencyKey);
    }
    private async Task ExecuteAsync(IServiceProvider provider,FixtureRecord record,FixtureDispatch expected,CancellationToken ct)
    {
        var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
        await ValidateClosedAsync(factory,ct);
        var stamp=_stamp??throw new InvalidOperationException("missing-stamp");
        var owner="staging-rooted-index:"+_operation.ToString("N");var now=DateTimeOffset.UtcNow;var duration=TimeSpan.FromMinutes(2);
        await using var db=await factory.CreateDbContextAsync(ct);
        await using var connection=new SqlConnection(db.Database.GetConnectionString());await connection.OpenAsync(ct);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        await using var command=new SqlCommand(ClaimSql,connection,transaction){CommandTimeout=5};
        void P(string name,SqlDbType type,object? value,int size=0){var p=command.Parameters.Add(name,type);if(size!=0)p.Size=size;p.Value=value??DBNull.Value;}
        P("@generation",SqlDbType.UniqueIdentifier,stamp.Generation);P("@epoch",SqlDbType.UniqueIdentifier,stamp.Epoch);P("@version",SqlDbType.BigInt,stamp.Version);
        P("@root",SqlDbType.UniqueIdentifier,_root);P("@sourceRevision",SqlDbType.UniqueIdentifier,record.SourceRevision);
        P("@dispatch",SqlDbType.UniqueIdentifier,expected.Id);P("@job",SqlDbType.UniqueIdentifier,expected.Job);
        P("@record",SqlDbType.UniqueIdentifier,record.Id);P("@revision",SqlDbType.BigInt,record.Revision);P("@created",SqlDbType.DateTimeOffset,_created);
        P("@stage",SqlDbType.Int,(int)expected.Stage);P("@operation",SqlDbType.NVarChar,expected.Operation,128);
        P("@dispatchGeneration",SqlDbType.BigInt,expected.Generation);P("@key",SqlDbType.NVarChar,expected.Key,512);
        P("@hash",SqlDbType.NVarChar,record.Hash,64);P("@path",SqlDbType.NVarChar,record.Path,2048);
        P("@now",SqlDbType.DateTimeOffset,now);P("@expiry",SqlDbType.DateTimeOffset,now.Add(duration));P("@owner",SqlDbType.NVarChar,owner,256);
        ClaimedDispatchMessage dispatch;
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))throw new InvalidOperationException("exact-rooted-claim-refused");
            dispatch=new(new DispatchMessageId(expected.Id),new PipelineRecordId(record.Id),record.Revision,expected.Stage,expected.Operation,
                expected.Generation,expected.Key,reader.GetFieldValue<DateTimeOffset>(0),owner,now.Add(duration),reader.GetInt64(1));
            if(await reader.ReadAsync(ct))throw new InvalidOperationException("multiple-rooted-claims");
        }
        await transaction.CommitAsync(ct);
        var job=await provider.GetRequiredService<IJobClaimStore>().ClaimForDispatchAsync(dispatch,owner,now,duration,ct)
            ??throw new InvalidOperationException("native-exact-job-claim-refused");
        if(job.JobId.Value!=expected.Job)throw new InvalidOperationException("rooted-job-identity-changed");
        var work=new StageWorkItem(dispatch,job);
        var transitions=provider.GetRequiredService<StageTransitionService>();
        await transitions.ValidateRepositorySourceAsync(work,ct);
        var worker=provider.GetServices<IStageWorker>().Single(w=>w.Operation==expected.Operation);
        await worker.ExecuteAsync(work,ct);
        if(DateTimeOffset.UtcNow>=now.Add(duration))throw new InvalidOperationException("rooted-worker-settled-after-lease");
        Journal("stage-settled",new {recordId=record.Id,dispatch=expected,completedUtc=DateTimeOffset.UtcNow});
    }
    private void Journal(string phase,object value)
    {
        var path=Path.Combine(_output??throw new InvalidOperationException("missing-fixture-output"),$"{Interlocked.Increment(ref _sequence):D4}-{phase}.json");
        var partial=path+".partial";
        using(var file=new FileStream(partial,FileMode.CreateNew)){JsonSerializer.Serialize(file,value);file.Flush(true);}
        File.Move(partial,path,false);
    }
    private static void NoLinks(string path)
    {
        for(var part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("linked-fixture-path");
    }
    private static string Source(Guid operation,int paragraphs)=>string.Join("\n\n",Enumerable.Range(1,paragraphs).Select(i=>
        $"Controlled indexing specimen {operation:N}, section {i:D4}. The violet lighthouse register records the source, its location and the reviewer. " +
        "The librarian checks each specimen against the retained original and writes the observed outcome. A complete entry identifies the expected result, the approval sequence and the evidence needed to check it. " +
        "Publication follows the completed review. Readers keep the exact source identity and passage boundaries while the new catalogue entry is prepared. " +
        $"This distinct section {i:D4} describes a synthetic specimen used solely for staging qualification and has no customer content."));

    public ValueTask DisposeAsync()
    {
        lock(_disposeSync)return new(_disposeTask??=DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _closing,1);Interlocked.Exchange(ref _stopRequested,1);
        // Drain foreground admission before taking the task snapshot: an admitted
        // StartBacklogAsync may still be publishing its owned task.
        await _control.WaitAsync();
        Task? background;
        try{background=_background;}finally{_control.Release();}
        Exception? failure=null;
        if(background is not null)try{await background;}catch(Exception error){failure=error;}
        // Foreground intake/publication also owns the listener and semaphore until it settles.
        await _control.WaitAsync();
        try{_native.Dispose();}finally{_control.Release();}
        // No OS wait handle was created. Keep the closed managed gate valid for
        // already queued callers until they observe closure, then let GC reclaim it.
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private async Task EnterControlAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing)!=0,this);
        await _control.WaitAsync(ct);
        if(Volatile.Read(ref _closing)!=0){_control.Release();throw new ObjectDisposedException(nameof(StagingRootedIndexFixture));}
    }
    private byte[] OwnerMarker()=>JsonSerializer.SerializeToUtf8Bytes(new {schema=1,operation=_operation});

    private sealed record FixtureRecord(Guid Id,Guid SourceRevision,string Path,string Hash,long Revision);
    private sealed record FixtureDispatch(Guid Id,Guid Job,PipelineStage Stage,string Operation,long Generation,string Key);
    private sealed record FixtureStamp(Guid? Generation,Guid Epoch,long Version);
    private sealed record FixtureSnapshot(StagingFixtureRoot[] Roots,StagingFixtureDeletion[] Deletions);

    private const string ClaimSql="""
        SET LOCK_TIMEOUT 1000;
        IF NOT EXISTS(SELECT 1 FROM IndexState WITH(UPDLOCK,HOLDLOCK)
          WHERE Id=1 AND ActiveIndexGenerationId=@generation AND CorpusEpoch=@epoch AND CorpusVersion=@version
          AND RequiredIndexFormat=2 AND CorpusRebuildOperationId IS NULL) THROW 51000,'Fixture stamp changed',1;
        ;WITH candidate AS (
          SELECT d.* FROM OutboxMessages d WITH(UPDLOCK,HOLDLOCK)
          WHERE d.Id=@dispatch AND d.JobId=@job AND d.PipelineRecordId=@record AND d.SourceRevision=@revision
          AND d.Stage=@stage AND d.Operation COLLATE Latin1_General_100_BIN2=@operation
          AND d.DispatchGeneration=@dispatchGeneration AND d.IdempotencyKey COLLATE Latin1_General_100_BIN2=@key
          AND d.CreatedAtUtc>=@created AND d.DispatchedAtUtc IS NULL AND d.DueAtUtc<=@now
          AND (d.LeaseExpiresAtUtc IS NULL OR d.LeaseExpiresAtUtc<=@now)
          AND NOT EXISTS(SELECT 1 FROM CorpusRebuildSupersededJobs WHERE JobId=@job)
          AND EXISTS(SELECT 1 FROM Jobs j WITH(UPDLOCK,HOLDLOCK)
            INNER JOIN PipelineRecords r ON r.Id=j.PipelineRecordId
            INNER JOIN SourceRevisions v ON v.Id=r.SourceRevisionId
            INNER JOIN SourceRootConfigurations root ON root.Id=v.SourceRootId
            WHERE j.Id=@job AND j.PipelineRecordId=@record AND j.SourceRevision=@revision AND j.Stage=@stage
            AND j.Operation COLLATE Latin1_General_100_BIN2=@operation AND j.DueAtUtc<=@now
            AND (j.Reason IS NULL OR j.Reason NOT IN('repository-source-deferred','repository-source-blocked'))
            AND ((j.PublicState=0 AND (j.LeaseExpiresAtUtc IS NULL OR j.LeaseExpiresAtUtc<=@now))
              OR (j.PublicState=1 AND j.LeaseExpiresAtUtc<=@now))
            AND r.Revision=@revision AND r.RegisteredAtUtc>=@created AND r.IsDeleted=0 AND r.CompletionCriteriaMet=0
            AND r.ContentHash COLLATE Latin1_General_100_BIN2=@hash AND r.CurrentStage=@stage
            AND r.SourceRevisionId=@sourceRevision AND v.SourceRootId=@root AND root.State=0
            AND v.CanonicalPath COLLATE Latin1_General_100_BIN2=@path AND v.SuppressedAtUtc IS NULL))
        UPDATE candidate SET LeaseOwner=@owner,LeaseExpiresAtUtc=@expiry,LeaseGeneration=LeaseGeneration+1
        OUTPUT inserted.DueAtUtc,inserted.LeaseGeneration;
        """;
}
