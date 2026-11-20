using System.Diagnostics;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceStatusEligibilityReadCostTests(NativeSqlServerFixture fixture,ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Large_enabled_and_deleting_roots_keep_exact_legacy_status_without_per_file_assessment_proofs()
        => await MeasureAsync(false);

    [NativeSqlServerFact]
    public async Task Stale_root_statistics_keep_source_status_accurate_without_excess_reads()
    {
        var owned=new NativeSqlServerFixture();await owned.InitializeAsync();
        try{await new SourceStatusEligibilityReadCostTests(owned,output).MeasureAsync(true);}
        finally{await owned.DisposeAsync();}
    }

    private async Task MeasureAsync(bool staleRootStatistics)
    {
        var enabled=Guid.NewGuid();var deleting=Guid.NewGuid();var unrelated=Guid.NewGuid();
        var now=DateTimeOffset.UtcNow;
        var options=new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options;
        await using(var setup=new FluxKnowledgeDbContext(options))
        {
            if(staleRootStatistics)
            {
                var bootstrap=new SourceRootConfigurationEntity
                {
                    Id=Guid.NewGuid(),CanonicalPath=$"I:\\synthetic-removed-root\\{Guid.NewGuid():N}",DisplayName="Removed statistics seed",
                    State=(int)SourceRootState.Enabled,ConfigurationRevision=1,CreatedAtUtc=now,UpdatedAtUtc=now
                };
                setup.SourceRootConfigurations.Add(bootstrap);await setup.SaveChangesAsync();
                await setup.Database.ExecuteSqlRawAsync("UPDATE STATISTICS dbo.SourceRootConfigurations PK_SourceRootConfigurations WITH FULLSCAN;");
                setup.SourceRootConfigurations.Remove(bootstrap);await setup.SaveChangesAsync();
            }
            foreach(var id in new[]{enabled,deleting,unrelated})
            {
                setup.SourceRootConfigurations.Add(new()
                {
                    Id=id,CanonicalPath=$"I:\\synthetic-status-eligibility\\{id:N}",DisplayName=id.ToString(),
                    State=(int)(id==deleting?SourceRootState.Deleting:SourceRootState.Enabled),ConfigurationRevision=1,
                    MaximumFileBytes=SourcePolicyDefaults.MaximumTextBytes,CreatedAtUtc=now,UpdatedAtUtc=now
                });
                await setup.SaveChangesAsync();
            }
            if(staleRootStatistics)
            {
                var temporary=Enumerable.Range(0,12).Select(_=>new SourceRootConfigurationEntity
                {
                    Id=Guid.NewGuid(),CanonicalPath=$"I:\\synthetic-root-statistics\\{Guid.NewGuid():N}",DisplayName="Temporary statistics history",
                    State=(int)SourceRootState.Enabled,ConfigurationRevision=1,CreatedAtUtc=now,UpdatedAtUtc=now
                }).ToArray();
                setup.SourceRootConfigurations.AddRange(temporary);await setup.SaveChangesAsync();
                setup.SourceRootConfigurations.RemoveRange(temporary);await setup.SaveChangesAsync();
                Assert.Equal(1,await RootStatisticsRowsAsync(setup));
            }
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT TOP(36000) NEWID() AS RevisionId,NEWID() AS ActivityId,
                    ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS N INTO #StatusRows FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,
                    Classification,Extension,ByteLength,DiscoveryEvidenceJson,DiscoveredAtUtc,OriginKind)
                SELECT RevisionId,CASE WHEN N<=15000 THEN {enabled} WHEN N<=30000 THEN {deleting} ELSE {unrelated} END,
                    CONCAT('status-eligibility:',N),1,{new string('a',64)},CONCAT(N'I:\synthetic-status-eligibility\',N,N'.txt'),
                    CASE WHEN N%15000 BETWEEN 1 AND 10 THEN 'Unknown' ELSE 'AcceptedUtf8Text' END,'.txt',1,{"{}"},{now},0
                FROM #StatusRows;
                INSERT SourceActivities(Id,SourceRevisionId,ActivityKind,ExecutionClass,ProcessorVersion,DescriptorFingerprint,
                    InputFingerprint,State,AttemptCount,Reason,CreatedAtUtc,UpdatedAtUtc)
                SELECT ActivityId,RevisionId,0,0,
                    CASE WHEN N%15000 BETWEEN 1 AND 10 THEN 'phase-3a-v1' ELSE {RetainedDefaultPolicy.Version} END,
                    CASE WHEN N%15000 BETWEEN 1 AND 10 THEN {SourceActivityEntity.LegacyDescriptorFingerprint} ELSE {RetainedDefaultPolicy.TextFingerprint} END,
                    {new string('a',64)},CASE WHEN N%15000 BETWEEN 1 AND 10 THEN {(int)SourceActivityState.Pending} ELSE {(int)SourceActivityState.DeferredUnsupported} END,
                    0,CASE WHEN N%15000 BETWEEN 1 AND 10 THEN NULL ELSE 'fixture-existing-policy' END,{now},{now}
                FROM #StatusRows;
                DROP TABLE #StatusRows;
                """);
        }
        await using var context=new FluxKnowledgeDbContext(options);
        await context.Database.OpenConnectionAsync();
        var before=await LogicalReadsAsync(context);
        var timer=Stopwatch.StartNew();
        var result=await SourceRootProjectionReader.ReadStateSummariesAsync(context,[enabled,deleting],default);
        timer.Stop();
        var reads=await LogicalReadsAsync(context)-before;
        output.WriteLine($"source_status current_files=30000 unrelated_files=6000 logical_reads={reads} elapsed_ms={timer.Elapsed.TotalMilliseconds:F1}");
        foreach(var id in new[]{enabled,deleting})
        {
            var summary=result[id];
            Assert.Equal(15000,summary.Known);Assert.Equal(14990,summary.Deferred);Assert.Equal(10,summary.Blocked);
            Assert.Equal(10,summary.Files.Count(value=>value.Reason==RetainedDefaultPolicy.UnadmittedLegacyTextReason));
        }
        Assert.DoesNotContain(unrelated,result.Keys);
        Assert.InRange(reads,1,150_000);
        long staleWarmReads=0;
        if(staleRootStatistics)
        {
            before=await LogicalReadsAsync(context);timer.Restart();
            var staleWarm=await SourceRootProjectionReader.ReadStateSummariesAsync(context,[enabled,deleting],default);
            timer.Stop();staleWarmReads=await LogicalReadsAsync(context)-before;
            output.WriteLine($"source_status stale_warm_logical_reads={staleWarmReads} stale_warm_elapsed_ms={timer.Elapsed.TotalMilliseconds:F1}");
            Assert.InRange(staleWarmReads,1,100_000);
            foreach (var id in new[] { enabled, deleting })
                AssertEqualFileStates(result[id].Files, staleWarm[id].Files);
            var versions=await context.SourceRootConfigurations.OrderBy(value=>value.Id).Select(value=>value.RowVersion).ToArrayAsync();
            await context.Database.ExecuteSqlRawAsync("UPDATE STATISTICS dbo.SourceRootConfigurations PK_SourceRootConfigurations WITH FULLSCAN;");
            Assert.Equal(3,await RootStatisticsRowsAsync(context));
            var after=await context.SourceRootConfigurations.OrderBy(value=>value.Id).Select(value=>value.RowVersion).ToArrayAsync();
            Assert.Equal(versions.Select(Convert.ToHexString),after.Select(Convert.ToHexString));
        }
        before=await LogicalReadsAsync(context);timer.Restart();
        var warm=await SourceRootProjectionReader.ReadStateSummariesAsync(context,[enabled,deleting],default);
        timer.Stop();
        var warmReads=await LogicalReadsAsync(context)-before;
        output.WriteLine($"source_status stale_statistics={staleRootStatistics} warm_logical_reads={warmReads} warm_elapsed_ms={timer.Elapsed.TotalMilliseconds:F1}");
        Assert.InRange(warmReads,1,100_000);
        foreach (var id in new[] { enabled, deleting })
            AssertEqualFileStates(result[id].Files, warm[id].Files);
    }

    private static void AssertEqualFileStates(
        IReadOnlyList<SourceLogicalFileState> expected,
        IReadOnlyList<SourceLogicalFileState> actual)
    {
        // Each read materialises a new reason list. Compare its contents and use record equality for the other fields.
        Assert.Equal<SourceLogicalFileState>(
            expected.OrderBy(file => file.SourceRevisionId),
            actual.OrderBy(file => file.SourceRevisionId),
            static (expectedFile, actualFile) =>
                (expectedFile with { RecordedReasons = actualFile.RecordedReasons }) == actualFile &&
                expectedFile.RecordedReasons.SequenceEqual(actualFile.RecordedReasons, StringComparer.Ordinal));
    }

    private static async Task<long> RootStatisticsRowsAsync(FluxKnowledgeDbContext context)=>
        await context.Database.SqlQueryRaw<long>("SELECT CONVERT(bigint,p.rows) AS [Value] FROM sys.stats s CROSS APPLY sys.dm_db_stats_properties(s.object_id,s.stats_id) p WHERE s.object_id=OBJECT_ID('dbo.SourceRootConfigurations') AND s.name='PK_SourceRootConfigurations'")
            .SingleAsync();

    private static async Task<long> LogicalReadsAsync(FluxKnowledgeDbContext context)
    {
        await using var command=context.Database.GetDbConnection().CreateCommand();command.CommandTimeout=5;
        command.CommandText="SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id=@@SPID";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
