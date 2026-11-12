using System.Security.Cryptography;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class LegacyStagingRecoveryProofTests(NativeSqlServerFixture fixture):IClassFixture<NativeSqlServerFixture>
{
    [Fact]
    public void Original_retirement_constructor_remains_available_to_existing_binaries()=>
        Assert.Contains(typeof(SqlSegmentRetirementOperations).GetConstructors(),c=>c.GetParameters().Length==6);

    [NativeSqlServerFact]
    public async Task Unrelated_historical_catalogue_size_does_not_expand_the_target_projection_or_remove_prior_pins()
    {
        await using var item=await Case.CreateAsync(fixture);
        var foreign=Enumerable.Range(0,4200).Select(_=>Guid.NewGuid()).ToArray();var historical=Guid.NewGuid();
        await using(var db=await item.Environment.Factory.CreateDbContextAsync())
        {
            db.IndexGenerations.AddRange(foreign.Select(id=>new IndexGenerationEntity{Id=id,Dimensions=2,ModelFingerprint=new string('a',64),
                IndexPath=Path.Combine(item.Plan.Scope.IndexRoot,"generations",id.ToString("N")),MetadataChecksum=new string('b',64),CreatedAtUtc=DateTimeOffset.UtcNow.AddDays(-1)}));
            db.IndexRecoveryPins.Add(new(){Id=new string('e',64),Packet="preserved-unregistered-history",GenerationId=historical});
            await db.SaveChangesAsync();
        }
        await using var proof=await LegacyStagingRecoveryProof.CreateAsync(item.Environment.Factory,item.Plan,default);
        var capture=await new SqlIndexRecoveryReferences(item.Environment.Factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe()).AuditLegacyStagingAsync(proof,default);
        Assert.Contains(capture.Pins,p=>p.GenerationId==historical);
        Assert.True(capture.Pins.Count<20);
        Assert.DoesNotContain(capture.Pins,p=>p.GenerationId is{} id&&foreign.Contains(id));
        await using var check=await item.Environment.Factory.CreateDbContextAsync();
        Assert.Equal(foreign.Length,await check.IndexGenerations.CountAsync(g=>foreign.Contains(g.Id)&&g.RetirementOperationId==null));
    }

    [NativeSqlServerFact]
    public async Task A_changed_deletion_snapshot_is_refused_before_the_new_seal_commits()
    {
        await using var item=await Case.CreateAsync(fixture);
        await using var proof=await LegacyStagingRecoveryProof.CreateAsync(item.Environment.Factory,item.Plan,default);
        var audit=new SqlIndexRecoveryReferences(item.Environment.Factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {
            BeforeProtocolTransaction=async(_,ct)=>
            {
                await using var db=await item.Environment.Factory.CreateDbContextAsync(ct);
                var operation=await db.SourceDeletionOperations.SingleAsync(ct);operation.UpdatedAtUtc=DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        };
        var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>audit.AuditLegacyStagingAsync(proof,default).AsTask());
        Assert.Equal("index-recovery-legacy-admission-snapshot-changed",failure.Message);
        await using var check=await item.Environment.Factory.CreateDbContextAsync();Assert.False((await check.IndexRecoveryAudit.SingleAsync()).IsValid);
    }

    [NativeSqlServerFact]
    public async Task A_wrong_retirement_binding_is_refused_before_any_connection_is_opened()
    {
        await using var item=await Case.CreateAsync(fixture);
        await using var proof=await LegacyStagingRecoveryProof.CreateAsync(item.Environment.Factory,item.Plan,default);
        await new SqlIndexRecoveryReferences(item.Environment.Factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe()).AuditLegacyStagingAsync(proof,default);
        var work=await new SqlSourceDeletionStore(item.Environment.Factory,TimeProvider.System).ClaimNextAsync(default);Assert.NotNull(work);
        var blocked=new BlockedConnectionFactory();
        var retirement=new SqlSegmentRetirementOperations(blocked,new WindowsIndexProjectionFileStore(item.Plan.Scope.IndexRoot),item.Plan.Scope.IndexRoot,
            item.Plan.Scope.RecoveryRoot,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe(),proof);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>retirement.AdvanceAsync(work,default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>retirement.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>retirement.TryReclaimManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>retirement.TryReclaimSegmentAsync(item.Plan.Scope.SegmentId,Guid.NewGuid(),default,work).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>retirement.MarkUnreferencedSegmentAsync(item.Plan.Scope.SegmentId,Guid.NewGuid(),default,work).AsTask());
        Assert.Equal(0,blocked.OpenAttempts);
    }

    [NativeSqlServerFact]
    public async Task Seal_two_requires_the_exact_live_scope_and_never_weakens_current_segment_pins()
    {
        await using var item=await Case.CreateAsync(fixture);
        await using var proof=await LegacyStagingRecoveryProof.CreateAsync(item.Environment.Factory,item.Plan,default);
        var audit=new SqlIndexRecoveryReferences(item.Environment.Factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        var captured=await audit.AuditLegacyStagingAsync(proof,default);
        Assert.NotEmpty(captured.Pins);
        Assert.Contains(captured.Pins,p=>p.GenerationId==item.Bootstrap);
        var work=await new SqlSourceDeletionStore(item.Environment.Factory,TimeProvider.System).ClaimNextAsync(default);
        Assert.NotNull(work);
        var normal=new SqlSegmentRetirementOperations(item.Environment.Factory,new WindowsIndexProjectionFileStore(item.Plan.Scope.IndexRoot),
            item.Plan.Scope.IndexRoot,item.Plan.Scope.RecoveryRoot,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        var refused=await Assert.ThrowsAsync<InvalidOperationException>(()=>normal.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work).AsTask());
        Assert.Equal("segment-retirement-legacy-live-proof-required",refused.Message);
        var scoped=new SqlSegmentRetirementOperations(item.Environment.Factory,new WindowsIndexProjectionFileStore(item.Plan.Scope.IndexRoot),
            item.Plan.Scope.IndexRoot,item.Plan.Scope.RecoveryRoot,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe(),proof);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>scoped.MarkManifestAsync(Guid.NewGuid(),Guid.NewGuid(),default,work).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>scoped.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>scoped.RunOnceAsync(default).AsTask());
        var marked=await scoped.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work);
        Assert.Empty(marked.Segments); // The selected keeper snapshot still references the segment.
        await using(var db=await item.Environment.Factory.CreateDbContextAsync())
        {
            Assert.Equal(1,(await db.IndexSegments.SingleAsync(s=>s.Id==item.Plan.Scope.SegmentId)).State);
            Assert.True((await db.IndexRecoveryAudit.SingleAsync()).IsValid);
        }
        await proof.DisposeAsync(); // Simulate exit before invalidation: a persisted seal is insufficient.
        var afterExit=await Assert.ThrowsAsync<InvalidOperationException>(()=>normal.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work).AsTask());
        Assert.Equal("segment-retirement-legacy-live-proof-required",afterExit.Message);
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>scoped.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work).AsTask());
    }

    [NativeSqlServerFact]
    public async Task A_changed_seal_or_admission_snapshot_cannot_authorise_retirement()
    {
        await using var item=await Case.CreateAsync(fixture);
        await using var proof=await LegacyStagingRecoveryProof.CreateAsync(item.Environment.Factory,item.Plan,default);
        var audit=new SqlIndexRecoveryReferences(item.Environment.Factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        await audit.AuditLegacyStagingAsync(proof,default);
        var work=await new SqlSourceDeletionStore(item.Environment.Factory,TimeProvider.System).ClaimNextAsync(default);Assert.NotNull(work);
        // Disposable SQL fault injection: change only the seal's row version.
        await using(var db=await item.Environment.Factory.CreateDbContextAsync())
        {var seal=await db.IndexRecoveryAudit.SingleAsync();seal.FailurePath="synthetic-row-version-change";await db.SaveChangesAsync();}
        var scoped=new SqlSegmentRetirementOperations(item.Environment.Factory,new WindowsIndexProjectionFileStore(item.Plan.Scope.IndexRoot),
            item.Plan.Scope.IndexRoot,item.Plan.Scope.RecoveryRoot,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe(),proof);
        var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>scoped.MarkManifestAsync(item.Plan.Scope.GenerationId,Guid.NewGuid(),default,work).AsTask());
        Assert.Equal("segment-retirement-legacy-live-proof-required",failure.Message);
    }

    [NativeSqlServerTheory]
    [InlineData("prior-pin")]
    [InlineData("active-operation")]
    [InlineData("missing-app-role")]
    public async Task Incomplete_or_conflicting_authority_is_refused(string fault)
    {
        await using var item=await Case.CreateAsync(fixture);
        var plan=item.Plan;
        await using(var db=await item.Environment.Factory.CreateDbContextAsync())
        {
            if(fault=="prior-pin")db.IndexRecoveryPins.Add(new(){Id=new string('d',64),Packet="preserved-unrelated-reference",GenerationId=plan.Scope.GenerationId});
            if(fault=="active-operation")
            {var operation=await db.SourceDeletionOperations.SingleAsync();operation.State=2;operation.LeaseId=Guid.NewGuid();operation.LeaseExpiresAtUtc=DateTimeOffset.UtcNow.AddMinutes(1);}
            await db.SaveChangesAsync();
        }
        if(fault=="missing-app-role")plan=plan with{Evidence=plan.Evidence.Where(e=>e.Kind!="application-model").ToArray()};
        await Assert.ThrowsAnyAsync<Exception>(()=>LegacyStagingRecoveryProof.CreateAsync(item.Environment.Factory,plan,default));
    }

    private sealed class Case:IAsyncDisposable
    {
        internal required SqlToUsearchRebuildTests.PipelineEnvironment Environment {get;init;}
        internal required LegacyStagingRecoveryPlan Plan {get;init;}
        internal Guid Bootstrap {get;init;}
        internal required string Stage {get;init;}
        internal required string Evidence {get;init;}

        internal static async Task<Case> CreateAsync(NativeSqlServerFixture fixture)
        {
            var token=Guid.NewGuid().ToString("N");var operation=Guid.NewGuid();
            var stage=@"I:\Codex Workspaces\Staging\FluxKnowledge\LegacyNative-"+token;
            var evidence=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\legacy-proof-unit-"+token;
            var recovery=Path.Combine(stage,"Recovery");var index=Path.Combine(stage,"Data","Index");
            Directory.CreateDirectory(recovery);Directory.CreateDirectory(evidence);
            SqlToUsearchRebuildTests.PipelineEnvironment? environment=null;
            try
            {
                foreach(var app in new[]{"App-before-M32","App-before-model-admission","App-before-keyword-literal-20261030"})
                {
                    var path=Path.Combine(recovery,app);Directory.CreateDirectory(path);
                    File.Copy(typeof(FluxKnowledge.Web.Mcp.NativeV1McpTools).Assembly.Location,Path.Combine(path,"FluxKnowledge.Web.dll"));
                    File.WriteAllText(Path.Combine(path,"FluxKnowledge.Web.deps.json"),"""{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"},"targets":{".NETCoreApp,Version=v10.0":{"FluxKnowledge.Web/1.0.0":{"runtime":{"FluxKnowledge.Web.dll":{}}}}}}""");
                    File.WriteAllText(Path.Combine(path,"FluxKnowledge.Web.runtimeconfig.json"),"""{"runtimeOptions":{"tfm":"net10.0"}}""");
                    File.WriteAllText(Path.Combine(path,"web.config"),"""<configuration><aspNetCore processPath="dotnet" arguments=".\FluxKnowledge.Web.dll" /></configuration>""");
                }
                var backup="pre-m32-activation-"+Guid.NewGuid().ToString("N")+".bak";
                File.WriteAllText(Path.Combine(recovery,"source-copy-only.bak"),"synthetic source backup bytes");
                File.WriteAllText(Path.Combine(recovery,backup),"synthetic prior staging backup bytes");
                File.WriteAllText(Path.Combine(recovery,"staging-before-ann512.json"),JsonSerializer.Serialize(new
                {Origin="http://127.0.0.1:5147",DashboardReadBenchmark=true,SqlConnectionString=fixture.ConnectionString,CpuSemantic=true,SourceJournalEnabled=false,
                    IndexPublicationMode="Segmented",CpuRerankerShards=4,CpuRerankerThreads=4}));
                environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A keeper survives every legacy proof test.",segmented:true,indexRootOverride:index);
                var bootstrap=await environment.ActiveGenerationAsync();var verified=DateTimeOffset.UtcNow.AddSeconds(-1);
                var created=DateTimeOffset.UtcNow;var text="Unique synthetic publication "+operation.ToString("N")+" for a target-restricted proof.";
                var directory=Path.Combine(stage,"Sources","controlled-index-"+operation.ToString("N"));
                Directory.CreateDirectory(directory);await File.WriteAllTextAsync(Path.Combine(directory,"publication.txt"),text);
                var recordId=await environment.AddRetainedAndPumpAsync(text,"publication",directory);
                var generation=await environment.ActiveGenerationAsync();
                Guid revisionId,rootId,deletionId=Guid.NewGuid(),incarnation;string hash,segmentId;long version;Guid epoch;
                await using(var db=await environment.Factory.CreateDbContextAsync())
                {
                    var record=await db.PipelineRecords.SingleAsync(r=>r.Id==recordId);hash=record.ContentHash;revisionId=record.SourceRevisionId!.Value;
                    rootId=(await db.SourceRevisions.SingleAsync(r=>r.Id==revisionId)).SourceRootId;
                    var candidate=await db.IndexPublicationCandidates.SingleAsync(c=>c.ResultGenerationId==generation.Id);
                    segmentId=(await db.IndexSegments.SingleAsync(s=>s.ProducingCandidateId==candidate.Id)).Id;
                    version=(await db.IndexGenerations.SingleAsync(g=>g.Id==generation.Id)).CorpusVersion!.Value;epoch=candidate.CorpusEpoch;
                    await db.Database.OpenConnectionAsync();incarnation=await SqlIndexRecoveryReferences.DatabaseIdentityAsync(db,default);
                }
                await environment.AddAndPumpAtPathAsync("The replacement keeper remains selected while historical snapshots retire.","initial.txt");
                await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
                await using(var db=await environment.Factory.CreateDbContextAsync())
                {
                    var root=await db.SourceRootConfigurations.SingleAsync(r=>r.Id==rootId);root.State=2;
                    db.SourceDeletionOperations.Add(new(){Id=deletionId,SourceRootId=rootId,State=0,Phase="cleanup-memberships",CreatedAtUtc=DateTimeOffset.UtcNow,UpdatedAtUtc=DateTimeOffset.UtcNow});
                    db.SourceDeletionCleanupItems.AddRange(new SourceDeletionCleanupItemEntity{Id=Guid.NewGuid(),SourceDeletionOperationId=deletionId,StorageKind=3,RelativePath=generation.Id.ToString("N")},
                        new(){Id=Guid.NewGuid(),SourceDeletionOperationId=deletionId,StorageKind=4,RelativePath=segmentId});
                    await db.SaveChangesAsync();
                }
                var files=Directory.GetFiles(recovery,"*",SearchOption.AllDirectories).Select(p=>new RecoveryLegacyFile(Path.GetRelativePath(recovery,p),new FileInfo(p).Length,Hash(p))).ToArray();
                var documents=new List<LegacyStagingEvidenceFile>();
                void Write(string kind,object value)
                {var path=Path.Combine(evidence,kind+".json");File.WriteAllText(path,JsonSerializer.Serialize(value));documents.Add(new(kind,path,new FileInfo(path).Length,Hash(path)));}
                var own=Path.Combine(stage,"Sources","controlled-index-"+operation.ToString("N"));
                Write("before-intake",new{operation,createdUtc=created,directory=own});
                Write("records",new{rootId,records=new[]{new{Id=recordId,SourceRevision=revisionId,Hash=hash,Path=Path.Combine(own,"publication.txt")}}});
                Write("publication",new{recordId,before=new{Generation=bootstrap.Id,Epoch=epoch,Version=version-1},after=new{Generation=generation.Id,Epoch=epoch,Version=version}});
                var source=files.Single(f=>f.RelativePath=="source-copy-only.bak");var prior=files.Single(f=>f.RelativePath==backup);
                Write("backup-source",new{database="FluxKnowledge",is_copy_only=true,has_checksums=true,observed_utc=verified,path=Path.Combine(recovery,source.RelativePath),bytes=source.Bytes,sha256=source.Sha256});
                Write("backup-stage",new{Success=true,Catalogue=fixture.DatabaseName,VerifiedUtc=verified,BackupPath=Path.Combine(recovery,prior.RelativePath),Bytes=prior.Bytes,Sha256=prior.Sha256,
                    Version=version-1,Epoch=epoch,Base=bootstrap.Id,Target=bootstrap.Id,Header=new{DatabaseName=fixture.DatabaseName,IsCopyOnly=true,HasBackupChecksums=true,IsDamaged=false}});
                RecoveryLegacyFile[] App(string folder)=>files.Where(f=>f.RelativePath.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.Ordinal)).ToArray();
                var m32=App("App-before-M32");
                var roleHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',m32.OrderBy(f=>f.RelativePath,StringComparer.Ordinal)
                    .Select(f=>$"{f.RelativePath[("App-before-M32".Length+1)..].Replace('\\','/')}|{f.Bytes}|{f.Sha256}")))));
                Write("application-m32",new{Kind="observed-published-application-only",PayloadRoot=Path.Combine(recovery,"App-before-M32"),RestoreTarget=Path.Combine(stage,"App"),FileManifestSha256=roleHash});
                var model=App("App-before-model-admission");
                Write("application-model",new{Source=Path.Combine(stage,"App"),Destination=Path.Combine(recovery,"App-before-model-admission"),Files=model.Length,Bytes=model.Sum(f=>f.Bytes),AllCopiedHashesMatched=true,ModelsCopied=false,PreservedUtc=verified});
                const string keyword="App-before-keyword-literal-20261030";
                Write("application-keyword",new{source=Path.Combine(stage,"App"),preserved=Path.Combine(recovery,keyword),allCopiesVerified=true,createdUtc=verified,
                    files=App(keyword).Select(f=>new{relativePath=f.RelativePath[(keyword.Length+1)..],bytes=f.Bytes,sha256=f.Sha256})});
                var scope=new LegacyStagingRetirementScope(fixture.DatabaseName,incarnation,recovery,index,deletionId,rootId,operation,recordId,revisionId,hash,generation.Id,segmentId,created);
                return new(){Environment=environment,Plan=new(scope,files,documents),Bootstrap=bootstrap.Id,Stage=stage,Evidence=evidence};
            }
            catch
            {
                if(environment is not null)await environment.DisposeAsync();
                foreach(var path in new[]{stage,evidence})if(Directory.Exists(path))Directory.Delete(path,true);
                throw;
            }
        }

        private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        public async ValueTask DisposeAsync()
        {
            await Environment.DisposeAsync();
            foreach(var path in new[]{Stage,Evidence})
            {
                var full=Path.GetFullPath(path);
                if(full!=path || !Path.GetFileName(full).Contains("-",StringComparison.Ordinal))throw new InvalidOperationException("owned-legacy-case-cleanup-target");
                if(Directory.Exists(full))Directory.Delete(full,true);
            }
        }
    }

    private sealed class BlockedConnectionFactory:IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly BlockOpening _interceptor=new();
        internal int OpenAttempts=>_interceptor.Attempts;
        public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=localhost;Database=FluxKnowledge;Integrated Security=true;Connect Timeout=1").AddInterceptors(_interceptor).Options);
        private sealed class BlockOpening:DbConnectionInterceptor
        {
            internal int Attempts;
            public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,ConnectionEventData eventData,
                InterceptionResult result,CancellationToken cancellationToken=default)
            {Interlocked.Increment(ref Attempts);throw new InvalidOperationException("synthetic-connection-must-not-open");}
        }
    }
}
