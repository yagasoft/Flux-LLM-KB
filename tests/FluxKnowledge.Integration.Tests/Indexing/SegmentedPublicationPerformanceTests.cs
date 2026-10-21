using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentedPublicationPerformanceTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(1, false, false)]
    [InlineData(10, false, false)]
    [InlineData(1, true, false)]
    [InlineData(10, true, false)]
    [InlineData(1, true, true)]
    [InlineData(10, true, true)]
    public async Task Small_publication_reuses_unrelated_payloads_and_cache_measurements_cover_both_working_sets(int scale, bool segmented, bool oversized)
    {
        var metrics = new Counters();
        var cacheBytes = oversized ? 16 * 1024 : 32 * 1024 * 1024;
        var text = string.Join(' ', Enumerable.Range(0, 3500 * scale).Select(index => $"word_{index:00000}"));
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, text,
            new PassageBuilder(new Words()), segmented: segmented, segmentMaximumVectors: 8,
            configureServices: services =>
            {
                services.AddSingleton<IIndexGenerationStore>(provider => new CountingGenerationStore(new SqlPipelineStore(
                    provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>()), metrics));
                if (!segmented) return;
                services.AddScoped<ISegmentedIndexStore>(provider => new CountingSegmentStore(new SqlSegmentedIndexStore(
                    provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), TimeProvider.System,
                    provider.GetRequiredService<IGpuInteractiveOwnerProbe>(), provider.GetService<IDeploymentValidationHold>(),
                    provider.GetRequiredService<IndexSegmentBuildOptions>()), metrics));
                services.AddSingleton<IIndexSegmentOwnerStore>(provider => new CountingOwners(new SqlIndexSegmentOwnerStore(
                    provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), TimeProvider.System,
                    provider.GetRequiredService<IGpuInteractiveOwnerProbe>()), metrics));
                services.AddSingleton(provider => new UsearchSegmentCache(provider.GetRequiredService<UsearchIndexOptions>(),
                    provider.GetRequiredService<IIndexSegmentReadLeaseFactory>(), provider.GetRequiredService<IIndexSegmentOwnerStore>(),
                    TimeProvider.System, maximumBytes: cacheBytes));
            });
        await using var db = await environment.Factory.CreateDbContextAsync();
        var before = await environment.ActiveGenerationAsync();
        var files = Directory.GetFiles(environment.IndexRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Hash: Hash(path), Written: File.GetLastWriteTimeUtc(path)));
        var ids = await db.IndexSegments.Select(value => value.Id).ToArrayAsync();
        var oldMembers = await db.IndexSegmentVectors.OrderBy(value => value.SegmentId).ThenBy(value => value.VectorId)
            .Select(value => new { value.SegmentId, value.VectorId }).ToArrayAsync();
        metrics.Reset();
        var timer = Stopwatch.StartNew();
        await environment.AddAndPumpAsync("One new small source must not rewrite unrelated source payloads.");
        timer.Stop();
        var after = await environment.ActiveGenerationAsync();
        Assert.Equal(before.VectorCount + 1, after.VectorCount);
        var added = Directory.GetFiles(environment.IndexRoot, "*", SearchOption.AllDirectories).Except(files.Keys).ToArray();
        var nativeBytes = added.Where(path => Path.GetFileName(path) == "index.usearch").Sum(path => new FileInfo(path).Length);
        var totalBytes = added.Sum(path => new FileInfo(path).Length);
        var publicationBytes = metrics.PublicationPayloadBytes;
        var publicationVectors = metrics.PublicationVectors;
        foreach (var (path, proof) in files)
        {
            Assert.Equal(proof.Hash, Hash(path));
            Assert.Equal(proof.Written, File.GetLastWriteTimeUtc(path));
        }
        var fullMembershipRows = await db.IndexGenerationVectors.LongCountAsync(value => value.GenerationId == after.Id);
        output.WriteLine($"scale={scale} segmented={segmented} oversized={oversized} base_vectors={before.VectorCount} base_segments={before.SegmentCount} publication_ms={timer.Elapsed.TotalMilliseconds:F2} builder_vectors_read={publicationVectors} builder_payload_bytes={publicationBytes} new_native_bytes={nativeBytes} all_new_file_bytes={totalBytes} full_generation_member_rows={fullMembershipRows}");
        if (segmented)
        {
            Assert.Equal(1, publicationVectors);
            Assert.Equal(after.Dimensions * sizeof(float), publicationBytes);
            Assert.Equal(0, fullMembershipRows);
            Assert.Single(added, path => Path.GetFileName(path) == "index.usearch");
            Assert.Equal(oldMembers, await db.IndexSegmentVectors.Where(value => ids.Contains(value.SegmentId))
                .OrderBy(value => value.SegmentId).ThenBy(value => value.VectorId).Select(value => new { value.SegmentId, value.VectorId }).ToArrayAsync());
            await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
            var lengths = await (from member in db.IndexGenerationSegments join segment in db.IndexSegments on member.SegmentId equals segment.Id
                                 where member.GenerationId == after.Id select segment.FileLength).ToArrayAsync();
            output.WriteLine($"scale={scale} oversized={oversized} cache_budget_bytes={cacheBytes} active_native_file_bytes={lengths.Sum()} largest_native_file_bytes={lengths.Max()}");
            Assert.InRange(lengths.Max(), 1, cacheBytes); // Even the oversized-cache case can hold a full segment.
            Assert.Equal(oversized, lengths.Sum() > cacheBytes);
        }
        else
        {
            Assert.True(publicationVectors >= after.VectorCount);
            Assert.Equal(after.VectorCount, fullMembershipRows);
        }

        var vector = await db.Vectors.AsNoTracking().OrderBy(value => value.VectorId).FirstAsync();
        var query = new float[vector.Dimensions]; Buffer.BlockCopy(vector.Values, 0, query, 0, vector.Values.Length);
        var owner = new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe().Current;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            metrics.Reset(); timer.Restart();
            using var scope = environment.Services.CreateScope();
            var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)
                .TryAcquireAsync(Guid.NewGuid(), owner, after.ModelFingerprint, after.Dimensions, CancellationToken.None);
            Assert.NotNull(lease);
            await using (var ann = await scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(lease, CancellationToken.None))
                Assert.Contains(await ann.SearchAsync(query, 100, CancellationToken.None), hit => hit.VectorId == vector.VectorId);
            timer.Stop();
            output.WriteLine($"scale={scale} segmented={segmented} oversized={oversized} query_iteration={iteration} elapsed_ms={timer.Elapsed.TotalMilliseconds:F2} sql_vector_payload_bytes={metrics.QueryPayloadBytes} cache_native_opens={(segmented ? metrics.NativeOpens.ToString() : "legacy-not-counted")}");
            if (!segmented) Assert.Equal(after.VectorCount * after.Dimensions * sizeof(float), metrics.QueryPayloadBytes);
            else
            {
                Assert.Equal(0, metrics.QueryPayloadBytes);
                if (iteration == 0) Assert.Equal(after.SegmentCount, metrics.NativeOpens);
                else if (oversized) Assert.InRange(metrics.NativeOpens, 1, after.SegmentCount - 1);
                else Assert.Equal(0, metrics.NativeOpens);
            }
        }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private sealed class Words : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-scale-words-v1";
        public int CountTokens(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
    private sealed class Counters
    {
        public long PublicationVectors, PublicationPayloadBytes, QueryPayloadBytes;
        public int NativeOpens;
        public void Reset() { PublicationVectors = PublicationPayloadBytes = QueryPayloadBytes = 0; NativeOpens = 0; }
        public void Publication(IReadOnlyList<CanonicalVector> vectors) { PublicationVectors += vectors.Count; PublicationPayloadBytes += vectors.Sum(value => (long)value.Values.Length); }
    }
    private sealed class CountingOwners(IIndexSegmentOwnerStore inner, Counters counters) : IIndexSegmentOwnerStore
    {
        public ValueTask<Guid> RegisterAsync(IndexSegmentDescriptor segment, Guid instance, CancellationToken token) => inner.RegisterAsync(segment, instance, token);
        public async ValueTask OpenedAsync(Guid id, CancellationToken token) { await inner.OpenedAsync(id, token); counters.NativeOpens++; }
        public ValueTask ReleasedAfterDisposalAsync(Guid id, CancellationToken token) => inner.ReleasedAfterDisposalAsync(id, token);
        public ValueTask<IReadOnlySet<string>> ReadRetiringAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.ReadRetiringAsync(ids, token);
    }
    private sealed class CountingGenerationStore(IIndexGenerationStore inner, Counters counters) : IIndexGenerationStore
    {
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(PipelineRecordId id, long revision, CancellationToken token) => inner.ReadChunksAsync(id, revision, token);
        public async ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid id, CancellationToken token)
        { var vectors = await inner.ReadVectorsAsync(id, token); counters.QueryPayloadBytes += vectors.Sum(value => (long)value.Values.Length); return vectors; }
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken token) => inner.ReadEligibleVectorsAsync(token);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadPublicationVectorsAsync(Guid id, CancellationToken token) => inner.ReadPublicationVectorsAsync(id, token);
        public async ValueTask<IndexPublicationSnapshot> ReadPublicationSnapshotAsync(Guid id, CancellationToken token)
        { var snapshot = await inner.ReadPublicationSnapshotAsync(id, token); counters.Publication(snapshot.Vectors); return snapshot; }
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid id, CancellationToken token) => inner.GetGenerationAsync(id, token);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken token) => inner.GetActiveGenerationIdAsync(token);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken token) => inner.UpdateGenerationMetadataAsync(generation, token);
    }
    private sealed class CountingSegmentStore(ISegmentedIndexStore inner, Counters counters) : ISegmentedIndexStore
    {
        public ValueTask<bool> IsRequiredAsync(CancellationToken token) => inner.IsRequiredAsync(token);
        public ValueTask<SegmentedIndexCandidate> PrepareAsync(Guid id, CancellationToken token) => inner.PrepareAsync(id, token);
        public async IAsyncEnumerable<CanonicalVector> ReadBuildVectorsAsync(Guid id, [EnumeratorCancellation] CancellationToken token)
        { await foreach (var vector in inner.ReadBuildVectorsAsync(id, token)) { counters.PublicationVectors++; counters.PublicationPayloadBytes += vector.Values.Length; yield return vector; } }
        public ValueTask SealSegmentAsync(Guid id, IndexSegmentDescriptor segment, IReadOnlyList<CanonicalVector> vectors, CancellationToken token) => inner.SealSegmentAsync(id, segment, vectors, token);
        public ValueTask SealManifestAsync(Guid id, IndexManifestDescriptor manifest, CancellationToken token) => inner.SealManifestAsync(id, manifest, token);
        public ValueTask<IndexManifestDescriptor> ReadManifestAsync(Guid id, CancellationToken token) => inner.ReadManifestAsync(id, token);
        public ValueTask<SegmentedProjectionState> ReadProjectionAsync(CancellationToken token) => inner.ReadProjectionAsync(token);
        public ValueTask<bool> TryActivateRefreshAsync(IndexGenerationCandidateSnapshot snapshot, CancellationToken token) => inner.TryActivateRefreshAsync(snapshot, token);
        public ValueTask AbandonAsync(Guid id, CancellationToken token) => inner.AbandonAsync(id, token);
    }
}
