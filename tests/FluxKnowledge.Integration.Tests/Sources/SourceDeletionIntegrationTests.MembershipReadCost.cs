using System.Data.Common;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_protected_membership_at_the_end_of_a_full_page_refuses_every_delete(bool activeButRetired)
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("protected-full-membership-page", SourceRootState.Deleting, now);
        var operation = await AddDeletionAsync(source, now);
        var (ids, _) = await SeedDistinctMembershipVectorsAsync(source, 1000, now);
        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(store, operation, stopAtCutover: true);
        var generation = Guid.NewGuid();
        await using (var mutation = CreateContext())
        {
            mutation.IndexGenerations.Add(new()
            {
                Id=generation, ModelFingerprint="source-deletion-test:1", Dimensions=1, IndexPath="protected-full-page",
                MetadataChecksum=new string('a',64), VectorCount=1, CreatedAtUtc=now,
                RetiredAtUtc=activeButRetired ? now : null
            });
            mutation.IndexGenerationVectors.Add(new() { GenerationId=generation, VectorId=ids[^1] });
            await mutation.SaveChangesAsync();
            if (activeButRetired) await mutation.IndexState.Where(value => value.Id==1)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.ActiveIndexGenerationId,generation));
        }
        var page = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("source-delete-membership-generation-not-retired",(await store.PurgeAsync(page,null,default)).ReasonCode);
        await using var check = CreateContext();
        Assert.Equal(1001,await check.IndexGenerationVectors.CountAsync());
        Assert.Equal(1000,await check.Vectors.CountAsync());
        var current = await check.SourceDeletionOperations.SingleAsync(value => value.Id==operation);
        Assert.Equal(0,current.MembershipRowsDeleted);
        Assert.Null(current.MembershipVectorCursor);
    }

    [NativeSqlServerTheory]
    [InlineData(32,true)]
    [InlineData(1024,false)]
    public async Task Membership_page_checks_and_deletion_seek_through_generation_history(int retiredGenerations,bool drain)
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("membership-history", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("membership-history-control", SourceRootState.Enabled, now);
        var operation = await AddDeletionAsync(source, now);
        var (ids, generation) = await SeedDistinctMembershipVectorsAsync(source, 1000, now);
        var ordinary = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        await AdvanceSqlToFilesAsync(ordinary, operation, stopAtCutover: true);
        var foreign = await AddVectorAsync(control, generation, "A foreign membership survives.", now);
        await using (var seed = CreateContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT TextChunks(ArtifactId,SourceRevision,Ordinal,StartOffset,Length,Content,ContentHash,
                    PassagePolicyFingerprint,ContextHeader,SearchInputHash)
                SELECT original.ArtifactId,original.SourceRevision,n.Ordinal,original.StartOffset,original.Length,original.Content,
                    original.ContentHash,original.PassagePolicyFingerprint,original.ContextHeader,original.SearchInputHash
                FROM TextChunks original CROSS JOIN (SELECT TOP(49999) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS Ordinal
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b) n WHERE original.Id={foreign.TextChunkId};
                INSERT Vectors(TextChunkId,ModelFingerprint,Dimensions,[Values],TextChunkContentHash,PayloadChecksum,
                    SourceRevision,IsDeleted,IndexGenerationId,CreatedAtUtc,SearchInputHash)
                SELECT chunk.Id,original.ModelFingerprint,original.Dimensions,original.[Values],original.TextChunkContentHash,
                    original.PayloadChecksum,original.SourceRevision,0,original.IndexGenerationId,original.CreatedAtUtc,original.SearchInputHash
                FROM TextChunks chunk CROSS JOIN Vectors original WHERE original.VectorId={foreign.VectorId}
                    AND chunk.ArtifactId=(SELECT ArtifactId FROM TextChunks WHERE Id={foreign.TextChunkId}) AND chunk.Id<>{foreign.TextChunkId};
                CREATE TABLE #History (Id uniqueidentifier PRIMARY KEY, N int NOT NULL, Retired bit NOT NULL);
                INSERT #History SELECT TOP({retiredGenerations+32768}) NEWID(),ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),
                    CONVERT(bit,CASE WHEN ROW_NUMBER() OVER(ORDER BY (SELECT NULL))<={retiredGenerations} THEN 1 ELSE 0 END)
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT IndexGenerations(Id,FormatVersion,SegmentCount,CorpusEpoch,CorpusVersion,ModelFingerprint,Dimensions,
                    IndexPath,MetadataChecksum,VectorCount,CreatedAtUtc,ValidatedAtUtc,RetiredAtUtc)
                SELECT history.Id,original.FormatVersion,original.SegmentCount,original.CorpusEpoch,original.CorpusVersion,
                    original.ModelFingerprint,original.Dimensions,'membership-read-cost',original.MetadataChecksum,
                    CASE WHEN history.Retired=1 THEN 1000 ELSE 32 END,{now},{now},CASE WHEN history.Retired=1 THEN {now} ELSE NULL END
                FROM #History history CROSS JOIN IndexGenerations original WHERE original.Id={generation};
                INSERT IndexGenerationVectors(GenerationId,VectorId)
                SELECT history.Id,member.VectorId FROM #History history CROSS JOIN IndexGenerationVectors member
                WHERE history.Retired=1 AND member.GenerationId={generation};
                INSERT IndexGenerationVectors(GenerationId,VectorId)
                SELECT history.Id,vector.VectorId FROM #History history JOIN Vectors vector ON
                    vector.VectorId BETWEEN {foreign.VectorId}+((history.N-{retiredGenerations}-1)*127)%49968 AND {foreign.VectorId}+((history.N-{retiredGenerations}-1)*127)%49968+31
                WHERE history.Retired=0;
                UPDATE IndexState SET ActiveIndexGenerationId=(SELECT TOP(1) Id FROM #History WHERE Retired=0) WHERE Id=1;
                DROP TABLE #History;
                """);
        }
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var messages = new List<string>();
        connection.InfoMessage += (_, item) => messages.Add(item.Message);
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = "SET STATISTICS IO ON;";
            await statistics.ExecuteNonQueryAsync();
        }
        var capture = new MembershipHistoryCommands();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.CommandTimeout(10)).AddInterceptors(capture).Options);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var unitTimer=System.Diagnostics.Stopwatch.StartNew();
        Assert.True((await store.PurgeAsync(work, null, default)).ContinueImmediately);
        unitTimer.Stop();
        var reads = messages.SelectMany(message => Regex.Matches(message,
                @"Table 'IndexGenerationVectors'[^\r\n]*?(?<!lob )logical reads (\d+),"))
            .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        output.WriteLine($"membership_history page_vectors=1000 deleted_rows=16000 retired_generations={retiredGenerations+1} foreign_unretired=32768 foreign_vectors=50000 membership_reads={reads} guard_parameters={capture.MaximumGuardParameters} total_unit_ms={unitTimer.Elapsed.TotalMilliseconds:F1}");
        Assert.InRange(unitTimer.Elapsed.TotalSeconds,0,5);
        // This counter includes membership-table mutation reads for 16,000 rows.
        // The isolated generation-proof budget below remains 50,000 reads.
        Assert.InRange(reads, 1, 200_000);
        Assert.NotNull(capture.GuardSql);
        await using (var replay = factory.CreateDbContext())
        await using (var transaction = await replay.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(replay,default);
            await replay.Database.ExecuteSqlRawAsync("SET STATISTICS XML ON;");
            try
            {
                foreach(var smallFirst in new[]{true,false})
                foreach(var small in new[]{smallFirst,!smallFirst})
                {
                    messages.Clear();
                    await using var command=connection.CreateCommand();
                    command.Transaction=(SqlTransaction)transaction.GetDbTransaction();
                    command.CommandTimeout=10;
                    command.CommandText=$"-- membership-cost-cache-order-{smallFirst}\n"+capture.GuardSql;
                    foreach(var parameter in capture.GuardParameters)
                    {
                        var clone=(SqlParameter)((ICloneable)parameter).Clone();
                        if(clone.Value is string json && json.StartsWith('['))
                        {
                            clone.Value=JsonSerializer.Serialize(small?ids[..1]:ids);
                            clone.Size=-1;
                        }
                        command.Parameters.Add(clone);
                    }
                    long generationRows=0,membershipRows=0;
                    await using var reader=await command.ExecuteReaderAsync();
                    do
                    {
                        while(await reader.ReadAsync())
                        {
                            if(reader.FieldCount!=1 || reader.GetValue(0) is not string xml || !xml.StartsWith("<ShowPlanXML",StringComparison.Ordinal)) continue;
                            var plan=XDocument.Parse(xml);XNamespace ns="http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                            foreach(var access in plan.Descendants(ns+"RelOp").Where(op=>op.Elements().Any(element=>element.Element(ns+"Object") is not null)))
                            {
                                var table=(string?)access.Elements().Select(element=>element.Element(ns+"Object")).FirstOrDefault(element=>element is not null)?.Attribute("Table");
                                var count=access.Descendants(ns+"RunTimeCountersPerThread").Sum(counter=>long.Parse((string?)counter.Attribute("ActualRowsRead")??(string?)counter.Attribute("ActualRows")??"0",CultureInfo.InvariantCulture));
                                if(table=="[IndexGenerations]") generationRows+=count;
                                if(table=="[IndexGenerationVectors]") membershipRows+=count;
                            }
                        }
                    }while(await reader.NextResultAsync());
                    var totalReads=messages.SelectMany(message=>Regex.Matches(message,@"(?<!lob )logical reads (\d+),"))
                        .Sum(match=>long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture));
                    output.WriteLine($"membership_guard small_first={smallFirst} small={small} total_reads={totalReads} generation_rows={generationRows} membership_rows={membershipRows}");
                    Assert.InRange(generationRows,small?0:1,2L*(retiredGenerations+32769));
                    Assert.InRange(totalReads,1,50_000);
                }
            }
            finally{await replay.Database.ExecuteSqlRawAsync("SET STATISTICS XML OFF;");}
            await transaction.CommitAsync();
        }
        await using var check = CreateContext();
        Assert.Equal(16000, await check.SourceDeletionOperations.Where(value => value.Id == operation).Select(value => value.MembershipRowsDeleted).SingleAsync());
        Assert.Equal(1_048_576, await check.IndexGenerationVectors.CountAsync(value => value.VectorId >= foreign.VectorId));
        Assert.Equal(51_000, await check.Vectors.CountAsync());
        Assert.True(await check.PipelineRecords.AnyAsync(value => value.Id == control.RecordId));
        Assert.Equal((retiredGenerations+1)*1000-16000, await check.IndexGenerationVectors.CountAsync(value => ids.Contains(value.VectorId)));
        if(!drain) return;
        var maxUnitReads = reads;
        string phase = "cleanup-memberships";
        var units = 0;
        for (; units < 40 && phase != "cleanup-files"; units++)
        {
            messages.Clear();
            work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var result = await store.PurgeAsync(work, null, default);
            Assert.True(result.ContinueImmediately, result.ReasonCode);
            phase = result.Phase;
            var unitReads = messages.SelectMany(message => Regex.Matches(message,
                    @"Table 'IndexGenerationVectors'[^\r\n]*?(?<!lob )logical reads (\d+),"))
                .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            maxUnitReads = Math.Max(maxUnitReads, unitReads);
            Assert.InRange(unitReads, 0, 200_000);
        }
        output.WriteLine($"membership_history drained_units={units} phase={phase} maximum_unit_membership_reads={maxUnitReads}");
        Assert.Equal("cleanup-files", phase);
        Assert.Equal(1_048_576, await check.IndexGenerationVectors.CountAsync());
        Assert.Equal(50_000, await check.Vectors.CountAsync());
        Assert.False(await check.SourceRevisions.AnyAsync(value => value.SourceRootId == source.RootId));
        Assert.True(await check.SourceRevisions.AnyAsync(value => value.SourceRootId == control.RootId));
    }

    private sealed class MembershipHistoryCommands : DbCommandInterceptor
    {
        internal int MaximumGuardParameters { get; private set; }
        internal string? GuardSql {get;private set;}
        internal SqlParameter[] GuardParameters {get;private set;}=[];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[IndexGenerationVectors]", StringComparison.Ordinal) &&
                command.CommandText.Contains("RetiredAtUtc", StringComparison.Ordinal))
            {
                MaximumGuardParameters = Math.Max(MaximumGuardParameters, command.Parameters.Count);
                GuardSql=command.CommandText;
                GuardParameters=command.Parameters.Cast<SqlParameter>().Select(value=>(SqlParameter)((ICloneable)value).Clone()).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
}
