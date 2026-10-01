using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class PublishedPassageSelectionPerformanceTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(512, false, false)]
    [InlineData(1024, false, true)]
    [InlineData(1024, true, true)]
    public async Task Publication_queries_resolve_chunk_identity_with_input_proportional_work_and_exact_payloads(int records, bool mostlyWithdrawn, bool retainInitialPlan)
    {
        const int chunksPerRecord = 24;
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic initial passage.");
        await environment.AddRetainedAndPumpAsync("Synthetic retained passage.");
        await using var seed = await environment.Factory.CreateDbContextAsync();
        var template = await seed.Vectors.AsNoTracking().FirstAsync();
        var retained = await seed.SourceRevisions.AsNoTracking().SingleAsync(value => value.Id ==
            seed.PipelineRecords.Where(record => record.SourceRevisionId != null).Select(record => record.SourceRevisionId).Single());
        // The generated catalogue is owned by this fixture. Each case starts with
        // clean Query Store state so a prior theory's forced plan cannot leak in.
        await seed.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET QUERY_STORE=ON (OPERATION_MODE=READ_WRITE, QUERY_CAPTURE_MODE=ALL)");
        await seed.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET QUERY_STORE CLEAR ALL");
        var expected = (await SqlPublishedPassageSelection.ReadVectorIdsAsync(seed, CancellationToken.None)).ToHashSet();
        var observer = new QueryObserver();
        await using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(observer).Options);
        await context.Database.OpenConnectionAsync();
        if (retainInitialPlan)
        {
            // This context belongs exclusively to the fixture's generated disposable catalogue.
            // Preserve an initial small-corpus plan to reproduce the observed growth regression.
            await context.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET QUERY_STORE=ON (OPERATION_MODE=READ_WRITE, QUERY_CAPTURE_MODE=ALL)");
            await SqlPublishedPassageSelection.ReadVectorIdsAsync(context, CancellationToken.None);
            var initialIdSql = observer.CommandText;
            await SqlPublishedPassageSelection.ReadVectorsAsync(context, CancellationToken.None);
            var initialVectorSql = observer.CommandText;
            await context.Database.ExecuteSqlRawAsync("EXEC sys.sp_query_store_flush_db");
            await RetainPlanAsync(context, initialIdSql);
            await RetainPlanAsync(context, initialVectorSql);
        }
        var chunks = new List<(TextChunkEntity Chunk, bool Eligible)>();
        var now = DateTimeOffset.UtcNow;
        var contentHash = new string('a', 64);
        for (var index = 0; index < records; index++)
        {
            var id = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var completed = index % 10 < 6;
            var eligible = completed && (!mostlyWithdrawn || index % 10 == 0);
            seed.SourceIdentities.Add(new SourceIdentityEntity { Id = sourceId, SourceKind = "synthetic-performance", StableKey = sourceId.ToString("N"), CreatedAtUtc = now });
            seed.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = revisionId, SourceRootId = retained.SourceRootId, StableSourceIdentity = "synthetic:" + revisionId.ToString("N"),
                Revision = 1, ContentSha256 = contentHash, CanonicalPath = "C:\\synthetic-publication\\" + revisionId.ToString("N") + ".cs",
                Classification = "AcceptedUtf8Text", Extension = ".cs", ByteLength = 24, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}",
                SuppressedAtUtc = eligible ? null : now
            });
            seed.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = id, SourceIdentityId = sourceId, SourceRevisionId = index % 20 == 0 ? null : revisionId,
                Revision = 1, ContentHash = contentHash, RootLineageRecordId = id, RegisteredAtUtc = now,
                CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = completed, IsDeleted = !eligible
            });
            seed.Artifacts.Add(new ArtifactEntity
            {
                Id = artifactId, PipelineRecordId = id, SourceRevision = 1, Stage = (int)PipelineStage.CanonicalIndex,
                ContentHash = contentHash, ContentType = "text/plain", SearchText = "Synthetic passage.", CreatedAtUtc = now
            });
            foreach (var stage in new[] { PipelineStage.Extract, PipelineStage.Normalise, PipelineStage.Embed })
                seed.Artifacts.Add(new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = id, SourceRevision = 1,
                    Stage = (int)stage, ContentHash = contentHash, ContentType = "text/plain", SearchText = "Synthetic noncanonical stage.", CreatedAtUtc = now });
            for (var ordinal = 0; ordinal < chunksPerRecord; ordinal++)
            {
                var text = $"Synthetic passage for record {index}, ordinal {ordinal}.";
                var chunkHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                var chunk = new TextChunkEntity
                {
                    ArtifactId = artifactId, SourceRevision = 1, Ordinal = ordinal, Content = text,
                    ContentHash = chunkHash, Length = text.Length, PassagePolicyFingerprint = "synthetic-policy", SearchInputHash = chunkHash
                };
                seed.TextChunks.Add(chunk);
                chunks.Add((chunk, eligible));
            }
        }
        await seed.SaveChangesAsync();
        var addedVectors = chunks.Select(value => (Vector: new VectorEntity
        {
            TextChunkId = value.Chunk.Id, ModelFingerprint = template.ModelFingerprint, Dimensions = template.Dimensions,
            Values = template.Values, TextChunkContentHash = value.Chunk.ContentHash, PayloadChecksum = template.PayloadChecksum,
            SearchInputHash = value.Chunk.SearchInputHash, SourceRevision = 1, IndexGenerationId = template.IndexGenerationId, CreatedAtUtc = now
        }, value.Eligible)).ToArray();
        seed.Vectors.AddRange(addedVectors.Select(value => value.Vector));
        await seed.SaveChangesAsync();
        foreach (var value in addedVectors.Where(value => value.Eligible)) expected.Add(value.Vector.VectorId);

        var connection = (SqlConnection)context.Database.GetDbConnection();
        long reads = 0;
        connection.InfoMessage += (_, message) =>
        {
            foreach (Match match in Regex.Matches(message.Message, @"logical reads (\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                reads += long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        };
        await context.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON");
        var stopwatch = Stopwatch.StartNew();
        var ids = await SqlPublishedPassageSelection.ReadVectorIdsAsync(context, CancellationToken.None);
        var idSql = observer.CommandText;
        output.WriteLine($"records={records}; vectors={addedVectors.Length}; withdrawn={mostlyWithdrawn}; projection=ids; elapsed_ms={stopwatch.ElapsedMilliseconds}");
        Assert.Equal(expected.Order(), ids);
        reads = 0;
        stopwatch.Restart();
        var vectors = await SqlPublishedPassageSelection.ReadVectorsAsync(context, CancellationToken.None);
        var vectorSql = observer.CommandText;
        output.WriteLine($"records={records}; vectors={addedVectors.Length}; withdrawn={mostlyWithdrawn}; projection=payload; elapsed_ms={stopwatch.ElapsedMilliseconds}");
        Assert.Equal(expected.Order(), vectors.Select(vector => vector.VectorId));
        var addedById = addedVectors.ToDictionary(value => value.Vector.VectorId, value => value.Vector);
        Assert.All(vectors.Where(vector => addedById.ContainsKey(vector.VectorId)), vector =>
        {
            Assert.Equal(template.Values, vector.Values);
            Assert.Equal(template.ModelFingerprint, vector.ModelFingerprint);
            Assert.Equal(template.Dimensions, vector.Dimensions);
            Assert.Equal(template.PayloadChecksum, vector.PayloadChecksum);
            Assert.Equal(addedById[vector.VectorId].TextChunkContentHash, vector.TextChunkContentHash);
            Assert.Equal(1, vector.SourceRevision);
        });
        await context.Database.ExecuteSqlRawAsync("SET STATISTICS IO OFF");
        // Consume every ADO result, including informational messages and actual plans.
        // EF disposes its first-result reader without exposing the later statistics.
        reads = 0;
        await AssertChunkAccessAsync(connection, idSql, expected.Count, addedVectors.Length);
        var idReads = reads;
        reads = 0;
        await AssertChunkAccessAsync(connection, vectorSql, expected.Count, addedVectors.Length);
        var vectorReads = reads;
        output.WriteLine($"records={records}; vectors={addedVectors.Length}; withdrawn={mostlyWithdrawn}; id_reads={idReads}; payload_reads={vectorReads}");
        // A proportional test budget, not a product request/file cap. It tolerates index
        // point lookups while rejecting the captured record-by-vector pair expansion.
        var readBudget = 96L * addedVectors.Length + 128L * records;
        Assert.True(idReads > 0 && vectorReads > 0, "SQL I/O instrumentation did not report either projection's logical reads.");
        Assert.True(idReads < readBudget, $"ID projection used {idReads} logical reads for {records} records/{addedVectors.Length} vectors; budget {readBudget}.");
        Assert.True(vectorReads < readBudget, $"Payload projection used {vectorReads} logical reads for {records} records/{addedVectors.Length} vectors; budget {readBudget}.");
    }

    private static async Task RetainPlanAsync(FluxKnowledgeDbContext context, string sql)
    {
        var plan = await context.Database.SqlQuery<PlanIdentity>($"""
            SELECT TOP(1) p.query_id AS QueryId, p.plan_id AS PlanId
            FROM sys.query_store_plan p
            INNER JOIN sys.query_store_query q ON q.query_id=p.query_id
            INNER JOIN sys.query_store_query_text t ON t.query_text_id=q.query_text_id
            WHERE LTRIM(RTRIM(t.query_sql_text)) IN ({sql.Trim()}, {sql.Trim() + ";"})
            ORDER BY p.plan_id
            """).FirstAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_query_store_force_plan @query_id={plan.QueryId}, @plan_id={plan.PlanId}");
    }

    private sealed class PlanIdentity
    {
        public long QueryId { get; init; }
        public long PlanId { get; init; }
    }

    private async Task AssertChunkAccessAsync(SqlConnection connection, string sql, int expectedRows, int inputVectors)
    {
        await using var command = new SqlCommand("SET STATISTICS IO ON; SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF; SET STATISTICS IO OFF;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        XDocument? plan = null;
        var rows = 0;
        do
        {
            if (reader.FieldCount == 1 && reader.GetName(0) == "Microsoft SQL Server 2005 XML Showplan")
            {
                Assert.True(await reader.ReadAsync());
                plan = XDocument.Parse(reader.GetString(0));
            }
            else { while (await reader.ReadAsync()) rows++; }
        } while (await reader.NextResultAsync());
        Assert.Equal(expectedRows, rows);
        Assert.NotNull(plan);
        AssertNoRevisionPairExpansion(plan);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var accesses = plan.Descendants(ns + "RelOp").Where(node =>
            node.Element(ns + "IndexScan")?.Element(ns + "Object")?.Attribute("Table")?.Value == "[TextChunks]");
        var executions = accesses.Sum(node => node.Descendants(ns + "RunTimeCountersPerThread")
            .Sum(counter => (long?)counter.Attribute("ActualExecutions") ?? 0));
        output.WriteLine($"actual_chunk_access_executions={executions}; input_vectors={inputVectors}");
        Assert.True(executions <= 2L * inputVectors + 16, $"Chunk access executed {executions} times for {inputVectors} vectors.");
        output.WriteLine(plan.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Plan_guard_rejects_the_observed_revision_pair_expansion_before_chunk_identity()
    {
        // Sanitised shape of production plans 1243/1244. No production IDs/text.
        var bad = XDocument.Parse("""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><RelOp PhysicalOp="Nested Loops"><NestedLoops>
              <RelOp PhysicalOp="Hash Match"><Hash>
                <RelOp><IndexScan><Object Table="[PipelineRecords]" /></IndexScan></RelOp>
                <RelOp><IndexScan><Object Table="[Vectors]" /></IndexScan></RelOp>
              </Hash></RelOp>
              <RelOp><IndexScan><Object Table="[TextChunks]" /></IndexScan></RelOp>
            </NestedLoops></RelOp></ShowPlanXML>
            """);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoRevisionPairExpansion(bad));
    }

    private static void AssertNoRevisionPairExpansion(XDocument plan)
    {
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        foreach (var operation in plan.Descendants(ns + "RelOp"))
        {
            var tables = operation.Descendants(ns + "Object").Select(value => (string?)value.Attribute("Table")).ToHashSet();
            Assert.False(tables.Contains("[Vectors]") && tables.Contains("[PipelineRecords]") && !tables.Contains("[TextChunks]"),
                "The plan combines records and vectors before resolving vector-to-chunk identity, allowing revision-pair expansion.");
        }
    }

    private sealed class QueryObserver : DbCommandInterceptor
    {
        public string CommandText { get; private set; } = "";
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [Vectors] AS [vector]", StringComparison.Ordinal))
            {
                Assert.Empty(command.Parameters);
                CommandText = command.CommandText;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
