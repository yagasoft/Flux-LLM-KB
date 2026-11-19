using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_batch_or_active_capacity_refuses_before_the_first_graph_page(bool activeCapacity)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("graph-batch",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("graph-batch-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,4097,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        for(var pass=0;pass<6;pass++)
        {
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            if((await store.PurgeAsync(work,null,default)).Phase=="purge-records") break;
        }
        await using(var mutation=CreateContext())
        {
            var batch=Guid.NewGuid();var owned=Guid.NewGuid();
            mutation.GpuCapacitySlots.Add(new GpuCapacitySlotEntity {SlotKey="graph-proof",UpdatedAtUtc=now});
            mutation.GpuBatches.Add(new GpuBatchEntity {Id=batch,CapacitySlotKey="graph-proof",ModelRuntimeKey="fixture",SettingsFingerprint="fixture",
                OwnerKey="fixture",ItemCount=1,EstimatedBytes=1,State=2,CreatedAtUtc=now,UpdatedAtUtc=now});
            AddLocalOcrTask(mutation,source,owned,FluxKnowledge.Domain.Gpu.GpuMiniTaskExecutionState.Completed,now);
            mutation.GpuMiniTasks.Local.Single(value => value.Id==owned).BatchId=batch;
            if(!activeCapacity)
            {
                var foreign=Guid.NewGuid();
                AddLocalOcrTask(mutation,control,foreign,FluxKnowledge.Domain.Gpu.GpuMiniTaskExecutionState.Completed,now);
                mutation.GpuMiniTasks.Local.Single(value => value.Id==foreign).BatchId=batch;
            }
            await mutation.SaveChangesAsync();
            if(activeCapacity) await mutation.GpuCapacitySlots.ExecuteUpdateAsync(set => set.SetProperty(value => value.ActiveBatchId,batch));
        }
        var page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurgeAsync(page,null,default).AsTask());
        Assert.Equal(activeCapacity?"source-delete-local-ocr-active-batch":"source-delete-local-ocr-shared-batch",error.Message);
        await using var check=CreateContext();
        Assert.Equal(4097,await check.Vectors.CountAsync());Assert.Equal(4097,await check.TextChunks.CountAsync());
        Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id==source.RecordId));
        Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id==control.RecordId));
    }

    [NativeSqlServerFact]
    public async Task Withdrawal_refuses_a_vector_reintroduced_behind_the_committed_cursor()
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("withdraw-behind",SourceRootState.Deleting,now);
        await AddDeletionAsync(source,now);
        var (ids,_)=await SeedDistinctMembershipVectorsAsync(source,4097,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        var page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("accepted",(await store.PurgeAsync(page,null,default)).Phase);
        await using(var mutation=CreateContext())
            await mutation.Vectors.Where(value => value.VectorId==ids[0]).ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted,false));
        page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("source-delete-withdrawal-changed",(await store.PurgeAsync(page,null,default)).ReasonCode);
        await using var check=CreateContext();
        Assert.Equal(4097,await check.Vectors.CountAsync());
        Assert.Equal(4097,await check.IndexGenerationVectors.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task An_unretired_checkpoint_refuses_graph_paging_before_any_vector_or_chunk_is_removed()
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("graph-checkpoint",SourceRootState.Deleting,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,4097,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        for(var pass=0;pass<6;pass++)
        {
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            if((await store.PurgeAsync(work,null,default)).Phase=="purge-records") break;
        }
        await using(var mutation=CreateContext())
        {
            mutation.IndexGenerations.Add(new IndexGenerationEntity {Id=Guid.NewGuid(),EmbeddingJobId=source.JobId,
                ModelFingerprint="source-deletion-test:1",Dimensions=1,IndexPath="",MetadataChecksum=new string('a',64),CreatedAtUtc=now});
            await mutation.SaveChangesAsync();
        }
        var page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurgeAsync(page,null,default).AsTask());
        Assert.Equal("source-delete-checkpoint-not-retired",error.Message);
        await using var check=CreateContext();
        Assert.Equal(4097,await check.Vectors.CountAsync());
        Assert.Equal(4097,await check.TextChunks.CountAsync());
        Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id==source.RecordId));
    }

    [NativeSqlServerFact]
    public async Task Graph_purge_removes_bounded_vector_then_chunk_pages_and_replays_the_same_receipt()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("graph-pages",SourceRootState.Deleting,now);
        var control = await SeedRootAsync("graph-keeper",SourceRootState.Enabled,now);
        var operation = await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,32017,now);
        await using(var seed=CreateContext())
        {
            var chunk=await seed.TextChunks.Include(value => value.Artifact).OrderBy(value => value.Id).FirstAsync();
            seed.TextChunkWindowEligibilities.Add(CanonicalWindowEligibility.Bind(chunk,chunk.Artifact,now));
            await seed.SaveChangesAsync();
        }
        var withdrawalTimings=new GraphCommandTimings();
        var store = new SqlSourceDeletionStore(new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(30))
            .AddInterceptors(withdrawalTimings).Options),TimeProvider.System);
        try { await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true); }
        finally { output.WriteLine($"withdrawal max_statement_ms={withdrawalTimings.MaxMs:F1} command_ms={withdrawalTimings.TotalMs:F1} update_ms={withdrawalTimings.WithdrawalUpdateMaxMs:F1} capture_ms={withdrawalTimings.GenerationCaptureMaxMs:F1} capture_sql={withdrawalTimings.GenerationCaptureSql} update_sql={withdrawalTimings.WithdrawalUpdateSql} failed_sql={withdrawalTimings.FailedSql}"); }
        Assert.True(withdrawalTimings.GenerationCaptureMaxMs < 15_000, $"Generation target capture took {withdrawalTimings.GenerationCaptureMaxMs:F1}ms.");
        for (var pass=0;pass<36;pass++)
        {
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            if ((await store.PurgeAsync(work,null,default)).Phase=="purge-records") break;
        }
        var page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("purge-records",page.Phase);
        var writer=new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString),TimeProvider.System);
        Assert.Equal("purge-records",(await writer.PurgeAsync(page,null,default)).Phase);
        Assert.Equal("purge-records",(await store.PurgeAsync(page,null,default)).Phase);
        await using (var first=CreateContext())
        {
            Assert.Equal(16017,await first.Vectors.CountAsync());
            Assert.Equal(32017,await first.TextChunks.CountAsync());
            Assert.True(await first.PipelineRecords.AnyAsync(value => value.Id==source.RecordId));
            Assert.True(await first.PipelineRecords.AnyAsync(value => value.Id==control.RecordId));
        }
        var lastVectors=16017;var lastChunks=32017;
        for (var pass=0;pass<12;pass++)
        {
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var result=await new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System).PurgeAsync(work,null,default);
            await using var check=CreateContext();
            var vectors=await check.Vectors.CountAsync();var chunks=await check.TextChunks.CountAsync();
            Assert.InRange(lastVectors-vectors,0,16000);Assert.InRange(lastChunks-chunks,0,16000);
            if (lastVectors>0) Assert.Equal(lastChunks,chunks);
            lastVectors=vectors;lastChunks=chunks;
            if (result.Phase=="cleanup-files") break;
        }
        Assert.Equal(0,lastVectors);Assert.Equal(0,lastChunks);
        await using var final=CreateContext();
        Assert.Empty(await final.TextChunkWindowEligibilities.ToArrayAsync());
        Assert.False(await final.PipelineRecords.AnyAsync(value => value.Id==source.RecordId));
        Assert.True(await final.PipelineRecords.AnyAsync(value => value.Id==control.RecordId));
    }

    [NativeSqlServerFact]
    public async Task Initial_withdrawal_commits_bounded_pages_and_replays_without_withdrawing_another_page()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("withdraw-pages", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("withdraw-control", SourceRootState.Enabled, now);
        var operationId = await AddDeletionAsync(source, now);
        var (ids, _) = await SeedDistinctMembershipVectorsAsync(source, 10001, now);
        var (foreign, _) = await SeedMembershipsAsync(control, 1, now);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        for (var batch = 0; batch < 3; batch++)
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var writer = batch == 0 ? new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString), TimeProvider.System) : store;
            var result = await writer.PurgeAsync(work, null, default);
            Assert.Equal(batch < 2 ? "accepted" : "rebuild-index", result.Phase);
            if (batch == 0) Assert.Equal("accepted", (await store.PurgeAsync(work, null, default)).Phase);
            await using var check = CreateContext();
            Assert.Equal(Math.Min((batch+1)*4096,ids.Length), await check.Vectors.CountAsync(value => ids.Contains(value.VectorId) && value.IsDeleted));
            Assert.False(await check.Vectors.Where(value => value.VectorId == foreign.VectorId).Select(value => value.IsDeleted).SingleAsync());
            Assert.False(await check.PipelineRecords.Where(value => value.Id == control.RecordId).Select(value => value.IsDeleted).SingleAsync());
            Assert.Equal(10002, await check.IndexGenerationVectors.CountAsync());
            Assert.Equal(0, await check.SourceDeletionOperations.Where(value => value.Id == operationId).Select(value => value.MembershipRowsDeleted).SingleAsync());
            Assert.Null(await check.SourceDeletionOperations.Where(value => value.Id == operationId).Select(value => value.MembershipVectorCursor).SingleAsync());
        }
    }

    [NativeSqlServerFact]
    public async Task Membership_pages_clear_2501_distinct_vectors_in_three_batches_and_replay_a_lost_commit()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("membership-page", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var (ids, _) = await SeedDistinctMembershipVectorsAsync(source, 2501, now);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId, stopAtCutover: true);
        for (var batch = 0; batch < 3; batch++)
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var writer = batch == 0 ? new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString), TimeProvider.System) : store;
            Assert.True((await writer.PurgeAsync(work, null, default)).ContinueImmediately);
            if (batch == 0) Assert.True((await store.PurgeAsync(work, null, default)).ContinueImmediately);
            await using var check = CreateContext();
            var deleted = Math.Min((batch + 1) * 1000, ids.Length);
            Assert.Equal(ids.Length - deleted, await check.IndexGenerationVectors.CountAsync());
            Assert.Equal(deleted, await check.SourceDeletionOperations.Select(value => value.MembershipRowsDeleted).SingleAsync());
            Assert.Equal(ids[deleted - 1], await check.SourceDeletionOperations.Select(value => value.MembershipVectorCursor).SingleAsync());
            Assert.Equal(ids.Length, await check.Vectors.CountAsync());
        }
    }

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(16250)]
    public async Task Membership_page_cursor_advances_only_through_the_fully_cleared_prefix(int firstFanout)
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("membership-prefix", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var (ids, generation) = await SeedDistinctMembershipVectorsAsync(source, 3, now);
        await using (var seed = CreateContext())
        {
            if (firstFanout == 0)
                await seed.IndexGenerationVectors.Where(value => value.VectorId == ids[0]).ExecuteDeleteAsync();
            for (var index = 1; index < firstFanout; index++)
            {
                var id = Guid.NewGuid();
                seed.IndexGenerations.Add(new IndexGenerationEntity { Id = id, ModelFingerprint = "source-deletion-test:1", Dimensions = 1,
                    IndexPath = "membership-prefix", MetadataChecksum = new string('a', 64), VectorCount = 1, CreatedAtUtc = now, ValidatedAtUtc = now });
                seed.IndexGenerationVectors.Add(new IndexGenerationVectorEntity { GenerationId = id, VectorId = ids[0] });
            }
            await seed.SaveChangesAsync();
        }
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId, stopAtCutover: true);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        await store.PurgeAsync(work, null, default);
        await using (var first = CreateContext())
        {
            var operation = await first.SourceDeletionOperations.SingleAsync();
            Assert.Equal(firstFanout == 0 ? 2 : 16000, operation.MembershipRowsDeleted);
            Assert.Equal(firstFanout == 0 ? ids[^1] : (long?)null, operation.MembershipVectorCursor);
        }
        if (firstFanout > 0)
        {
            work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            await store.PurgeAsync(work, null, default);
            await using var final = CreateContext();
            Assert.Empty(await final.IndexGenerationVectors.ToArrayAsync());
            Assert.Equal(ids[^1], await final.SourceDeletionOperations.Select(value => value.MembershipVectorCursor).SingleAsync());
            Assert.Equal(firstFanout + 2, await final.SourceDeletionOperations.Select(value => value.MembershipRowsDeleted).SingleAsync());
        }
        else
        {
            // A membership reintroduced behind the cursor must prevent graph purge.
            await using (var late = CreateContext())
            {
                late.IndexGenerationVectors.Add(new IndexGenerationVectorEntity { GenerationId = generation, VectorId = ids[0] });
                await late.SaveChangesAsync();
            }
            work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Equal("source-delete-membership-changed", (await store.PurgeAsync(work, null, default)).ReasonCode);
        }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_protected_later_vector_refuses_the_entire_membership_page_without_deleting_earlier_rows(bool notRetired)
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("membership-protected-later", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var (ids, _) = await SeedDistinctMembershipVectorsAsync(source, 3, now);
        var lastId = ids[^1];
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operationId, stopAtCutover: true);
        await using (var change = CreateContext())
        {
            if (!notRetired) await change.Vectors.Where(value => value.VectorId == lastId).ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, false));
            else
            {
                var generation = Guid.NewGuid();
                change.IndexGenerations.Add(new IndexGenerationEntity { Id = generation, ModelFingerprint = "source-deletion-test:1", Dimensions = 1,
                    IndexPath = "protected-later", MetadataChecksum = new string('a', 64), VectorCount = 1, CreatedAtUtc = now, ValidatedAtUtc = now });
                change.IndexGenerationVectors.Add(new IndexGenerationVectorEntity { GenerationId = generation, VectorId = ids[^1] });
                await change.SaveChangesAsync();
            }
        }
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var result = await store.PurgeAsync(work, null, default);
        Assert.Equal(notRetired ? "source-delete-membership-generation-not-retired" : "source-delete-vector-not-withdrawn", result.ReasonCode);
        await using var check = CreateContext();
        Assert.Equal(notRetired ? 4 : 3, await check.IndexGenerationVectors.CountAsync());
        Assert.Equal(0, await check.SourceDeletionOperations.Select(value => value.MembershipRowsDeleted).SingleAsync());
        Assert.Null(await check.SourceDeletionOperations.Select(value => value.MembershipVectorCursor).SingleAsync());
    }

    private async Task<(long[] Ids, Guid Generation)> SeedDistinctMembershipVectorsAsync(SeededRoot source, int count, DateTimeOffset now)
    {
        var (prototype, generations) = await SeedMembershipsAsync(source, 1, now);
        await using var seed = CreateContext();
        if (count>10_000) seed.Database.SetCommandTimeout(300);
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT TextChunks (ArtifactId,SourceRevision,Ordinal,StartOffset,Length,Content,ContentHash,
                PassagePolicyFingerprint,ContextHeader,SearchInputHash)
            SELECT original.ArtifactId,original.SourceRevision,n.[ordinal],original.StartOffset,original.Length,original.Content,
                original.ContentHash,original.PassagePolicyFingerprint,original.ContextHeader,original.SearchInputHash
            FROM TextChunks original CROSS JOIN (SELECT TOP({count - 1}) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS [ordinal]
                FROM sys.all_objects a CROSS JOIN sys.all_objects b) n WHERE original.Id={prototype.TextChunkId};
            INSERT Vectors (TextChunkId,ModelFingerprint,Dimensions,[Values],TextChunkContentHash,PayloadChecksum,
                SourceRevision,IsDeleted,IndexGenerationId,CreatedAtUtc,SearchInputHash)
            SELECT chunk.Id,original.ModelFingerprint,original.Dimensions,original.[Values],original.TextChunkContentHash,
                original.PayloadChecksum,original.SourceRevision,0,original.IndexGenerationId,original.CreatedAtUtc,original.SearchInputHash
            FROM TextChunks chunk CROSS JOIN Vectors original
            WHERE original.VectorId={prototype.VectorId} AND chunk.ArtifactId=(SELECT ArtifactId FROM TextChunks WHERE Id={prototype.TextChunkId})
                AND chunk.Id<>{prototype.TextChunkId};
            INSERT IndexGenerationVectors (GenerationId,VectorId)
            SELECT {generations[0]},vector.VectorId FROM Vectors vector
            INNER JOIN TextChunks chunk ON chunk.Id=vector.TextChunkId
            WHERE chunk.ArtifactId=(SELECT ArtifactId FROM TextChunks WHERE Id={prototype.TextChunkId}) AND vector.VectorId<>{prototype.VectorId};
            UPDATE IndexGenerations SET VectorCount={count} WHERE Id={generations[0]};
            """);
        return (await seed.Vectors.OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync(), generations[0]);
    }
}
