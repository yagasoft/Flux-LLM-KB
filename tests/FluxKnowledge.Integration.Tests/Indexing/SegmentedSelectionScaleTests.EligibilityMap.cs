using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SegmentedSelectionScaleTests
{
    private static Task SeedMetadataOwnersAsync(FluxKnowledgeDbContext db, long templateVector, int count) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            IF DB_NAME() NOT LIKE N'FluxKnowledge_Phase1Tests[_]%' OR NOT EXISTS(
                SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND owner_sid=SUSER_SID())
                THROW 51000,'Metadata owner seeding requires the owned test catalogue.',1;
            DECLARE @owners TABLE(IdentityId uniqueidentifier,RevisionId uniqueidentifier,RecordId uniqueidentifier,ArtifactId uniqueidentifier);
            INSERT @owners SELECT TOP ({count}) NEWID(),NEWID(),NEWID(),NEWID() FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            DECLARE @artifact uniqueidentifier=(SELECT ArtifactId FROM TextChunks WHERE Id=(SELECT TextChunkId FROM Vectors WHERE VectorId={templateVector}));
            DECLARE @record uniqueidentifier=(SELECT PipelineRecordId FROM Artifacts WHERE Id=@artifact);
            DECLARE @revision uniqueidentifier=(SELECT SourceRevisionId FROM PipelineRecords WHERE Id=@record);
            INSERT SourceIdentities(Id,SourceKind,StableKey,CreatedAtUtc)
            SELECT fresh.IdentityId,original.SourceKind,N'metadata-owner/'+CONVERT(nvarchar(36),fresh.IdentityId),SYSUTCDATETIME()
            FROM @owners fresh CROSS JOIN SourceIdentities original WHERE original.Id=(SELECT SourceIdentityId FROM PipelineRecords WHERE Id=@record);
            INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,ParentSourceRevisionId,
                Classification,Extension,OriginKind,ByteLength,FileCreatedAtUtc,FileLastWriteAtUtc,DiscoveredAtUtc,
                DiscoveryEvidenceJson,CurrentDiscoveryEvidenceJson,SuppressedAtUtc,RetainUntilUtc,RetentionEvidenceJson)
            SELECT fresh.RevisionId,original.SourceRootId,CONVERT(nvarchar(36),fresh.IdentityId),original.Revision,original.ContentSha256,
                original.CanonicalPath+N'.metadata-'+CONVERT(nvarchar(36),fresh.RevisionId)+N'.txt',NULL,
                original.Classification,original.Extension,0,original.ByteLength,original.FileCreatedAtUtc,original.FileLastWriteAtUtc,
                SYSUTCDATETIME(),NULL,NULL,NULL,NULL,NULL
            FROM @owners fresh CROSS JOIN SourceRevisions original WHERE original.Id=@revision;
            INSERT PipelineRecords(Id,SourceIdentityId,SourceRevisionId,Revision,ContentHash,RootLineageRecordId,ParentRevisionRecordId,
                CurrentStage,CompletionCriteriaMet,IsDeleted,RegisteredAtUtc,RepositoryRecoveryBindingJson)
            SELECT fresh.RecordId,fresh.IdentityId,fresh.RevisionId,original.Revision,original.ContentHash,fresh.RecordId,NULL,
                original.CurrentStage,original.CompletionCriteriaMet,0,SYSUTCDATETIME(),NULL
            FROM @owners fresh CROSS JOIN PipelineRecords original WHERE original.Id=@record;
            INSERT Artifacts(Id,PipelineRecordId,SourceRevision,Stage,ContentHash,ContentType,SearchText,DocumentMetadataJson,CreatedAtUtc)
            SELECT fresh.ArtifactId,fresh.RecordId,original.SourceRevision,original.Stage,original.ContentHash,
                original.ContentType,original.SearchText,NULL,SYSUTCDATETIME()
            FROM @owners fresh CROSS JOIN Artifacts original WHERE original.Id=@artifact;
            """);

    [NativeSqlServerFact]
    public async Task Root_eligibility_map_requires_a_transaction_and_rebuilds_after_cancelled_rollback()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "An unrelated source is outside the mapped scope.", segmented: true);
        var recordId = await environment.AddRetainedAndPumpAsync("The eligibility map must not outlive its transaction.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        var record = await db.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == recordId);
        var scopes = JsonSerializer.Serialize(new[] { new IndexAffectedScope(1, record.SourceIdentityId) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(db, scopes, default));
        await db.Database.OpenConnectionAsync();
        var session = await db.Database.SqlQuery<int>($"SELECT CAST(@@SPID AS int) AS [Value]").SingleAsync();
        await using (var invalid = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(db, scopes, default));
            await invalid.RollbackAsync();
        }
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, default);
            await SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(db, scopes, default);
            Assert.Single(await SqlSegmentedIndexStore.QueryMappedRootEligibleWindow(db, 0, long.MaxValue).ToArrayAsync());
            var collision = await Assert.ThrowsAsync<SqlException>(() => SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(db, scopes, default));
            Assert.Equal(2714, collision.Number); // An existing map must never become reusable proof.
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SqlSegmentedIndexStore.QueryMappedRootEligibleWindow(db, 0, long.MaxValue).ToArrayAsync(cancellation.Token));
            await transaction.RollbackAsync();
        }
        Assert.Null(await db.Database.SqlQuery<int?>($"SELECT OBJECT_ID(N'tempdb..#RootEligibleArtifacts') AS [Value]").SingleAsync());
        await db.SourceRevisions.Where(value => value.Id == record.SourceRevisionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SuppressedAtUtc, DateTimeOffset.UtcNow));
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, default);
            await SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(db, scopes, default);
            Assert.Equal(session, await db.Database.SqlQuery<int>($"SELECT CAST(@@SPID AS int) AS [Value]").SingleAsync());
            Assert.Empty(await SqlSegmentedIndexStore.QueryMappedRootEligibleWindow(db, 0, long.MaxValue).ToArrayAsync());
            await transaction.RollbackAsync();
        }
        Assert.Null(await db.Database.SqlQuery<int?>($"SELECT OBJECT_ID(N'tempdb..#RootEligibleArtifacts') AS [Value]").SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("legacy-search-hash", true)]
    [InlineData("content-hash", false)]
    [InlineData("search-hash", false)]
    [InlineData("deleted", false)]
    [InlineData("stale-revision-constraints", true)]
    public async Task Root_eligibility_map_preserves_vector_guards(string mutation, bool eligible)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The plain-source control remains outside this owner scope.", segmented: true);
        var recordId = await environment.AddRetainedAndPumpAsync("Mapped vectors retain canonical byte and revision checks.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        var record = await db.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == recordId);
        var vectorId = await db.CurrentIndexMembers.Where(value => value.SourceIdentityId == record.SourceIdentityId).Select(value => value.VectorId).SingleAsync();
        var vector = await db.Vectors.AsNoTracking().SingleAsync(value => value.VectorId == vectorId);
        var chunkSearchHash = await db.TextChunks.Where(value => value.Id == vector.TextChunkId)
            .Select(value => value.SearchInputHash).SingleAsync();
        if (mutation == "legacy-search-hash") await db.Vectors.Where(value => value.VectorId == vectorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SearchInputHash, (string?)null));
        if (mutation == "content-hash") await db.Vectors.Where(value => value.VectorId == vectorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.TextChunkContentHash, new string(vector.TextChunkContentHash[0] == '0' ? '1' : '0', 64)));
        if (mutation == "search-hash") await db.Vectors.Where(value => value.VectorId == vectorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SearchInputHash, new string(chunkSearchHash[0] == '0' ? '1' : '0', 64)));
        if (mutation == "deleted") await db.Vectors.Where(value => value.VectorId == vectorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.IsDeleted, true));
        if (mutation == "stale-revision-constraints")
        {
            var staleVector = await Assert.ThrowsAsync<SqlException>(() => db.Vectors.Where(value => value.VectorId == vectorId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRevision, value => value.SourceRevision + 1)));
            Assert.Equal(547, staleVector.Number);
            var staleChunk = await Assert.ThrowsAsync<SqlException>(() => db.TextChunks.Where(value => value.Id == vector.TextChunkId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRevision, value => value.SourceRevision + 1)));
            Assert.Equal(547, staleChunk.Number);
        }
        var scopes = JsonSerializer.Serialize(new[] { new IndexAffectedScope(1, record.SourceIdentityId) });
        var ordinary = await SqlSegmentedIndexStore.QueryEligible(db, scopes, 0).ToArrayAsync();
        Assert.Equal(eligible ? 1 : 0, ordinary.Length);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlPublishedPassageSelection.AcquireFenceAsync(db, default);
        await SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(db, scopes, default);
        var mapped = await SqlSegmentedIndexStore.QueryMappedRootEligibleWindow(db, 0, long.MaxValue).ToArrayAsync();
        Assert.Equal(ordinary.Select(value => (value.VectorId, value.PayloadChecksum, value.SourceIdentityId, value.OwnerSourceRevisionId, value.SourceRootId)),
            mapped.Select(value => (value.VectorId, value.PayloadChecksum, value.SourceIdentityId, value.OwnerSourceRevisionId, value.SourceRootId)));
        await transaction.RollbackAsync();
    }
}
