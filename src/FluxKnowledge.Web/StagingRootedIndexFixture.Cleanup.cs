using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal sealed partial class StagingRootedIndexFixture
{
    private bool _recovered;
    internal void RequireRecoveredLegacyScope(LegacyStagingRetirementScope scope)
    {
        if(!_recovered || _root!=scope.SourceRootId || _operation!=scope.FixtureOperationId || _created!=scope.FixtureCreatedUtc)
            throw new InvalidOperationException("legacy-proof-exited-fixture-binding-required");
    }

    internal async Task<object> CleanupAsync(CancellationToken ct,IIndexSourceDeletionMaintenance? indexMaintenance=null)
    {
        if(!_recovered)throw new InvalidOperationException("cleanup-requires-prior-exited-host-proof");
        try{await StopBacklogAsync();}
        catch(Exception settledFailure){Journal("joined-backlog-failure-before-cleanup",new {type=settledFailure.GetType().Name,actualTaskSettled=true});}
        await EnterControlAsync(ct);
        try
        {
            if(_baseline is null || !exclusiveWriterWitness())throw new InvalidOperationException("owned-root-cleanup-witness-required");
            await using var scope=scopes.CreateAsyncScope();var provider=scope.ServiceProvider;
            var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
            var before=await SnapshotAsync(factory,ct);
            if(_root is null)
            {
                _baseline.Validate(before.Roots,before.Deletions,null,null,exclusiveWriterWitness());
                DeleteExactSyntheticInputs();
                Journal("interrupted-intake-cleaned",new {noRootCommitted=true,foreignBaselineUnchanged=true});
                return new {phase="cleaned",noRootCommitted=true,syntheticInputsRemoved=true};
            }
            var existing=before.Deletions.Where(d=>d.RootId==_root).ToArray();
            if(existing.Length>1)throw new InvalidOperationException("duplicate-owned-deletion-operations");
            if(existing.Length==0)
            {
                _baseline.Validate(before.Roots,before.Deletions,_root,null,exclusiveWriterWitness());
                var command=new NativeCorpusMutation("root_delete",JsonSerializer.SerializeToElement(new {rootId=_root.Value}));
                var native=provider.GetRequiredService<FluxKnowledge.Application.IntegrationV1.Corpus.NativeCorpusCommandService>();
                var preview=await native.PreviewAsync(command,"staging-index-fixture",ct);
                var receipt=await native.CommitAsync(command,preview.ConfirmationId,"staging-index-fixture-cleanup:"+_operation.ToString("N"),"staging-index-fixture",ct);
                Journal("root-delete-requested",new {rootId=_root,receipt});
            }
            var snapshot=await SnapshotAsync(factory,ct);
            var deletion=snapshot.Deletions.Single(d=>d.RootId==_root);
            var guarded=new OwnedDeletionStore(provider.GetRequiredService<ISourceDeletionStore>(),factory,_baseline,_root.Value,deletion.Id,exclusiveWriterWitness);
            var coordinator=indexMaintenance is null?ActivatorUtilities.CreateInstance<SourceDeletionCoordinator>(provider,guarded):
                ActivatorUtilities.CreateInstance<SourceDeletionCoordinator>(provider,guarded,indexMaintenance);
            for(var unit=0;unit<256;unit++)
            {
                ct.ThrowIfCancellationRequested();
                snapshot=await SnapshotAsync(factory,ct);
                _baseline.Validate(snapshot.Roots,snapshot.Deletions,_root,deletion.Id,exclusiveWriterWitness());
                var current=snapshot.Deletions.Single(d=>d.Id==deletion.Id);
                if(current.State==3)
                {
                    await using var db=await factory.CreateDbContextAsync(ct);
                    if(await db.PipelineRecords.AnyAsync(r=>r.SourceRevisionId!=null && db.SourceRevisions.Any(v=>v.Id==r.SourceRevisionId && v.SourceRootId==_root),ct) ||
                        await db.SourceRevisions.AnyAsync(v=>v.SourceRootId==_root,ct))
                        throw new InvalidOperationException("native-root-cleanup-has-retained-owned-records");
                    Journal("native-cleanup-completed",new {rootId=_root,deletionOperation=deletion.Id,unit,foreignBaselineUnchanged=true});
                    DeleteExactSyntheticInputs();
                    return new {rootId=_root,phase="cleaned",deletionOperation=deletion.Id,foreignWorkUnchanged=true,syntheticInputsRemoved=true};
                }
                var progressed=await coordinator.RunOneAsync(ct);
                Journal("native-cleanup-unit",new {rootId=_root,deletionOperation=deletion.Id,unit,progressed});
                if(!progressed)
                {
                    await using var db=await factory.CreateDbContextAsync(ct);
                    var status=await db.SourceDeletionOperations.AsNoTracking().SingleAsync(d=>d.Id==deletion.Id,ct);
                    if(status.State!=3)throw new InvalidOperationException("native-root-cleanup-blocked:"+status.Phase+":"+status.ReasonCode);
                }
            }
            throw new InvalidOperationException("native-root-cleanup-unit-bound");
        }
        finally{_control.Release();}
    }

    // Recovery is used only after the external controller has proved the old exact host/job exited.
    // The caller supplies a fresh closed-writer witness; no model or worker is started by recovery.
    internal async Task RecoverAsync(string output,string priorWindow,CancellationToken ct)
    {
        _output=Path.GetFullPath(output);NoLinks(_output);
        const string evidence=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\indexing-fixture-";
        if(!_output.StartsWith(evidence,StringComparison.OrdinalIgnoreCase) || !exclusiveWriterWitness())throw new InvalidOperationException("owned-fixture-recovery-output-required");
        var files=Directory.EnumerateFiles(_output,"*.json",SearchOption.TopDirectoryOnly).Take(4097).ToArray();
        if(files.Length>4096)throw new InvalidOperationException("fixture-journal-bound");
        var before=files.Single(f=>Path.GetFileName(f).EndsWith("-before-intake.json",StringComparison.Ordinal));
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(before,ct));var value=document.RootElement;
        StagingIndexFixtureExitProof.Verify(priorWindow,value);
        _operation=value.GetProperty("operation").GetGuid();_created=value.GetProperty("createdUtc").GetDateTimeOffset();
        var baseline=value.GetProperty("baseline").Deserialize<FixtureSnapshot>()??throw new InvalidOperationException("missing-recovery-baseline");
        _baseline=new(baseline.Roots,baseline.Deletions);
        var expectedDirectory=Path.Combine(settings.SourcesRoot,"controlled-index-"+_operation.ToString("N"));
        if(value.GetProperty("directory").GetString()!=expectedDirectory)throw new InvalidOperationException("fixture-recovery-path-mismatch");
        await using var scope=scopes.CreateAsyncScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
        await using var db=await factory.CreateDbContextAsync(ct);
        var roots=await db.SourceRootConfigurations.AsNoTracking().Where(r=>r.CanonicalPath==expectedDirectory && r.CreatedAtUtc>=_created).Take(2).ToArrayAsync(ct);
        if(roots.Length>1 || roots.Length==1 && roots[0].DisplayName!="Controlled search indexing fixture")throw new InvalidOperationException("exact-recovery-root-not-proved");
        var createdFile=files.SingleOrDefault(f=>Path.GetFileName(f).EndsWith("-root-created.json",StringComparison.Ordinal));
        Guid? createdRoot=null;
        var damagedOriginalReceipt=false;
        if(createdFile is not null)
        {
            try
            {
                using var created=JsonDocument.Parse(await File.ReadAllTextAsync(createdFile,ct));
                createdRoot=created.RootElement.GetProperty("rootId").GetGuid();
            }
            catch(JsonException)
            {
                // Independent exact-path/time/name SQL identity is sufficient here;
                // preserve the damaged evidence instead of trusting a partial receipt.
                damagedOriginalReceipt=true;
            }
        }
        foreach(var recoveredFile in files.Where(f=>Path.GetFileName(f).EndsWith("-recovered-after-exit.json",StringComparison.Ordinal)))
        {
            using var recovered=JsonDocument.Parse(await File.ReadAllTextAsync(recoveredFile,ct));
            var binding=recovered.RootElement;
            if(binding.GetProperty("operation").GetGuid()!=_operation || binding.GetProperty("directory").GetString()!=expectedDirectory ||
                binding.GetProperty("createdUtc").GetDateTimeOffset()!=_created)
                throw new InvalidOperationException("durable-recovered-root-binding-mismatch");
            var boundRoot=binding.GetProperty("rootId");
            if(boundRoot.ValueKind==JsonValueKind.Null)continue;
            var recoveredRoot=boundRoot.GetGuid();
            if(createdRoot.HasValue && createdRoot!=recoveredRoot)throw new InvalidOperationException("conflicting-durable-recovery-roots");
            createdRoot=recoveredRoot;
        }
        if(damagedOriginalReceipt && roots.Length==0 && createdRoot is null)
            throw new InvalidOperationException("damaged-root-receipt-without-independent-identity");
        _root=roots.Length==1?roots[0].Id:createdRoot;
        if(roots.Length==1 && createdRoot.HasValue && createdRoot!=_root)throw new InvalidOperationException("recovery-root-journal-mismatch");
        if(roots.Length==0 && _root.HasValue &&
            (await db.SourceRootConfigurations.AnyAsync(r=>r.Id==_root,ct) ||
             !await db.SourceDeletionOperations.AnyAsync(d=>d.SourceRootId==_root && d.State==3,ct)))
            throw new InvalidOperationException("completed-native-root-deletion-not-proved");
        _recovered=true;
        _sequence=files.Select(f=>int.TryParse(Path.GetFileName(f).Split('-')[0],out var n)?n:0).Max();
        Journal("recovered-after-exit",new {rootId=_root,operation=_operation,directory=expectedDirectory,createdUtc=_created,modelsStarted=false});
    }

    private void DeleteExactSyntheticInputs()
    {
        var directory=Path.Combine(settings.SourcesRoot,"controlled-index-"+_operation.ToString("N"));NoLinks(directory);
        if(!Directory.Exists(directory))return;
        var names=Directory.EnumerateFileSystemEntries(directory, "*",SearchOption.TopDirectoryOnly).Take(4).Select(Path.GetFileName).ToArray();
        if(names.Length>3 || names.Any(n=>n is not ("backlog.txt" or "publication.txt" or ".staging-fixture-owner.json")))throw new InvalidOperationException("foreign-fixture-source-entry-preserved");
        var marker=Path.Combine(directory,".staging-fixture-owner.json");NoLinks(marker);
        if(File.Exists(marker))VerifyOwnedBytes(marker,OwnerMarker(),allowPartial:_root is null);
        foreach(var item in new[]{("backlog.txt",2048),("publication.txt",32)})
        {
            var path=Path.Combine(directory,item.Item1);NoLinks(path);if(!File.Exists(path))continue;
            var bytes=Encoding.UTF8.GetBytes(Source(_operation,item.Item2));
            VerifyOwnedBytes(path,bytes,allowPartial:_root is null);
        }
        // Only the two freshly generated inputs with their exact original bytes are removable.
        foreach(var name in names)File.Delete(Path.Combine(directory,name!));
        Directory.Delete(directory,false);
        Journal("synthetic-inputs-removed",new {directory,absent=!Directory.Exists(directory)});
    }

    private void VerifyOwnedBytes(string path,byte[] expected,bool allowPartial)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length>expected.Length || file.Length!=expected.Length && !allowPartial || File.GetCreationTimeUtc(path)<_created.UtcDateTime)
            throw new InvalidOperationException("changed-synthetic-input-preserved");
        // Interrupted intake is removable only within the journalled fresh namespace,
        // before any SQL root committed, and with every existing byte equal to the generated prefix.
        if(!SHA256.HashData(file).SequenceEqual(SHA256.HashData(expected.AsSpan(0,checked((int)file.Length)))))
            throw new InvalidOperationException("changed-synthetic-input-preserved");
    }

    private sealed class OwnedDeletionStore(ISourceDeletionStore native,IDbContextFactory<FluxKnowledgeDbContext> factory,
        StagingIndexFixtureScope baseline,Guid root,Guid operation,Func<bool> exclusive) : ISourceDeletionStore
    {
        public async ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken ct)
        {
            var snapshot=await SnapshotAsync(factory,ct);
            if(!baseline.CanClaimDeletion(snapshot.Roots,snapshot.Deletions,root,operation,DateTimeOffset.UtcNow,exclusive()))return null;
            var claimed=await native.ClaimNextAsync(ct);
            if(claimed is not null && (claimed.OperationId!=operation || claimed.SourceRootId!=root))
                throw new InvalidOperationException("closed-fixture-native-deletion-identity-violation");
            return claimed;
        }
        public ValueTask<SourceDeletionRunResult> PurgeAsync(SourceDeletionWorkItem work,IndexGenerationCandidateSnapshot? survivor,CancellationToken ct)
        {Check(work);return native.PurgeAsync(work,survivor,ct);}
        public ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(SourceDeletionWorkItem work,CancellationToken ct)
        {Check(work);return native.ReadPendingCleanupAsync(work,ct);}
        public ValueTask RecordCleanupResultAsync(SourceDeletionWorkItem work,Guid item,SourceDeletionFileResult result,CancellationToken ct)
        {Check(work);return native.RecordCleanupResultAsync(work,item,result,ct);}
        public ValueTask FailAsync(SourceDeletionWorkItem work,string reason,CancellationToken ct){Check(work);return native.FailAsync(work,reason,ct);}
        public ValueTask DeferAsync(SourceDeletionWorkItem work,string reason,CancellationToken ct){Check(work);return native.DeferAsync(work,reason,ct);}
        private void Check(SourceDeletionWorkItem work)
        {if(!exclusive() || work.OperationId!=operation || work.SourceRootId!=root)throw new InvalidOperationException("foreign-native-deletion-work-refused");}
    }
}
