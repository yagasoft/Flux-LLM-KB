using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.NativeV1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed partial class CorpusRetrievalEndToEndTests
{
    [SqlServerScaleTheory]
    [Trait("Category", "Scale")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_formats_preserve_frozen_ANN_and_cited_retrieval(bool segmented)
    {
        const int corpusSize = 12_000;
        const int queryCount = 24;
        const int neighbours = 10;
        var passages = Enumerable.Range(0, corpusSize)
            .Select(index => $"entry{index:X5} clue{unchecked((uint)index * 2654435761u):X8}.").ToArray();
        var queries = Enumerable.Range(0, queryCount)
            .Select(index => passages[index * 479].TrimEnd('.')).ToArray();
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(
            Fixture, "Baseline marker.", new PassageBuilder(new WordTokenizer(), new PassagePolicy(2, 2, 128, 0, 0)),
            segmented: segmented, segmentMaximumVectors: 2_048);
        var record = await pipeline.AddRetainedAndPumpAsync(string.Join('\n', passages));
        await using var db = await pipeline.Factory.CreateDbContextAsync();
        var previous = await MembershipAsync(db);
        Output.WriteLine($"format={(segmented ? 2 : 1)} baseline={previous}");
        Assert.Equal(corpusSize + 1, (await pipeline.ActiveGenerationAsync()).VectorCount);

        for (var update = 1; update <= 3; update++)
        {
            var started = Stopwatch.GetTimestamp();
            await pipeline.AddAndPumpAtPathAsync($"increment{update} update{update}.", $"update-{update}.txt");
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var generation = await pipeline.ActiveGenerationAsync();
            var current = await MembershipAsync(db);
            var inserted = (current.Legacy - previous.Legacy, current.Vectors - previous.Vectors,
                current.Links - previous.Links, current.Active - previous.Active);
            Output.WriteLine($"update={update} vectors={generation.VectorCount} segments={generation.SegmentCount} elapsed_ms={elapsed:F3} totals={current} inserted={inserted}");
            Assert.Equal(corpusSize + update + 1, generation.VectorCount);
            Assert.Equal(segmented ? (0L, 1L, (long)generation.SegmentCount, 1L) : ((long)generation.VectorCount, 0L, 0L, 0L), inserted);
            previous = current;
        }

        // The fixture uses deterministic embeddings. Bind their recorded input exactly
        // as in the smaller HTTP retrieval fixture before measuring the serving path.
        await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        await WaitForFullTextAsync(pipeline.Factory);
        var vectors = await (from vector in db.Vectors.AsNoTracking()
            join chunk in db.TextChunks on vector.TextChunkId equals chunk.Id
            orderby chunk.Content
            select new { vector.VectorId, vector.Values, chunk.Content }).ToArrayAsync();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var exact = vectors.Select(row =>
        {
            digest.AppendData(Encoding.UTF8.GetBytes(row.Content));
            digest.AppendData(row.Values);
            var values = new float[row.Values.Length / sizeof(float)];
            Buffer.BlockCopy(row.Values, 0, values, 0, row.Values.Length);
            return (row.VectorId, Values: values);
        }).ToArray();
        Output.WriteLine($"vectors={vectors.Length} content_and_vector_sha256={Convert.ToHexString(digest.GetHashAndReset())}");

        using var scope = pipeline.Services.CreateScope();
        var native = scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>();
        var ann = new ObservedAnnFactory(native);
        var cache = segmented ? pipeline.Services.GetRequiredService<UsearchSegmentCache>() : null;
        var leases = new SqlCorpusGenerationLeaseStore(pipeline.Factory, TimeProvider.System);
        var owner = new WindowsInteractiveGpuOwnerProbe();
        var disclosure = new LocalPrivateContentDisclosure();
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var (reader, service) = CreateRetrieval();

        var cold = new List<double>();
        var warm = new List<double>();
        var recall = new List<double>();
        var topOne = 0;
        var citations = 0;
        string? savedEvidence = null;
        await using (var app = await StartPublicationComparisonHostAsync(service))
        using (var client = app.GetTestClient())
        {
            foreach (var query in queries)
            {
                if (cache is not null)
                    await cache.SweepAsync(forceIdle: true, CancellationToken.None);
                var started = Stopwatch.GetTimestamp();
                if (segmented)
                    await PrepareAnnAsync(pipeline.Factory, ann, reader);
                var coldHit = await SearchComparisonAsync(client, ann, query);
                cold.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                started = Stopwatch.GetTimestamp();
                var hit = await SearchComparisonAsync(client, ann, query);
                warm.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Assert.Equal(coldHit.Passage, hit.Passage);
                if (string.Equals(hit.Passage.Trim(), query + ".", StringComparison.Ordinal))
                    topOne++;
                savedEvidence ??= hit.Evidence;
                await AssertComparisonCitationAsync(client, hit);
                citations++;

                var vector = (await pipeline.Embeddings.CreateEmbeddingAsync(query, CancellationToken.None)).Values;
                var similarities = exact.ToDictionary(row => row.VectorId, row => Dot(row.Values, vector));
                var cutoff = similarities.Values.OrderDescending().ElementAt(neighbours - 1);
                var lease = await leases.TryAcquireReadableAsync(Guid.NewGuid(), owner.Current,
                    DeterministicTokenHashEmbeddingProvider.Fingerprint, vector.Count, CancellationToken.None);
                Assert.NotNull(lease);
                await using var search = await native.OpenAsync(lease, CancellationToken.None);
                var hits = await search.SearchAsync(vector, neighbours, CancellationToken.None);
                Assert.Equal(neighbours, hits.Count);
                Assert.Equal(neighbours, hits.Select(result => result.VectorId).Distinct().Count());
                // Ties across the exact cutoff are interchangeable.
                recall.Add(hits.Count(result => similarities[result.VectorId] >= cutoff - 1e-6) / (double)neighbours);
            }
        }

        Output.WriteLine($"cold_prepare_and_rest_ms=[{string.Join(',', cold.Select(value => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}]");
        Output.WriteLine($"warm_rest_ms=[{string.Join(',', warm.Select(value => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}]");
        Output.WriteLine($"cold_p50_ms={Percentile(cold, 0.50):F3} cold_p95_ms={Percentile(cold, 0.95):F3} warm_p50_ms={Percentile(warm, 0.50):F3} warm_p95_ms={Percentile(warm, 0.95):F3} ann_recall_at_10={recall.Average():F4} rest_top1={topOne}/{queryCount} citation_reads={citations} native_calls={ann.SearchCalls}");
        Assert.Equal(segmented ? 2 : 1, ann.FormatVersion);
        Assert.Equal(queryCount, topOne);
        Assert.True(recall.Average() >= 0.95, $"Mean exact ANN recall@10 was {recall.Average():P2}.");

        Assert.NotNull(savedEvidence);
        if (cache is not null)
            await cache.SweepAsync(forceIdle: true, CancellationToken.None);
        var (restartedReader, restartedService) = CreateRetrieval();
        if (segmented)
            await PrepareAnnAsync(pipeline.Factory, ann, restartedReader);
        await using var restarted = await StartPublicationComparisonHostAsync(restartedService);
        using var restartedClient = restarted.GetTestClient();
        var afterRestart = await SearchComparisonAsync(restartedClient, ann, queries[0]);
        Assert.Equal(queries[0] + ".", afterRestart.Passage.Trim());
        await AssertComparisonCitationAsync(restartedClient, (afterRestart.Passage, savedEvidence));
        await db.PipelineRecords.Where(value => value.Id == record)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.IsDeleted, true));
        using var stale = await restartedClient.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = savedEvidence, context_characters = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleJson = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("evidence-stale", staleJson.RootElement.GetProperty("reasonCode").GetString());
        Assert.False(await db.CorpusQueryLeases.AnyAsync());
        Output.WriteLine("host_restart=passed persisted_native_reopen=passed prior_citation_read=passed stale_citation=409/evidence-stale active_query_leases=0");

        (SqlCorpusRetrievalReader Reader, CorpusRetrievalService Service) CreateRetrieval()
        {
            var freshReader = new SqlCorpusRetrievalReader(pipeline.Factory);
            var engine = new HybridPassageRetrievalEngine(freshReader, freshReader, leases, ann,
                new Models(pipeline.Embeddings), owner, codec, disclosure, searchTimeout: TimeSpan.FromSeconds(25));
            return (freshReader, new CorpusRetrievalService(freshReader, codec, disclosure, new(true), engine));
        }
    }

    private static async Task<(long Legacy, long Vectors, long Links, long Active)> MembershipAsync(FluxKnowledgeDbContext db) =>
        (await db.IndexGenerationVectors.LongCountAsync(), await db.IndexSegmentVectors.LongCountAsync(),
            await db.IndexGenerationSegments.LongCountAsync(), await db.CurrentIndexMembers.LongCountAsync());

    private static async Task<WebApplication> StartPublicationComparisonHostAsync(CorpusRetrievalService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<INativeV1Facade>(new CorpusOnlyFacade(service));
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        var app = builder.Build();
        app.MapFluxKnowledgeNativeV1();
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<(string Passage, string Evidence)> SearchComparisonAsync(
        HttpClient client, ObservedAnnFactory ann, string query)
    {
        var previousCalls = ann.SearchCalls;
        using var response = await client.PostAsJsonAsync("/api/v1/corpus/search", new { query, scope = "all", limit = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("hybrid", result.GetProperty("retrievalMode").GetString());
        Assert.Equal("ready", result.GetProperty("semanticStatus").GetString());
        Assert.True(ann.SearchCalls > previousCalls);
        var hit = Assert.Single(result.GetProperty("results").EnumerateArray());
        return (hit.GetProperty("passage").GetString()!, hit.GetProperty("evidenceRef").GetString()!);
    }

    private static async Task AssertComparisonCitationAsync(HttpClient client, (string Passage, string Evidence) hit)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/corpus/read",
            new { evidence_ref = hit.Evidence, context_characters = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(hit.Passage, json.RootElement.GetProperty("result").GetProperty("text").GetString());
    }

    private static double Dot(float[] a, IReadOnlyList<float> b)
    {
        double result = 0;
        for (var index = 0; index < a.Length; index++)
            result += (double)a[index] * b[index];
        return result;
    }

    private static double Percentile(List<double> values, double fraction) =>
        values.Order().ElementAt((int)Math.Ceiling(fraction * values.Count) - 1);
}
