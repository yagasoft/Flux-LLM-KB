using FluxKnowledge.Application.Ports;
using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    [Trait("Category", "Scale")]
    public async Task Final_graph_cleanup_bounds_audit_pages_and_completes_with_production_sized_metadata_fanout()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("final-graph-scale", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("final-graph-scale-control", SourceRootState.Enabled, now);
        var operation = await AddDeletionAsync(source, now);
        await SeedFinalMetadataFanoutAsync(source, control, operation);
        var publications = await SeedPublicationQueryGraphAsync(source, control, 0);
        await SeedUnrelatedPublicationsAsync(control, publications.ForeignBranch);
        var timing = new GraphCommandTimings();
        var artifactPages = new ArtifactPageRows();
        var ownership = new GraphOwnershipParameterBudget();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString, sql => sql.CommandTimeout(30)).AddInterceptors(timing, artifactPages, ownership).Options);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var maxUnit = 0d;
        var finished = false;
        for (var pass = 0; pass < 64; pass++)
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var watch = Stopwatch.StartNew();
            var result = await store.PurgeAsync(work, null, default);
            maxUnit = Math.Max(maxUnit, watch.Elapsed.TotalMilliseconds);
            output.WriteLine($"final_graph_unit pass={pass} phase={work.Phase} elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
            if (result.Completed) { finished = true; break; }
            Assert.True(result.ContinueImmediately, result.ReasonCode);
        }
        output.WriteLine($"final_graph_max_unit_ms={maxUnit:F1} max_statement_ms={timing.MaxMs:F1} slowest={timing.Slowest}");
        Assert.True(finished);
        Assert.True(maxUnit < 30_000, $"Whole cleanup unit took {maxUnit:F1}ms.");
        Assert.True(timing.MaxMs < 15_000, $"Cleanup statement took {timing.MaxMs:F1}ms: {timing.Slowest}");
        Assert.Equal(100000, artifactPages.TotalDeleted);
        Assert.Equal(4096, artifactPages.MaxDeleted);
        await using var verify = CreateContext();
        Assert.Equal(3, (await verify.SourceDeletionOperations.SingleAsync(value => value.Id == operation)).State);
        Assert.Equal(204576, await verify.AuditEvents.CountAsync());
        Assert.True(await verify.PipelineRecords.AnyAsync(value => value.Id == control.RecordId));
        Assert.False(await verify.SourceRootConfigurations.AnyAsync(value => value.Id == source.RootId));
        Assert.False(await verify.Artifacts.AnyAsync());
        Assert.Equal(publications.ForeignActivity, (await verify.SourceActivities.SingleAsync()).Id);
        Assert.Equal(publications.ForeignBranch, (await verify.SourceProcessorBranches.SingleAsync()).Id);
        Assert.Equal(600, await verify.DocumentPublications.CountAsync());
        Assert.Equal(1, await verify.SourceIdentities.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_audit_delete_rolls_back_the_page_and_preserves_its_source_anchors()
    {
        var (source, _, _) = await SeedAuditPageGraphAsync();
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        using var cancellation = new CancellationTokenSource();
        var cancel = new AuditPageCancellation(cancellation);
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(cancel).Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlSourceDeletionStore(factory, TimeProvider.System).PurgeAsync(work, null, cancellation.Token).AsTask());
        Assert.True(cancel.Fired);
        await using (var check = CreateContext())
        {
            Assert.Equal(32018, await check.AuditEvents.CountAsync());
            Assert.True(await check.SourceRevisions.AnyAsync(value => value.Id == source.RevisionId));
        }
        Assert.Equal("purge-records", (await store.PurgeAsync(work, null, default)).Phase);
    }

    [NativeSqlServerFact]
    public async Task Audit_pages_preserve_all_five_ownership_scopes_and_anchors_until_the_final_empty_check()
    {
        var (source, control, operation) = await SeedAuditPageGraphAsync();
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var first = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var result = await store.PurgeAsync(first, null, default);
        Assert.Equal("purge-records", result.Phase);
        await using (var check = CreateContext())
        {
            Assert.Equal(16018, await check.AuditEvents.CountAsync());
            Assert.True(await check.SourceRevisions.AnyAsync(value => value.Id == source.RevisionId));
            Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id == source.RecordId));
            // A fresh event between pages must not be hidden by cached emptiness.
            check.AuditEvents.Add(new() { SourceRootId = source.RootId, EventType = "late-owned", Actor = "test", DetailsJson = "{}", OccurredAtUtc = DateTimeOffset.UtcNow });
            await check.SaveChangesAsync();
        }
        var second = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("purge-records", (await store.PurgeAsync(second, null, default)).Phase);
        await using (var check = CreateContext()) Assert.Equal(19, await check.AuditEvents.CountAsync());
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            if ((await store.PurgeAsync(work, null, default)).Completed) break;
        }
        await using var verify = CreateContext();
        Assert.Equal(3, (await verify.SourceDeletionOperations.SingleAsync(value => value.Id == operation)).State);
        Assert.False(await verify.SourceRootConfigurations.AnyAsync(value => value.Id == source.RootId));
        Assert.True(await verify.PipelineRecords.AnyAsync(value => value.Id == control.RecordId));
        var audit = Assert.Single(await verify.AuditEvents.ToArrayAsync());
        Assert.Equal(control.RootId, audit.SourceRootId);
        Assert.Equal("unrelated", audit.EventType);
    }

    [NativeSqlServerFact]
    public async Task Lost_audit_page_acknowledgement_does_not_delete_another_page_on_replay()
    {
        var (source, _, _) = await SeedAuditPageGraphAsync();
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var lossy = new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True((await lossy.PurgeAsync(work, null, default)).ContinueImmediately);
        Assert.True((await store.PurgeAsync(work, null, default)).ContinueImmediately);
        await using var check = CreateContext();
        Assert.Equal(16018, await check.AuditEvents.CountAsync());
        Assert.True(await check.SourceRevisions.AnyAsync(value => value.Id == source.RevisionId));
    }

    private async Task<(SeededRoot Source, SeededRoot Control, Guid Operation)> SeedAuditPageGraphAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("audit-page-target", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("audit-page-control", SourceRootState.Enabled, now);
        var operation = await AddDeletionAsync(source, now);
        var activity = Guid.NewGuid();
        var scan = Guid.NewGuid();
        await using var context = CreateContext();
        context.SourceActivities.Add(new() { Id = activity, SourceRevisionId = source.RevisionId, ActivityKind = 0,
            ExecutionClass = 0, ProcessorVersion = "synthetic", InputFingerprint = new string('a',64),
            State = (int)SourceActivityState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceScanRequests.Add(new() { Id = scan, SourceRootId = source.RootId,
            RequestedBy = "test", RequestedAtUtc = now, State = (int)SourceScanRequestState.Completed });
        context.AuditEvents.Add(new() { SourceRootId = control.RootId, EventType = "unrelated", Actor = "test", DetailsJson = "{}", OccurredAtUtc = now });
        await context.SourceDeletionOperations.Where(value => value.Id == operation)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Phase, "purge-records"));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT AuditEvents(SourceRootId,SourceRevisionId,PipelineRecordId,SourceActivityId,SourceScanRequestId,EventType,Actor,DetailsJson,OccurredAtUtc)
            SELECT CASE WHEN n%7 IN(0,5) THEN {source.RootId} WHEN n%7=6 THEN {control.RootId} END,
                CASE WHEN n%7 IN(1,5,6) THEN {source.RevisionId} END,
                CASE WHEN n%7 IN(2,5) THEN {source.RecordId} END,
                CASE WHEN n%7 IN(3,5) THEN {activity} END,
                CASE WHEN n%7 IN(4,5) THEN {scan} END,
                'owned','test','[]',SYSUTCDATETIME()
            FROM(SELECT TOP(32017) ROW_NUMBER() OVER(ORDER BY(SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbers;
            """);
        Assert.Equal(32018, await context.AuditEvents.CountAsync());
        return (source, control, operation);
    }

    private async Task SeedFinalMetadataFanoutAsync(SeededRoot source, SeededRoot control, Guid operation)
    {
        var scan = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        var details = "{\"data\":\"" + new string('x', 240) + "\"}";
        await using var setup = CreateContext();
        setup.SourceScanRequests.Add(new() { Id = scan, SourceRootId = source.RootId, RequestedBy = "test",
            RequestedAtUtc = now, State = (int)SourceScanRequestState.Completed });
        setup.AuditEvents.Add(new() { SourceRootId = control.RootId, EventType = "unrelated", Actor = "test", DetailsJson = "{}", OccurredAtUtc = now });
        await setup.SaveChangesAsync();
        await setup.SourceDeletionOperations.Where(value => value.Id == operation)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Phase, "purge-records"));
        await setup.Database.OpenConnectionAsync();
        // Create temporary tables in the session batch, outside parameterised
        // sp_executesql scopes, so the second seed statement can reuse them.
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE TABLE #Parents(N int NOT NULL PRIMARY KEY,RevisionId uniqueidentifier NOT NULL,RecordId uniqueidentifier NOT NULL,IdentityId uniqueidentifier NOT NULL);
            CREATE TABLE #Activities(N int NOT NULL PRIMARY KEY,Id uniqueidentifier NOT NULL,RevisionId uniqueidentifier NOT NULL);
            """);
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT #Parents SELECT TOP(35000) ROW_NUMBER()OVER(ORDER BY(SELECT NULL)),NEWID(),NEWID(),NEWID() FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,Extension,ByteLength,DiscoveredAtUtc,OriginKind)
            SELECT RevisionId,{source.RootId},CONVERT(nvarchar(36),RevisionId),1,{hash},CONCAT('C:\final-graph-scale\',RevisionId),'AcceptedUtf8Text','.txt',4,SYSUTCDATETIME(),0 FROM #Parents;
            INSERT SourceIdentities(Id,SourceKind,StableKey,CreatedAtUtc)
            SELECT IdentityId,'local file',CONVERT(nvarchar(36),IdentityId),SYSUTCDATETIME() FROM #Parents WHERE N<=20000;
            INSERT PipelineRecords(Id,SourceIdentityId,SourceRevisionId,Revision,ContentHash,RootLineageRecordId,CurrentStage,RegisteredAtUtc,CompletionCriteriaMet,IsDeleted)
            SELECT RecordId,IdentityId,RevisionId,1,{hash},RecordId,5,SYSUTCDATETIME(),1,1 FROM #Parents WHERE N<=20000;
            INSERT Jobs(Id,PipelineRecordId,SourceRevision,Stage,Operation,PublicState,DueAtUtc,AttemptCount,LeaseGeneration)
            SELECT NEWID(),parent.RecordId,1,stage.N,CONCAT('synthetic-',stage.N),4,SYSUTCDATETIME(),1,0
            FROM #Parents parent CROSS JOIN(VALUES(1),(2),(3),(4),(5))stage(N) WHERE parent.N<=20000;
            INSERT Artifacts(Id,PipelineRecordId,SourceRevision,Stage,ContentHash,ContentType,SearchText,CreatedAtUtc,WindowEligibilityVersion)
            SELECT NEWID(),parent.RecordId,1,stage.N,{hash},'text/plain',REPLICATE(CONVERT(nvarchar(max),'x'),2048),SYSUTCDATETIME(),NEWID()
            FROM #Parents parent CROSS JOIN(VALUES(1),(2),(3),(4),(5))stage(N) WHERE parent.N<=20000;
            INSERT #Activities SELECT TOP(60000) ROW_NUMBER()OVER(ORDER BY(SELECT NULL)),NEWID(),{source.RevisionId} FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE activity SET RevisionId=parent.RevisionId FROM #Activities activity JOIN #Parents parent ON parent.N=1+(activity.N%35000);
            INSERT SourceActivities(Id,SourceRevisionId,ActivityKind,ExecutionClass,ProcessorVersion,InputFingerprint,DescriptorFingerprint,State,AttemptCount,CreatedAtUtc,UpdatedAtUtc)
            SELECT Id,RevisionId,0,0,CONCAT('synthetic-',N),{hash},{hash},{(int)SourceActivityState.Completed},0,SYSUTCDATETIME(),SYSUTCDATETIME() FROM #Activities;
            INSERT SourceProcessorBranches(Id,SourceActivityId,SourceRevisionId,InputSha256,ProcessorVersion,ProcessorFingerprint,State,LeaseGeneration,AttemptCount,CompletedMemberCount,CreatedAtUtc,UpdatedAtUtc)
            SELECT NEWID(),Id,RevisionId,{hash},CONCAT('synthetic-',N),{hash},0,0,0,0,SYSUTCDATETIME(),SYSUTCDATETIME() FROM #Activities WHERE N<=15000;
            """);
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT AuditEvents(SourceRootId,SourceRevisionId,PipelineRecordId,SourceActivityId,SourceScanRequestId,EventType,Actor,DetailsJson,OccurredAtUtc)
            SELECT CASE WHEN n.N<=125715 THEN {source.RootId} WHEN n.N>450000 THEN {control.RootId} END,
                CASE WHEN n.N>125715 AND n.N<=225715 THEN revision.RevisionId END,
                CASE WHEN n.N>225715 AND n.N<=325715 THEN record.RecordId END,
                CASE WHEN n.N>325715 AND n.N<=425715 THEN activity.Id END,
                CASE WHEN n.N>425715 AND n.N<=450000 THEN {scan} END,
                'synthetic','test',{details},SYSUTCDATETIME()
            FROM(SELECT TOP(654575) ROW_NUMBER()OVER(ORDER BY(SELECT NULL)) AS N FROM sys.all_objects a CROSS JOIN sys.all_objects b)n
            JOIN #Parents revision ON revision.N=1+(n.N%35000)
            JOIN #Parents record ON record.N=1+(n.N%20000)
            JOIN #Activities activity ON activity.N=1+(n.N%60000);
            DROP TABLE #Activities;
            DROP TABLE #Parents;
            """);
        Assert.Equal(654576, await setup.AuditEvents.CountAsync());
        Assert.Equal(100000, await setup.Artifacts.CountAsync());
    }

    private sealed class AuditPageCancellation(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("source-delete-audit-page-delete", StringComparison.Ordinal))
            { Fired = true; cancellation.Cancel(); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ArtifactPageRows : DbCommandInterceptor
    {
        public int TotalDeleted { get; private set; }
        public int MaxDeleted { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("source-delete-artifact-page-delete", StringComparison.Ordinal))
            { TotalDeleted += result; MaxDeleted = Math.Max(MaxDeleted, result); }
            return ValueTask.FromResult(result);
        }
    }
}
