using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SourceManifestRetirementPageTests
{
    [NativeSqlServerFact]
    public async Task Segment_selection_requires_both_the_exact_operation_and_the_exact_source_target()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"Foreign segment authority must never be inferred.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,generation,segments,recovery)=await SeedSegmentsAsync(environment,19);
        var foreignOperation=segments[0];var missingTarget=segments[1];var files=new SegmentFiles(new WindowsIndexProjectionFileStore(environment.IndexRoot));
        var store=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=async(phase,db,ct)=>
        {
            if(phase!="manifest-page-marked")return;
            await db.IndexSegments.Where(row=>row.Id==foreignOperation).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.RetirementOperationId,(Guid?)Guid.NewGuid()),ct);
            await db.SourceDeletionCleanupItems.Where(row=>row.SourceDeletionOperationId==work.OperationId && row.StorageKind==4 && row.RelativePath==missingTarget).ExecuteDeleteAsync(ct);
        }};
        Assert.Equal("source-delete-segment-retirement-progressing",await store.AdvanceAsync(work,default));
        Assert.Equal(16,files.Segments.Count);Assert.Empty(files.Manifests);Assert.DoesNotContain(foreignOperation,files.Segments);Assert.DoesNotContain(missingTarget,files.Segments);
        Assert.True(Directory.Exists(Path.Combine(environment.IndexRoot,"segments",foreignOperation)));Assert.True(Directory.Exists(Path.Combine(environment.IndexRoot,"segments",missingTarget)));
        await using var check=environment.Factory.CreateDbContext();Assert.Equal(3,await check.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==2));
        Assert.Null(await check.IndexGenerations.Where(row=>row.Id==generation).Select(row=>row.ReclaimedAtUtc).SingleAsync());
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task A_live_native_owner_remains_blocking_and_does_not_allow_a_seventeenth_attempt()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"Native ownership remains authoritative.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,generation,segments,recovery)=await SeedSegmentsAsync(environment,17);var blocked=segments.Order(StringComparer.Ordinal).First();var owner=new WindowsInteractiveGpuOwnerProbe().Current;
        await using(var setup=environment.Factory.CreateDbContext())
        {setup.IndexSegmentOwners.Add(new(){Id=Guid.NewGuid(),SegmentId=blocked,OwnerInstanceId=Guid.NewGuid(),OwnerProcessId=owner.ProcessId,OwnerStartedAtUtc=owner.StartedAtUtc,OwnerMachineFingerprint=owner.MachineFingerprint,CreatedAtUtc=DateTimeOffset.UtcNow});await setup.SaveChangesAsync();}
        var files=new SegmentFiles(new WindowsIndexProjectionFileStore(environment.IndexRoot));
        var store=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await store.AdvanceAsync(work,default));
        Assert.Equal(15,files.Segments.Count);Assert.DoesNotContain(blocked,files.Segments);Assert.Empty(files.Manifests);
        await using var check=environment.Factory.CreateDbContext();Assert.Equal(2,await check.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==2));
        Assert.True(Directory.Exists(Path.Combine(environment.IndexRoot,"segments",blocked)));Assert.True(Directory.Exists(Path.Combine(environment.IndexRoot,"manifests",generation.ToString("N"))));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerTheory]
    [InlineData("file-failure")]
    [InlineData("session-loss")]
    [InlineData("lease-expiry")]
    public async Task Interrupted_segment_pages_keep_committed_intents_and_recover_with_a_fresh_caller(string interruption)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"Recover native segment pages.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,generation,segments,recovery)=await SeedSegmentsAsync(environment,3);var clock=new PageClock();var visits=0;
        var files=new SegmentFiles(new WindowsIndexProjectionFileStore(environment.IndexRoot)){FailAt=interruption=="file-failure"?2:null};
        var store=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,clock,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=async(phase,db,ct)=>{if(phase=="before-delete" && ++visits==2){if(interruption=="session-loss")await db.Database.GetDbConnection().CloseAsync();if(interruption=="lease-expiry")clock.Now=clock.Now.AddMinutes(20);}}};
        if(interruption=="file-failure")await Assert.ThrowsAsync<IOException>(()=>store.AdvanceAsync(work,default).AsTask());
        else Assert.Equal(interruption=="session-loss"?"source-delete-segment-retirement-ownership-lost":"source-delete-segment-retirement-source-authority-lost",await store.AdvanceAsync(work,default));
        Assert.Single(files.Segments);Assert.Empty(files.Manifests);
        await using(var check=environment.Factory.CreateDbContext())
        {
            Assert.Equal(3,await check.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==2 && row.RetirementOperationId!=null));
            Assert.False(await check.IndexGenerationSegments.AnyAsync(row=>row.GenerationId==generation));
            Assert.Single(segments,id=>!Directory.Exists(Path.Combine(environment.IndexRoot,"segments",id)));
        }
        var retryWork=interruption=="lease-expiry"?Assert.IsType<SourceDeletionWorkItem>(await new SqlSourceDeletionStore(environment.Factory,clock).ClaimNextAsync(default)):work;
        var retry=new SqlSegmentRetirementOperations(environment.Factory,new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,clock,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(retryWork,default));
        await using var final=environment.Factory.CreateDbContext();Assert.Equal(3,await final.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==3));
        Assert.NotNull(await final.IndexGenerations.Where(row=>row.Id==generation).Select(row=>row.ReclaimedAtUtc).SingleAsync());
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task A_lost_segment_page_acknowledgement_does_not_repeat_native_deletion()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"The native result survives lost acknowledgement.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,_,segments,recovery)=await SeedSegmentsAsync(environment,3);var files=new SegmentFiles(new WindowsIndexProjectionFileStore(environment.IndexRoot));
        var store=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=(phase,_,_)=>{if(phase=="manifest-page-reclaimed")throw new OperationCanceledException("The native page committed before its acknowledgement was lost.");return ValueTask.CompletedTask;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.AdvanceAsync(work,default).AsTask());Assert.Equal(3,files.Segments.Count);Assert.Single(files.Manifests);
        var retryFiles=new SegmentFiles(new WindowsIndexProjectionFileStore(environment.IndexRoot));
        var retry=new SqlSegmentRetirementOperations(environment.Factory,retryFiles,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(work,default));Assert.Empty(retryFiles.Segments);Assert.Empty(retryFiles.Manifests);
        await using var final=environment.Factory.CreateDbContext();Assert.Equal(3,await final.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==3));
        Assert.Equal(1,await final.SourceDeletionCleanupItems.CountAsync(row=>row.SourceDeletionOperationId==work.OperationId && row.StorageKind==3 && row.State==1));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_between_phase_reference_or_changed_operation_blocks_segment_files(bool changedOperation)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"A fresh native-phase proof is required.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,generation,segments,recovery)=await SeedSegmentsAsync(environment,3);var files=new SegmentFiles(new WindowsIndexProjectionFileStore(environment.IndexRoot));
        var store=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=async(phase,db,ct)=>{if(phase!="manifest-page-marked")return;if(changedOperation)await db.IndexGenerations.Where(row=>row.Id==generation).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.RetirementOperationId,(Guid?)Guid.NewGuid()),ct);else await db.Artifacts.Where(row=>row.Stage==1).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.SearchText,generation.ToString("D")),ct);}};
        Assert.Equal(changedOperation?"source-delete-segment-retirement-intent-mismatch":"source-delete-segment-retirement-artefact-unconfirmed",await store.AdvanceAsync(work,default));
        Assert.Empty(files.Segments);Assert.Empty(files.Manifests);
        await using var check=environment.Factory.CreateDbContext();Assert.Equal(3,await check.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==2));
    }

    [NativeSqlServerFact]
    public async Task A_committed_manifest_advances_sixteen_native_segments_then_reuses_its_intent_without_mark_scans()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"The serving vector survives retirement of its historical copies.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,generation,segments,recovery)=await SeedSegmentsAsync(environment,17);
        var commands=new ReferenceCommands();
        var store=new SqlSegmentRetirementOperations(new Factory(Fixture.ConnectionString,commands),new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await store.AdvanceAsync(work,default));
        await using(var check=environment.Factory.CreateDbContext())
        {
            Assert.Equal(16,await check.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==3));
            Assert.Null(await check.IndexGenerations.Where(row=>row.Id==generation).Select(row=>row.ReclaimedAtUtc).SingleAsync());
            Assert.Equal(16,segments.Count(id=>!Directory.Exists(Path.Combine(environment.IndexRoot,"segments",id))));
        }
        var retryCommands=new ReferenceCommands();var nativeProofs=0;
        var retry=new SqlSegmentRetirementOperations(new Factory(Fixture.ConnectionString,retryCommands),new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe())
        {FileOperationCheckpoint=(phase,_,_)=>{if(phase=="manifest-page-reference-check")nativeProofs++;return ValueTask.CompletedTask;}};
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(work,default));
        Assert.Equal(0,retryCommands.Scans);Assert.Empty(retryCommands.GpuReads);Assert.Equal(1,nativeProofs);
        await using var final=environment.Factory.CreateDbContext();
        Assert.Equal(17,await final.IndexSegments.CountAsync(row=>segments.Contains(row.Id) && row.State==3));
        Assert.NotNull(await final.IndexGenerations.Where(row=>row.Id==generation).Select(row=>row.ReclaimedAtUtc).SingleAsync());
        Assert.False(Directory.Exists(Path.Combine(environment.IndexRoot,"manifests",generation.ToString("N"))));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    private static async Task<(SourceDeletionWorkItem Work,Guid Generation,string[] Segments,string Recovery)> SeedSegmentsAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment,int count)
    {
        var (work,_,recovery)=await SeedAsync(environment,0);
        await using var context=environment.Factory.CreateDbContext();
        var vector=await context.Vectors.AsNoTracking().FirstAsync();var template=await context.IndexSegments.AsNoTracking().FirstAsync();
        var input=new CanonicalVector(vector.VectorId,vector.TextChunkId,vector.ModelFingerprint,vector.Dimensions,vector.Values,vector.TextChunkContentHash,vector.PayloadChecksum,vector.SourceRevision);
        async IAsyncEnumerable<CanonicalVector> One(){yield return input;await Task.CompletedTask;}
        var builder=new UsearchSegmentBuilder(new(environment.IndexRoot),new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot));
        var descriptors=new List<FluxKnowledge.Application.Indexing.IndexSegmentDescriptor>();var now=DateTimeOffset.UtcNow;
        for(var number=0;number<count;number++)
        {
            var values=(byte[])vector.Values.Clone();BitConverter.GetBytes((float)(number+1)).CopyTo(values,0);
            input=input with {Values=values,PayloadChecksum=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(values))};
            var segment=Assert.Single(await builder.BuildAsync(input.ModelFingerprint,input.Dimensions,One(),default));descriptors.Add(segment);
            context.IndexSegments.Add(new(){Id=segment.Id,ModelFingerprint=segment.ModelFingerprint,Dimensions=segment.Dimensions,NativeFormat=segment.NativeFormat,
                DirectoryPath=segment.DirectoryPath,MembershipChecksum=segment.MembershipChecksum,FileChecksum=segment.FileChecksum,FileLength=segment.FileLength,
                VectorCount=segment.VectorCount,State=1,ProducingCandidateId=template.ProducingCandidateId,CreatedAtUtc=now,SealedAtUtc=now});
            context.IndexSegmentVectors.Add(new(){SegmentId=segment.Id,VectorId=input.VectorId});
            context.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=work.OperationId,StorageKind=4,RelativePath=segment.Id,State=0,CreatedAtUtc=now,UpdatedAtUtc=now});
        }
        var state=await context.IndexState.SingleAsync();
        var manifest=new UsearchManifestValidator(new(environment.IndexRoot),new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot))
            .Place(new(state.CorpusEpoch,state.CorpusVersion+1),input.ModelFingerprint,input.Dimensions,descriptors);
        var generation=manifest.Generation;
        context.IndexGenerations.Add(new(){Id=generation.Id,FormatVersion=2,SegmentCount=descriptors.Count,CorpusEpoch=state.CorpusEpoch,CorpusVersion=state.CorpusVersion+1,
            ModelFingerprint=generation.ModelFingerprint,Dimensions=generation.Dimensions,IndexPath=generation.IndexPath,MetadataChecksum=generation.MetadataChecksum,
            VectorCount=generation.VectorCount,CreatedAtUtc=now,ValidatedAtUtc=now});
        for(var ordinal=0;ordinal<descriptors.Count;ordinal++) context.IndexGenerationSegments.Add(new(){GenerationId=generation.Id,SegmentId=descriptors[ordinal].Id,Ordinal=ordinal});
        context.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=work.OperationId,StorageKind=3,RelativePath=generation.Id.ToString("N"),State=0,CreatedAtUtc=now,UpdatedAtUtc=now});
        await context.SaveChangesAsync();
        return(work,generation.Id,descriptors.Select(row=>row.Id).ToArray(),recovery);
    }

    private sealed class SegmentFiles(IIndexProjectionFileStore inner) : IIndexProjectionFileStore
    {
        internal List<string> Segments {get;}=[];internal List<Guid> Manifests {get;}=[];internal int? FailAt {get;init;}
        public long MaximumSegmentVerificationBytes(FluxKnowledge.Application.Indexing.IndexSegmentDescriptor segment)=>inner.MaximumSegmentVerificationBytes(segment);
        public long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation)=>inner.MaximumManifestVerificationBytes(generation);
        public async ValueTask DeleteSegmentAsync(FluxKnowledge.Application.Indexing.IndexSegmentDescriptor segment,CancellationToken token)
        {if(Segments.Count+1==FailAt)throw new IOException("The second native segment failed.");await inner.DeleteSegmentAsync(segment,token);Segments.Add(segment.Id);}
        public async ValueTask DeleteManifestAsync(IndexGenerationDescriptor generation,CancellationToken token)
        {await inner.DeleteManifestAsync(generation,token);Manifests.Add(generation.Id);}
    }
}
