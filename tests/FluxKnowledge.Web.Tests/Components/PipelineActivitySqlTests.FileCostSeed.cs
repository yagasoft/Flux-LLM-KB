using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    private static async Task SeedFileCostTailBatchAsync(FluxKnowledgeDbContext context, Guid root,
        Guid templateRevision, string pathPrefix, int start, int count, int currentJobCount)
    {
        // Match the old SaveChanges boundary: one ordinary transaction per 1,000
        // revisions. Normal inserts retain checks, triggers and generated columns.
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SET NOCOUNT ON;
            DECLARE @record uniqueidentifier, @identity uniqueidentifier, @job uniqueidentifier,
                @activity uniqueidentifier, @outbox uniqueidentifier;
            SELECT @record=p.Id, @identity=p.SourceIdentityId, @job=j.Id, @activity=a.Id, @outbox=o.Id
            FROM SourceRevisions r JOIN PipelineRecords p ON p.SourceRevisionId=r.Id
            JOIN Jobs j ON j.PipelineRecordId=p.Id JOIN OutboxMessages o ON o.JobId=j.Id
            JOIN SourceActivities a ON a.SourceRevisionId=r.Id AND a.ResultingPipelineRecordId=p.Id
            WHERE r.Id={templateRevision} AND r.SourceRootId={root} AND r.Extension=N'.txt';
            IF @record IS NULL OR @identity IS NULL OR @job IS NULL OR @activity IS NULL OR @outbox IS NULL
                THROW 51000, 'The ordinary file-cost seed template is incomplete.', 1;
            DECLARE @members TABLE (Ordinal int PRIMARY KEY, RevisionId uniqueidentifier,
                IdentityId uniqueidentifier, RecordId uniqueidentifier, JobId uniqueidentifier,
                ActivityId uniqueidentifier, OutboxId uniqueidentifier, CanonicalPath nvarchar(2048));
            WITH digit(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) d(n))
            INSERT @members
            SELECT n, NEWID(), NEWID(), NEWID(), NEWID(), NEWID(), NEWID(),
                {pathPrefix}+N'discovered-'+RIGHT(N'00000'+CONVERT(nvarchar(5),n),5)+N'.txt'
            FROM (SELECT {start}+a.n+10*b.n+100*c.n n FROM digit a CROSS JOIN digit b CROSS JOIN digit c) numbers
            WHERE n<{start}+{count};
            INSERT SourceRevisions (Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,
                ParentSourceRevisionId,Classification,Extension,OriginKind,ByteLength,FileCreatedAtUtc,
                FileLastWriteAtUtc,DiscoveredAtUtc,DiscoveryEvidenceJson,CurrentDiscoveryEvidenceJson,
                SuppressedAtUtc,RetainUntilUtc,RetentionEvidenceJson)
            SELECT m.RevisionId,r.SourceRootId,N'overview-source:'+LOWER(REPLACE(CONVERT(nvarchar(36),m.RevisionId),N'-',N'')),
                r.Revision,r.ContentSha256,m.CanonicalPath,r.ParentSourceRevisionId,r.Classification,r.Extension,
                r.OriginKind,r.ByteLength,r.FileCreatedAtUtc,r.FileLastWriteAtUtc,r.DiscoveredAtUtc,
                r.DiscoveryEvidenceJson,r.CurrentDiscoveryEvidenceJson,r.SuppressedAtUtc,r.RetainUntilUtc,r.RetentionEvidenceJson
            FROM @members m CROSS JOIN SourceRevisions r WHERE r.Id={templateRevision};
            INSERT SourceIdentities (Id,SourceKind,StableKey,CreatedAtUtc)
            SELECT m.IdentityId,i.SourceKind,m.CanonicalPath,i.CreatedAtUtc
            FROM @members m CROSS JOIN SourceIdentities i WHERE i.Id=@identity AND m.Ordinal<{currentJobCount};
            INSERT PipelineRecords (Id,SourceIdentityId,SourceRevisionId,Revision,ContentHash,RootLineageRecordId,
                ParentRevisionRecordId,CurrentStage,CompletionCriteriaMet,IsDeleted,RegisteredAtUtc)
            SELECT m.RecordId,m.IdentityId,m.RevisionId,p.Revision,p.ContentHash,m.RecordId,p.ParentRevisionRecordId,
                p.CurrentStage,p.CompletionCriteriaMet,p.IsDeleted,p.RegisteredAtUtc
            FROM @members m CROSS JOIN PipelineRecords p WHERE p.Id=@record AND m.Ordinal<{currentJobCount};
            INSERT Jobs (Id,PipelineRecordId,SourceRevision,Stage,Operation,PublicState,DueAtUtc,AttemptCount,
                LeaseOwner,LeaseExpiresAtUtc,LeaseGeneration,Reason,ErrorDetails)
            SELECT m.JobId,m.RecordId,j.SourceRevision,j.Stage,j.Operation,j.PublicState,j.DueAtUtc,j.AttemptCount,
                j.LeaseOwner,j.LeaseExpiresAtUtc,j.LeaseGeneration,j.Reason,j.ErrorDetails
            FROM @members m CROSS JOIN Jobs j WHERE j.Id=@job AND m.Ordinal<{currentJobCount};
            INSERT SourceActivities (Id,SourceRevisionId,ActivityKind,ExecutionClass,ProcessorVersion,InputFingerprint,
                DescriptorFingerprint,RequiredCapability,State,Reason,AttemptCount,LastAttemptAtUtc,AttemptEvidenceJson,
                ResultingPipelineRecordId,ResultingPipelineRecordRevision,CreatedAtUtc,UpdatedAtUtc)
            SELECT m.ActivityId,m.RevisionId,a.ActivityKind,a.ExecutionClass,a.ProcessorVersion,a.InputFingerprint,
                a.DescriptorFingerprint,a.RequiredCapability,a.State,a.Reason,a.AttemptCount,a.LastAttemptAtUtc,
                a.AttemptEvidenceJson,m.RecordId,a.ResultingPipelineRecordRevision,a.CreatedAtUtc,a.UpdatedAtUtc
            FROM @members m CROSS JOIN SourceActivities a WHERE a.Id=@activity AND m.Ordinal<{currentJobCount};
            INSERT OutboxMessages (Id,JobId,CompletedArtifactId,PipelineRecordId,SourceRevision,Stage,Operation,
                DispatchGeneration,IdempotencyKey,DueAtUtc,CreatedAtUtc,DispatchedAtUtc,LeaseOwner,LeaseExpiresAtUtc,LeaseGeneration)
            SELECT m.OutboxId,m.JobId,o.CompletedArtifactId,m.RecordId,o.SourceRevision,o.Stage,o.Operation,
                o.DispatchGeneration,N'file-cost:'+LOWER(REPLACE(CONVERT(nvarchar(36),m.RecordId),N'-',N'')),
                o.DueAtUtc,o.CreatedAtUtc,o.DispatchedAtUtc,o.LeaseOwner,o.LeaseExpiresAtUtc,o.LeaseGeneration
            FROM @members m CROSS JOIN OutboxMessages o WHERE o.Id=@outbox AND m.Ordinal<{currentJobCount};
            """);
        await transaction.CommitAsync();
    }

    private static async Task AssertFileCostSeedAsync(TestDbContextFactory factory, Guid root,
        int jobs, int files, DateTimeOffset now, int initialCount = 1000)
    {
        await using var context = factory.CreateDbContext();
        Assert.Equal(files, await context.SourceRevisions.CountAsync());
        Assert.Equal(jobs, await context.SourceIdentities.CountAsync());
        Assert.Equal(jobs, await context.PipelineRecords.CountAsync());
        Assert.Equal(jobs, await context.Jobs.CountAsync());
        Assert.Equal(jobs, await context.OutboxMessages.CountAsync());
        Assert.Equal(jobs+1, await context.SourceActivities.CountAsync());
        Assert.Equal(1, await context.SourceProcessorBranches.CountAsync());
        foreach (var ordinal in new[] { initialCount-1, initialCount, jobs-1, jobs, files-1 }.Distinct().Where(i => i<files))
        {
            var path = ordinal<initialCount ? $"concurrent-{ordinal:D4}.txt" : $"discovered-{ordinal:D5}.txt";
            var canonical = $"C:\\overview-source-indexing\\{root:N}\\{path}";
            var revision = await context.SourceRevisions.AsNoTracking().SingleAsync(r => r.CanonicalPath==canonical);
            Assert.Equal(root, revision.SourceRootId);
            Assert.Equal($"overview-source:{revision.Id:N}", revision.StableSourceIdentity);
            Assert.Equal((1L,new string('a',64),"AcceptedUtf8Text",".txt",0,1L,now,"{}"),
                (revision.Revision,revision.ContentSha256,revision.Classification,revision.Extension,
                    revision.OriginKind,revision.ByteLength,revision.DiscoveredAtUtc,revision.DiscoveryEvidenceJson));
            Assert.Null(revision.ParentSourceRevisionId);
            Assert.Null(revision.SuppressedAtUtc);
            Assert.Null(revision.CurrentDiscoveryEvidenceJson);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))),revision.CanonicalPathFingerprint);
            Assert.Equal(8,revision.RowVersion.Length);
            var record = await context.PipelineRecords.AsNoTracking().SingleOrDefaultAsync(p => p.SourceRevisionId==revision.Id);
            if (ordinal>=jobs)
            {
                Assert.Null(record);
                Assert.False(await context.SourceActivities.AnyAsync(a => a.SourceRevisionId==revision.Id));
                continue;
            }
            Assert.NotNull(record);
            Assert.Equal(record.Id,record.RootLineageRecordId);
            Assert.Equal(1,record.Revision);
            Assert.Equal(new string('a',64),record.ContentHash);
            Assert.Equal((int)PipelineStage.Extract,record.CurrentStage);
            Assert.False(record.IsDeleted);
            Assert.False(record.CompletionCriteriaMet);
            var identity = await context.SourceIdentities.SingleAsync(i => i.Id==record.SourceIdentityId);
            Assert.Equal(("local file",canonical,now),(identity.SourceKind,identity.StableKey,identity.CreatedAtUtc));
            var job = await context.Jobs.SingleAsync(j => j.PipelineRecordId==record.Id);
            Assert.Equal((1L,(int)PipelineStage.Extract,PipelineOperations.ExtractUtf8,(int)PublicJobState.WorkerQueued,now,0,0L),
                (job.SourceRevision,job.Stage,job.Operation,job.PublicState,job.DueAtUtc,job.AttemptCount,job.LeaseGeneration));
            var activity = await context.SourceActivities.SingleAsync(a => a.SourceRevisionId==revision.Id);
            Assert.Equal(record.Id,activity.ResultingPipelineRecordId);
            Assert.Equal(1,activity.ResultingPipelineRecordRevision);
            Assert.Equal(((int)SourceActivityKind.TextExtraction,(int)ExecutionClass.InProcess,"phase-3a-v1",(int)SourceActivityState.Pending),
                (activity.ActivityKind,activity.ExecutionClass,activity.ProcessorVersion,activity.State));
            Assert.Equal(DateTimeOffset.UnixEpoch,activity.CreatedAtUtc);
            Assert.Equal(DateTimeOffset.UnixEpoch,activity.UpdatedAtUtc);
            var outbox = await context.OutboxMessages.SingleAsync(o => o.PipelineRecordId==record.Id);
            Assert.Equal(job.Id,outbox.JobId);
            Assert.Equal($"file-cost:{record.Id:N}",outbox.IdempotencyKey);
            Assert.Equal((1L,job.Stage,job.Operation,0L,now,now,0L),
                (outbox.SourceRevision,outbox.Stage,outbox.Operation,outbox.DispatchGeneration,outbox.DueAtUtc,outbox.CreatedAtUtc,outbox.LeaseGeneration));
        }
    }

    [NativeSqlServerFact]
    public async Task File_cost_seed_rolls_back_a_failed_batch_and_keeps_generated_hashes_and_rowversions()
    {
        var owned = new NativeSqlServerFixture();
        await owned.InitializeAsync();
        try
        {
            var factory = new TestDbContextFactory(owned.ConnectionString);
            await using var setup = factory.CreateDbContext();
            await setup.Database.ExecuteSqlRawAsync("""
                ALTER TABLE dbo.SourceIdentities ADD CONSTRAINT CK_TestFileCostRollback
                    CHECK (StableKey NOT LIKE N'%discovered-01000.txt');
                """);
            try
            {
                var failure = await Assert.ThrowsAsync<SqlException>(() =>
                    new PipelineActivitySqlTests(owned,output).SeedFileCostFixtureAsync(2000,2000));
                Assert.Equal(547,failure.Number);
                Assert.Equal(1000,await setup.SourceRevisions.CountAsync());
                Assert.Equal(1000,await setup.SourceIdentities.CountAsync());
                Assert.Equal(1000,await setup.PipelineRecords.CountAsync());
                Assert.Equal(1000,await setup.Jobs.CountAsync());
                Assert.Equal(1000,await setup.OutboxMessages.CountAsync());
                Assert.Equal(1001,await setup.SourceActivities.CountAsync());
                var root = await setup.SourceRevisions.Select(r => r.SourceRootId).Distinct().SingleAsync();
                var template = await setup.SourceRevisions.Where(r => r.CanonicalPath.EndsWith("concurrent-0001.txt"))
                    .Select(r => r.Id).SingleAsync();
                var prefix = Revision(Guid.Empty,root,"",DateTimeOffset.UtcNow,false).CanonicalPath;
                await SeedFileCostTailBatchAsync(setup,root,template,prefix,2000,1,3000);
                var generatedPath=prefix+"discovered-02000.txt";
                var revision = await setup.SourceRevisions.SingleAsync(r => r.CanonicalPath==generatedPath);
                var before = revision.RowVersion.ToArray();
                revision.CurrentDiscoveryEvidenceJson="{}";
                await setup.SaveChangesAsync();
                Assert.False(before.SequenceEqual(revision.RowVersion));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(revision.CanonicalPath))),revision.CanonicalPathFingerprint);
            }
            finally { await setup.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.SourceIdentities DROP CONSTRAINT CK_TestFileCostRollback;"); }
        }
        finally { await owned.DisposeAsync(); }
    }
}
