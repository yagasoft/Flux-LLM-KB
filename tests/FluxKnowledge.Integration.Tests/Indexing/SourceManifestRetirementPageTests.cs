using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed partial class SourceManifestRetirementPageTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>
{
    private NativeSqlServerFixture Fixture => fixture;
    [NativeSqlServerFact]
    public async Task A_source_manifest_page_amortises_reference_scans_and_leaves_the_next_target_untouched()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"The current index must survive source retirement.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work, ids, recovery) = await SeedAsync(environment,17);
        await SeedRequestNoiseAsync(environment);
        await using(var noise=environment.Factory.CreateDbContext())
            await noise.Artifacts.Where(row=>row.Stage==(int)PipelineStage.Extract).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.SearchText,new string('z',2*1024*1024)));
        var commands=new ReferenceCommands();
        var files=new PageFiles();
        var reclaimProofs=0;
        var operations=new SqlSegmentRetirementOperations(new Factory(fixture.ConnectionString,commands),files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=(phase,_,_)=>{if(phase=="manifest-page-reference-check")reclaimProofs++;return ValueTask.CompletedTask;}};
        var reason=await operations.AdvanceAsync(work,default);
        output.WriteLine($"retirement_reason={reason}");
        Assert.Equal("source-delete-segment-retirement-progressing",reason);
        await using var check=environment.Factory.CreateDbContext();
        var reclaimed=await check.IndexGenerations.Where(row=>ids.Contains(row.Id) && row.ReclaimedAtUtc!=null).Select(row=>row.Id).ToArrayAsync();
        output.WriteLine($"manifest_targets=17 reclaimed={reclaimed.Length} artifact_reference_scans={commands.Scans+reclaimProofs}");
        Assert.Equal(16,reclaimed.Length);
        Assert.Equal(16,files.Deleted.Count);
        Assert.Equal(1,commands.Scans);Assert.Equal(1,reclaimProofs);
        Assert.Single(commands.GpuReads);
        Assert.Equal(16,await check.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.State==1));
        Assert.Single(await check.IndexGenerations.Where(row=>ids.Contains(row.Id) && row.RetirementOperationId==null).ToArrayAsync());
        var baseline=new ReferenceCommands();
        await using(var original=new Factory(fixture.ConnectionString,baseline).CreateDbContext())
        {
            var id=ids[0].ToString("N");var dashed=ids[0].ToString("D");var path=Path.Combine(environment.IndexRoot,"manifests",id);
            Assert.False(await original.Artifacts.AnyAsync(row=>(row.SearchText==id || row.SearchText==dashed || row.SearchText==path) &&
                (row.Stage!=5 || !original.OutboxMessages.Any(delivery=>delivery.CompletedArtifactId==row.Id && delivery.DispatchedAtUtc!=null))));
        }
        var oldReads=await ReadCostAsync(environment,Enumerable.Repeat(baseline.Reads.Single(),32));
        var pageReads=await ReadCostAsync(environment,commands.Reads.Concat(commands.Reads));
        output.WriteLine($"same_16_manifest_reference_workload old_logical_reads={oldReads} page_logical_reads={pageReads}");
        Assert.True(pageReads*4<oldReads,$"Expected amortised LOB reads: old={oldReads}, page={pageReads}.");
        var oldTypical=await ReadCostAsync(environment,Enumerable.Repeat(baseline.Reads.Single(),32),typical:true);
        var pageTypical=await ReadCostAsync(environment,commands.Reads.Concat(commands.Reads),typical:true);
        output.WriteLine($"typical_1000_bodies old_logical_reads={oldTypical} page_logical_reads={pageTypical}");
        Assert.True(pageTypical*4<oldTypical,$"Expected amortised typical-body reads: old={oldTypical}, page={pageTypical}.");
        var gpuBaseline=new ReferenceCommands();
        await using(var original=new Factory(fixture.ConnectionString,gpuBaseline).CreateDbContext())
            Assert.False(await original.EmbeddingGpuRequests.AnyAsync(row=>row.GenerationId==ids[0]));
        var oldGpu=await ReadCostAsync(environment,Enumerable.Repeat(gpuBaseline.GpuReads.Single(),32));
        var pageGpu=await ReadCostAsync(environment,commands.GpuReads.Concat(commands.GpuReads));
        output.WriteLine($"embedding_reference_guards old_logical_reads={oldGpu} page_logical_reads={pageGpu}");
        Assert.True(pageGpu*4<oldGpu,$"Expected one request scan per phase: old={oldGpu}, page={pageGpu}.");
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerTheory]
    [InlineData("N",false,null)]
    [InlineData("D",false,null)]
    [InlineData("path",false,null)]
    [InlineData("upper",false,null)]
    [InlineData("padding",false,null)]
    [InlineData("soft-hyphens",false,null)]
    [InlineData("combining",false,null)]
    [InlineData("suffix",false,null)]
    [InlineData("publish-pending",false,null)]
    [InlineData("N",true,null)]
    [InlineData("upper",false,"binary")]
    [InlineData("upper",false,"linguistic")]
    [InlineData("combining",false,"binary")]
    [InlineData("combining",false,"linguistic")]
    public async Task Exact_SQL_reference_semantics_and_dispatched_Publish_exemption_are_preserved(string shape,bool dispatched,string? columnCollation)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"Reference semantics.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,1);
        var id=ids[0].ToString("N");var target=shape=="D"?ids[0].ToString("D"):shape=="path"?Path.Combine(environment.IndexRoot,"manifests",id):id;
        var text=shape switch {"upper"=>target.ToUpperInvariant(),"padding"=>target+new string(' ',300),"soft-hyphens"=>new string('\u00ad',300)+target,
            "combining"=>new string('\u034f',300)+target,"suffix"=>target+"x",_=>target};
        await using var setup=environment.Factory.CreateDbContext();
        var stage=dispatched || shape=="publish-pending"?PipelineStage.Publish:PipelineStage.Extract;
        var artifact=await setup.Artifacts.SingleAsync(row=>row.Stage==(int)stage);
        artifact.SearchText=text;await setup.SaveChangesAsync();
        if(shape=="publish-pending") await setup.OutboxMessages.Where(row=>row.CompletedArtifactId==artifact.Id).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.DispatchedAtUtc,(DateTimeOffset?)null));
        if(columnCollation is not null)
        {
            await setup.Database.ExecuteSqlRawAsync("DROP FULLTEXT INDEX ON Artifacts;");
            await setup.Database.ExecuteSqlRawAsync(columnCollation=="binary"
                ? "ALTER TABLE Artifacts ALTER COLUMN SearchText nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL"
                : "ALTER TABLE Artifacts ALTER COLUMN SearchText nvarchar(max) COLLATE Latin1_General_100_CI_AI NOT NULL");
            await setup.Database.ExecuteSqlRawAsync("CREATE FULLTEXT INDEX ON Artifacts(SearchText LANGUAGE 1033) KEY INDEX PK_Artifacts ON FluxKnowledge WITH CHANGE_TRACKING AUTO;");
        }
        var matches=await setup.Artifacts.AnyAsync(row=>row.Id==artifact.Id && row.SearchText==target);
        var files=new PageFiles();
        var operations=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        var reason=await operations.AdvanceAsync(work,default);
        if(matches && !dispatched){Assert.Equal("source-delete-segment-retirement-artefact-unconfirmed",reason);Assert.Empty(files.Deleted);}
        else{Assert.Equal("source-delete-segment-retirement-progressing",reason);Assert.Single(files.Deleted);}
        output.WriteLine($"reference_shape={shape} SQL_equal={matches} dispatched={dispatched}");
    }

    [NativeSqlServerFact]
    public async Task A_new_reference_between_committed_mark_and_reclaim_still_blocks_all_file_deletion()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A new reference.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,3);var files=new PageFiles();
        var operations=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=async (phase,db,ct)=>{if(phase=="manifest-page-marked") await db.Artifacts.Where(row=>row.Stage==1).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.SearchText,ids[0].ToString("N")),ct);}};
        Assert.Equal("source-delete-segment-retirement-artefact-unconfirmed",await operations.AdvanceAsync(work,default));
        Assert.Empty(files.Deleted);
        await using var check=environment.Factory.CreateDbContext();
        Assert.Equal(3,await check.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.RetirementOperationId!=null && row.ReclaimedAtUtc==null));
    }

    [NativeSqlServerTheory]
    [InlineData("file-failure")]
    [InlineData("session-loss")]
    [InlineData("lease-expiry")]
    public async Task An_interruption_between_files_preserves_committed_intents_for_a_fresh_retry(string interruption)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"Interrupted files.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,3);var files=new PageFiles{FailAt=interruption=="file-failure"?2:null};
        var clock=new PageClock();var visits=0;
        var operations=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,clock,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=async (phase,db,ct)=>{if(phase=="before-delete" && ++visits==2){if(interruption=="session-loss") await db.Database.GetDbConnection().CloseAsync();if(interruption=="lease-expiry") clock.Now=clock.Now.AddMinutes(20);}}};
        if(interruption=="lease-expiry") Assert.Equal("source-delete-segment-retirement-source-authority-lost",await operations.AdvanceAsync(work,default));
        else if(interruption=="session-loss") Assert.Equal("source-delete-segment-retirement-ownership-lost",await operations.AdvanceAsync(work,default));
        else await Assert.ThrowsAnyAsync<Exception>(()=>operations.AdvanceAsync(work,default).AsTask());
        Assert.Single(files.Deleted);
        await using(var check=environment.Factory.CreateDbContext())
        {
            Assert.Equal(3,await check.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.RetirementOperationId!=null && row.ReclaimedAtUtc==null));
            Assert.Single(ids,id=>!File.Exists(Path.Combine(environment.IndexRoot,"manifests",id.ToString("N"),"manifest.json")));
        }
        var retryFiles=new PageFiles();var store=new SqlSourceDeletionStore(environment.Factory,clock);
        var retryWork=interruption=="lease-expiry"?Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default)):work;
        var retry=new SqlSegmentRetirementOperations(environment.Factory,retryFiles,environment.IndexRoot,recovery,clock,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(retryWork,default));
        await using var final=environment.Factory.CreateDbContext();
        Assert.Equal(3,await final.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.ReclaimedAtUtc!=null));
        Assert.Equal(3,await final.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.State==1));
    }

    [NativeSqlServerFact]
    public async Task A_lost_mark_acknowledgement_never_deletes_files_and_reuses_the_same_intents()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"Lost mark acknowledgement.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,3);var files=new PageFiles();
        var operations=new SqlSegmentRetirementOperations(new Factory(fixture.ConnectionString,new LostCommit()),files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operations.AdvanceAsync(work,default).AsTask());Assert.Empty(files.Deleted);
        await using var check=environment.Factory.CreateDbContext();
        var original=await check.IndexGenerations.Where(row=>ids.Contains(row.Id)).AsNoTracking().ToDictionaryAsync(row=>row.Id,row=>row.RetirementOperationId);
        Assert.All(original.Values,value=>Assert.NotNull(value));
        var retry=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(work,default));
        Assert.Equal(original.OrderBy(row=>row.Key), (await check.IndexGenerations.Where(row=>ids.Contains(row.Id)).AsNoTracking().ToDictionaryAsync(row=>row.Id,row=>row.RetirementOperationId)).OrderBy(row=>row.Key));
    }

    [NativeSqlServerFact]
    public async Task A_lost_reclaim_acknowledgement_records_the_existing_results_without_deleting_again()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"Lost reclaim acknowledgement.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,3);var files=new PageFiles();
        // The non-retrying file context intentionally strips factory interceptors;
        // inject the acknowledgement loss at its actual SQL commit instead.
        var operations=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=async (phase,db,ct)=>{if(phase=="manifest-page-reclaimed")throw new OperationCanceledException("The reclaim commit acknowledgement was lost.");await Task.CompletedTask;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operations.AdvanceAsync(work,default).AsTask());Assert.Equal(3,files.Deleted.Count);
        await using(var check=environment.Factory.CreateDbContext())
        {
            Assert.Equal(3,await check.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.ReclaimedAtUtc!=null));
            Assert.Equal(3,await check.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.State==0));
        }
        var retryFiles=new PageFiles();var retryCommands=new ReferenceCommands();
        var retry=new SqlSegmentRetirementOperations(new Factory(fixture.ConnectionString,retryCommands),retryFiles,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(work,default));
        Assert.Empty(retryFiles.Deleted);Assert.Equal(0,retryCommands.Scans);
        await using var final=environment.Factory.CreateDbContext();Assert.Equal(3,await final.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.State==1));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_source_authority_and_active_manifest_targets_are_refused_without_changes(bool activeTarget)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A foreign target remains protected.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,3);
        if(activeTarget)
        {
            await using var setup=environment.Factory.CreateDbContext();var active=await environment.ActiveGenerationAsync();var now=DateTimeOffset.UtcNow;
            setup.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=work.OperationId,StorageKind=3,RelativePath=active.Id.ToString("N"),State=0,CreatedAtUtc=now,UpdatedAtUtc=now});await setup.SaveChangesAsync();
        }
        var files=new PageFiles();var operations=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        var reason=await operations.AdvanceAsync(activeTarget?work:work with{SourceRootId=Guid.NewGuid()},default);
        Assert.Equal(activeTarget?"source-delete-segment-retirement-manifest-referenced":"source-delete-segment-retirement-source-authority-lost",reason);Assert.Empty(files.Deleted);
        await using var check=environment.Factory.CreateDbContext();Assert.Equal(3,await check.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.RetirementOperationId==null));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task A_manifest_with_an_unreclaimed_segment_keeps_its_file_while_other_targets_progress()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"The original segment.",segmented:true);
        var previous=await environment.ActiveGenerationAsync();await environment.AddAndPumpAtPathAsync("The current replacement.","initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,2);
        await using var setup=environment.Factory.CreateDbContext();var segmentId=await setup.IndexGenerationSegments.Where(row=>row.GenerationId==previous.Id).Select(row=>row.SegmentId).SingleAsync();var now=DateTimeOffset.UtcNow;
        setup.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=work.OperationId,StorageKind=3,RelativePath=previous.Id.ToString("N"),State=0,CreatedAtUtc=now,UpdatedAtUtc=now});
        setup.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=work.OperationId,StorageKind=4,RelativePath=segmentId,State=0,CreatedAtUtc=now,UpdatedAtUtc=now});await setup.SaveChangesAsync();
        var owner=new WindowsInteractiveGpuOwnerProbe().Current;
        setup.IndexSegmentOwners.Add(new(){Id=Guid.NewGuid(),SegmentId=segmentId,OwnerInstanceId=Guid.NewGuid(),OwnerProcessId=owner.ProcessId,
            OwnerStartedAtUtc=owner.StartedAtUtc,OwnerMachineFingerprint=owner.MachineFingerprint,CreatedAtUtc=now});await setup.SaveChangesAsync();
        var files=new PageFiles();var operations=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await operations.AdvanceAsync(work,default));
        Assert.Equal(2,files.Deleted.Count);Assert.DoesNotContain(previous.Id,files.Deleted);
        Assert.True(File.Exists(Path.Combine(previous.IndexPath,"manifest.json")));
        Assert.Equal(2,await setup.IndexSegments.Where(row=>row.Id==segmentId).Select(row=>row.State).SingleAsync());
        Assert.Null(await setup.IndexGenerations.Where(row=>row.Id==previous.Id).Select(row=>row.ReclaimedAtUtc).SingleAsync());
        Assert.Equal(2,await setup.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.State==1));
    }

    [NativeSqlServerFact]
    public async Task Native_sealed_manifest_files_are_reclaimed_through_the_normal_page_and_receipt_path()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A real native manifest page.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,17,native:true);
        var operations=new SqlSegmentRetirementOperations(environment.Factory,new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await operations.AdvanceAsync(work,default));
        Assert.Equal(16,ids.Count(id=>!Directory.Exists(Path.Combine(environment.IndexRoot,"manifests",id.ToString("N")))));
        await using var check=environment.Factory.CreateDbContext();
        Assert.Equal(16,await check.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.State==1));
        Assert.Equal(16,await check.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.ReclaimedAtUtc!=null));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    private static async Task<(SourceDeletionWorkItem Work, Guid[] Ids, string Recovery)> SeedAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment,int count,bool native=false)
    {
        var now=DateTimeOffset.UtcNow;
        var work=new SourceDeletionWorkItem(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"cleanup-memberships");
        await using var context=environment.Factory.CreateDbContext();
        context.SourceRootConfigurations.Add(new(){Id=work.SourceRootId,CanonicalPath=Path.Combine(environment.IndexRoot,"deleting-source"),DisplayName="retirement page",
            State=(int)SourceRootState.Deleting,Recursive=true,IncludePatternsJson="[]",ExcludePatternsJson="[]",AllowedClassificationsJson="[\"text/plain\"]",
            MaximumFileBytes=16777216,ReconciliationCadenceSeconds=900,ConfigurationRevision=1,CreatedAtUtc=now,UpdatedAtUtc=now});
        context.SourceDeletionOperations.Add(new(){Id=work.OperationId,SourceRootId=work.SourceRootId,State=1,Phase=work.Phase,LeaseId=work.LeaseId,LeaseExpiresAtUtc=now.AddMinutes(15),CreatedAtUtc=now,UpdatedAtUtc=now});
        var current=await context.IndexGenerations.AsNoTracking().SingleAsync(row=>row.Id==(context.IndexState.Single().ActiveIndexGenerationId));
        var ids=new List<Guid>();
        for(var number=0;number<count;number++)
        {
            var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"page-manifest-{work.OperationId:N}-{number}")));
            var id=new Guid(Convert.FromHexString(hash)[..16]);ids.Add(id);
            if(native)
            {
                var manifest=new UsearchManifestValidator(new(environment.IndexRoot),new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot))
                    .Place(new(current.CorpusEpoch!.Value,current.CorpusVersion!.Value+number+1),current.ModelFingerprint,current.Dimensions,[]);
                id=manifest.Generation.Id;hash=manifest.Generation.MetadataChecksum;ids[^1]=id;
            }
            context.IndexGenerations.Add(new(){Id=id,FormatVersion=2,SegmentCount=0,CorpusEpoch=current.CorpusEpoch,CorpusVersion=current.CorpusVersion,
                ModelFingerprint=current.ModelFingerprint,Dimensions=current.Dimensions,IndexPath=Path.Combine(environment.IndexRoot,"manifests",id.ToString("N")),
                MetadataChecksum=hash,VectorCount=0,CreatedAtUtc=now,ValidatedAtUtc=now});
            if(!native){var directory=Path.Combine(environment.IndexRoot,"manifests",id.ToString("N"));Directory.CreateDirectory(directory);await File.WriteAllTextAsync(Path.Combine(directory,"manifest.json"),"synthetic exact deletion target");}
            context.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=work.OperationId,StorageKind=3,RelativePath=id.ToString("N"),State=0,CreatedAtUtc=now,UpdatedAtUtc=now});
        }
        await context.SaveChangesAsync();
        var recovery=Path.Combine(environment.IndexRoot,"page-recovery");Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment,recovery);
        return(work,ids.ToArray(),recovery);
    }

    private sealed class ReferenceCommands : DbCommandInterceptor
    {
        internal int Scans { get; private set; }
        internal List<Captured> Reads {get;}=[];
        internal List<Captured> GpuReads {get;}=[];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("[Artifacts]",StringComparison.Ordinal) && command.CommandText.Contains("[SearchText]",StringComparison.Ordinal) && command.CommandText.Contains("[OutboxMessages]",StringComparison.Ordinal))
            {Scans++;Reads.Add(new(command.CommandText,command.Parameters.Cast<DbParameter>().Select(value=>(DbParameter)((ICloneable)value).Clone()).ToArray()));}
            if(command.CommandText.Contains("[EmbeddingGpuRequests]",StringComparison.Ordinal))
                GpuReads.Add(new(command.CommandText,command.Parameters.Cast<DbParameter>().Select(value=>(DbParameter)((ICloneable)value).Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Factory(string connection,params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).AddInterceptors(interceptors).Options);
    }
    private sealed class PageFiles : IIndexProjectionFileStore
    {
        internal List<Guid> Deleted {get;}=[];
        internal int? FailAt {get;init;}
        public long MaximumSegmentVerificationBytes(IndexSegmentDescriptor segment)=>0;
        public long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation)=>0;
        public ValueTask DeleteManifestAsync(IndexGenerationDescriptor generation,CancellationToken cancellationToken){if(Deleted.Count+1==FailAt) throw new IOException("The next file failed.");File.Delete(Path.Combine(generation.IndexPath,"manifest.json"));Deleted.Add(generation.Id);return ValueTask.CompletedTask;}
        public ValueTask DeleteSegmentAsync(IndexSegmentDescriptor segment,CancellationToken cancellationToken)=>throw new InvalidOperationException("A manifest page must not dispose a segment.");
    }
    private sealed class PageClock : TimeProvider {internal DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class LostCommit : DbTransactionInterceptor
    {
        private bool fired;
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData data,CancellationToken cancellationToken=default)
        {if(!fired){fired=true;throw new OperationCanceledException("The mark commit acknowledgement was lost.");}return Task.CompletedTask;}
    }
    private sealed record Captured(string Sql,DbParameter[] Parameters);
    private static async Task<long> ReadCostAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment,IEnumerable<Captured> reads,bool typical=false)
    {
        await using var context=environment.Factory.CreateDbContext();await context.Database.OpenConnectionAsync();
        var connection=(SqlConnection)context.Database.GetDbConnection();var messages=new List<string>();connection.InfoMessage+=(_,message)=>messages.Add(message.Message);
        if(typical) await context.Database.ExecuteSqlRawAsync("SELECT TOP(0) Id,Stage,SearchText INTO #CostArtifacts FROM Artifacts; CREATE TABLE #CostOutbox(CompletedArtifactId uniqueidentifier,DispatchedAtUtc datetimeoffset); INSERT #CostArtifacts SELECT TOP(1000) NEWID(),1,REPLICATE(CONVERT(nvarchar(max),N'Text body with retained words. '),300) FROM sys.all_objects;");
        await context.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON;");
        foreach(var captured in reads)
        {
            await using var command=connection.CreateCommand();command.CommandText=typical?captured.Sql.Replace("[Artifacts]","#CostArtifacts",StringComparison.Ordinal).Replace("[OutboxMessages]","#CostOutbox",StringComparison.Ordinal):captured.Sql;
            foreach(var parameter in captured.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
            await using var reader=await command.ExecuteReaderAsync();do{while(await reader.ReadAsync()){}}while(await reader.NextResultAsync());
        }
        await context.Database.ExecuteSqlRawAsync("SET STATISTICS IO OFF;");
        return messages.SelectMany(message=>Regex.Matches(message,@"(?:lob )?logical reads (\d+),")).Sum(match=>long.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture));
    }
}
