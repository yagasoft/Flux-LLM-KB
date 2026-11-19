using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SourceManifestRetirementPageTests
{
    [NativeSqlServerTheory]
    [InlineData("gpu")]
    [InlineData("candidate")]
    [InlineData("checkpoint")]
    [InlineData("vector")]
    [InlineData("native-reference")]
    [InlineData("foreign-source-target")]
    public async Task Page_metadata_checks_keep_foreign_and_recovery_references_blocking(string reference)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"A page must retain every existing metadata guard.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,16);
        await using var setup=environment.Factory.CreateDbContext();var lastId=ids[^1];var generation=await setup.IndexGenerations.SingleAsync(row=>row.Id==lastId);var now=DateTimeOffset.UtcNow;
        switch(reference)
        {
            case "gpu": setup.EmbeddingGpuRequests.Add(new(){MiniTaskId=Guid.NewGuid(),ParentJobId=Guid.NewGuid(),PipelineRecordId=Guid.NewGuid(),SourceRevision=1,
                GenerationId=generation.Id,CorpusEpoch=generation.CorpusEpoch!.Value,ModelFingerprint=generation.ModelFingerprint,Dimensions=generation.Dimensions,
                InputsJson="[]",InputDigest=new string('0',64),CreatedAtUtc=now,UpdatedAtUtc=now});break;
            case "candidate": var candidate=await setup.IndexPublicationCandidates.FirstAsync();candidate.State=1;candidate.BaseGenerationId=generation.Id;candidate.CompletedAtUtc=null;break;
            case "checkpoint": generation.EmbeddingJobId=await setup.Jobs.Select(row=>row.Id).FirstAsync();break;
            case "vector": (await setup.Vectors.FirstAsync()).IndexGenerationId=generation.Id;break;
            case "native-reference": setup.NativeOperationIntents.Add(new(){Id=Guid.NewGuid(),Action="retirement-test",ActorSurface="test",RequestFingerprint=new string('0',64),
                ConfirmationHash=new string('1',64),TargetMetadataJson=System.Text.Json.JsonSerializer.Serialize(new{generationId=generation.Id.ToString("D")}),CreatedAtUtc=now,ExpiresAtUtc=now.AddHours(1)});break;
            case "foreign-source-target":
                var original=await setup.SourceRootConfigurations.SingleAsync(row=>row.Id==work.SourceRootId);
                var foreign=(SourceRootConfigurationEntity)setup.Entry(original).CurrentValues.ToObject();foreign.Id=Guid.NewGuid();foreign.CanonicalPath+="-foreign";foreign.State=0;foreign.RowVersion=[];setup.SourceRootConfigurations.Add(foreign);
                var operation=Guid.NewGuid();setup.SourceDeletionOperations.Add(new(){Id=operation,SourceRootId=foreign.Id,State=0,Phase="accepted",CreatedAtUtc=now,UpdatedAtUtc=now});
                setup.SourceDeletionCleanupItems.Add(new(){Id=Guid.NewGuid(),SourceDeletionOperationId=operation,StorageKind=3,RelativePath=generation.Id.ToString("N"),State=0,CreatedAtUtc=now,UpdatedAtUtc=now});break;
        }
        await setup.SaveChangesAsync();var files=new PageFiles();
        var store=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal(reference=="native-reference"?"source-delete-segment-retirement-recovery-referenced":"source-delete-segment-retirement-manifest-referenced",await store.AdvanceAsync(work,default));
        Assert.Empty(files.Deleted);
        await using var final=environment.Factory.CreateDbContext();Assert.Equal(0,await final.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.RetiredAtUtc!=null));
    }

    private static async Task SeedRequestNoiseAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        await using var context=environment.Factory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("""
            INSERT EmbeddingGpuRequests(MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,GenerationId,CorpusEpoch,ModelFingerprint,Dimensions,InputsJson,InputDigest,State,NativeCleanupConfirmed,CreatedAtUtc,UpdatedAtUtc)
            SELECT TOP(2500) NEWID(),NEWID(),NEWID(),1,NEWID(),NEWID(),N'noise-profile',1,CONCAT(N'[',REPLICATE(N'0,',500),N'0]'),REPLICATE('0',64),0,0,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET()
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
    }
}
