using System.Security.Cryptography;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class UsearchGenerationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_and_append_candidates_accept_distinct_ids_with_repeated_vector_payloads(bool append)
    {
        var store = new AppendStore { Vectors = [AppendVector(1)] };
        var builder = AppendBuilder(store, new RecordingBaseReadLeaseFactory());
        var baseline = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        if (append) store.Base = new(baseline.Generation, [1]);
        store.Version = 2;
        store.Vectors = [AppendVector(1), .. Enumerable.Range(2, 513)
            .Select(id => AppendVector(2) with { VectorId = id, TextChunkId = id })];

        var candidate = await builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(514, candidate.Generation.VectorCount);
        new UsearchGenerationValidator().Validate(candidate.Generation.IndexPath, candidate.Generation, candidate.Vectors);
        using var reopened = new USearchIndex(Path.Combine(candidate.Generation.IndexPath,
            UsearchGenerationValidator.IndexFileName), false);
        Assert.Equal(514UL, reopened.Size());
        foreach (var expected in candidate.Vectors)
        {
            Assert.Equal(1, reopened.Get((ulong)expected.VectorId, out float[] values));
            Assert.Equal(expected.Values, System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
        }
    }

    [Theory]
    [InlineData("duplicate-sql-id")]
    [InlineData("missing-sql-vector")]
    [InlineData("extra-native-id")]
    [InlineData("missing-native-id")]
    [InlineData("replaced-native-id")]
    public async Task Membership_validation_rejects_duplicate_missing_or_extra_ids_without_ANN_enumeration(string mismatch)
    {
        var (_, baseline) = await CreateAppendBaseAsync();
        IReadOnlyList<CanonicalVector> vectors = baseline.Vectors;
        var expected = baseline.Generation;
        if (mismatch == "duplicate-sql-id")
        {
            vectors = [vectors[0], vectors[0], vectors[2]];
            expected = expected with { MetadataChecksum = UsearchGenerationValidator.ComputeChecksum(
                expected.ModelFingerprint, expected.Dimensions, vectors) };
            File.WriteAllText(Path.Combine(expected.IndexPath, UsearchGenerationValidator.MetadataFileName),
                System.Text.Json.JsonSerializer.Serialize(new UsearchGenerationValidator.Metadata(expected.Id,
                    expected.ModelFingerprint, "cos", expected.Dimensions, expected.VectorCount, expected.MetadataChecksum, expected.CorpusStamp)));
        }
        else if (mismatch == "missing-sql-vector") vectors = vectors.Take(2).ToArray();
        else
        {
            using var replacement = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 16, 32, 0, 0, false);
            var ids = mismatch == "extra-native-id" ? new long[] { 1, 2, 3, 4 }
                : mismatch == "missing-native-id" ? [1, 2] : [1, 2, 4];
            foreach (var id in ids) replacement.Add((ulong)id, AppendValues(id));
            replacement.Save(Path.Combine(expected.IndexPath, UsearchGenerationValidator.IndexFileName));
        }

        Assert.Throws<IndexGenerationValidationException>(() =>
            new UsearchGenerationValidator().Validate(expected.IndexPath, expected, vectors));
    }

    [Fact]
    public async Task Append_candidate_preserves_the_base_graph_and_adds_retrievable_vectors_in_a_new_file()
    {
        var (store, baseline) = await CreateAppendBaseAsync();
        var original = File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName));
        var metadata = File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.MetadataFileName));
        var guard = new RecordingBaseReadLeaseFactory();
        store.Vectors = [.. baseline.Vectors, AppendVector(4)];
        var candidate = await AppendBuilder(store, guard).BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, guard.Acquired);
        Assert.Equal(1, guard.Disposed);
        Assert.NotEqual(baseline.Generation.IndexPath, candidate.Generation.IndexPath);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.MetadataFileName)));
        using var index = new USearchIndex(Path.Combine(candidate.Generation.IndexPath, UsearchGenerationValidator.IndexFileName), false);
        Assert.Equal(32UL, index.Connectivity()); // Reused graph; a full build uses the default connectivity.
        Assert.Equal(4UL, index.Size());
        Assert.Equal(1, index.Get(4, out float[] appended));
        Assert.Equal(AppendValues(4), appended);
        var count = index.Search(appended, 4, out var keys, out _);
        Assert.Equal(4, count);
        Assert.Contains(4UL, keys);
        new UsearchGenerationValidator().Validate(candidate.Generation.IndexPath, candidate.Generation, candidate.Vectors);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("replaced-payload")]
    [InlineData("stale-stamp")]
    [InlineData("wrong-membership")]
    [InlineData("wrong-model")]
    [InlineData("missing-file")]
    [InlineData("missing-directory")]
    [InlineData("corrupt-metadata")]
    [InlineData("wrong-native-payload")]
    public async Task An_incompatible_append_base_uses_a_fresh_graph_without_mutating_the_old_generation(string incompatibility)
    {
        if (incompatibility == "missing-directory" && !OperatingSystem.IsWindows()) return;
        var (store, baseline) = await CreateAppendBaseAsync();
        var baseFile = Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName);
        var original = File.ReadAllBytes(baseFile);
        store.Vectors = [.. baseline.Vectors, AppendVector(4)];
        switch (incompatibility)
        {
            case "removed": store.Vectors = [baseline.Vectors[1], AppendVector(4)]; break;
            case "replaced-payload": store.Vectors = [AppendVector(1, alternate: true), baseline.Vectors[1], baseline.Vectors[2], AppendVector(4)]; break;
            case "stale-stamp": store.Base = store.Base! with { Generation = store.Base.Generation with { CorpusStamp = new(store.Epoch, 0) } }; break;
            case "wrong-membership": store.Base = store.Base! with { VectorIds = [1, 2, 999] }; break;
            case "wrong-model": store.Base = store.Base! with { Generation = store.Base.Generation with { ModelFingerprint = "different-model" } }; break;
            case "missing-file": File.Delete(baseFile); break;
            case "missing-directory": Directory.Delete(baseline.Generation.IndexPath, recursive: true); break;
            case "corrupt-metadata": File.WriteAllText(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.MetadataFileName), "{}"); break;
            case "wrong-native-payload":
                using (var corrupt = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 16, 32, 0, 0, false))
                {
                    foreach (var vector in baseline.Vectors) corrupt.Add((ulong)vector.VectorId, AppendValues(vector.VectorId, alternate: true));
                    corrupt.Save(baseFile);
                }
                original = File.ReadAllBytes(baseFile);
                break;
        }
        IIndexGenerationReadLeaseFactory guard = incompatibility == "missing-directory"
            ? new WindowsIndexGenerationReadLeaseFactory(_root) : new RecordingBaseReadLeaseFactory();
        var candidate = await AppendBuilder(store, guard).BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);

        using var index = new USearchIndex(Path.Combine(candidate.Generation.IndexPath, UsearchGenerationValidator.IndexFileName), false);
        Assert.NotEqual(32UL, index.Connectivity());
        Assert.Equal((ulong)store.Vectors.Count, index.Size());
        if (incompatibility == "removed") Assert.False(index.Contains(1));
        if (incompatibility is not ("missing-file" or "missing-directory")) Assert.Equal(original, File.ReadAllBytes(baseFile));
        new UsearchGenerationValidator().Validate(candidate.Generation.IndexPath, candidate.Generation, candidate.Vectors);
    }

    [Fact]
    public async Task Unsafe_base_access_is_not_converted_to_a_successful_full_rebuild()
    {
        var (store, baseline) = await CreateAppendBaseAsync();
        store.Vectors = [.. baseline.Vectors, AppendVector(4)];
        var guard = new RecordingBaseReadLeaseFactory { Failure = new UnauthorizedAccessException("unsafe-base-path") };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppendBuilder(store, guard)
            .BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "generations")));
    }

    [Fact]
    public async Task Cancelled_base_load_disposes_its_guard_without_modifying_the_base_or_placing_a_candidate()
    {
        var (store, baseline) = await CreateAppendBaseAsync();
        var baseFile = Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName);
        var original = File.ReadAllBytes(baseFile);
        store.Vectors = [.. baseline.Vectors, AppendVector(4)];
        using var cancellation = new CancellationTokenSource();
        var guard = new RecordingBaseReadLeaseFactory { OnAcquire = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppendBuilder(store, guard)
            .BuildAndPlaceAsync(Guid.NewGuid(), cancellation.Token).AsTask());

        Assert.Equal(1, guard.Disposed);
        Assert.Equal(original, File.ReadAllBytes(baseFile));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "generations")));
    }

    [Fact]
    public async Task Append_reuse_never_masks_corrupt_current_sql_payloads()
    {
        var (store, baseline) = await CreateAppendBaseAsync();
        var original = File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName));
        var invalid = baseline.Vectors[0] with { Values = AppendVector(1, alternate: true).Values };
        store.Vectors = [invalid, baseline.Vectors[1], baseline.Vectors[2], AppendVector(4)];

        await Assert.ThrowsAsync<IndexGenerationValidationException>(() =>
            AppendBuilder(store, new RecordingBaseReadLeaseFactory()).BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "generations")));
    }

    [Fact]
    public async Task Append_validation_allocation_failure_is_not_hidden_by_a_rebuild()
    {
        var (store, baseline) = await CreateAppendBaseAsync();
        store.Vectors = [.. baseline.Vectors, AppendVector(4)];
        var guard = new RecordingBaseReadLeaseFactory();
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root),
            new AllocationFailureValidator(), baseReadLeaseFactory: guard);

        await Assert.ThrowsAsync<OutOfMemoryException>(() => builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Equal(1, guard.Disposed);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "generations")));
    }

    [Fact]
    public async Task Concurrent_append_candidates_preserve_the_base_and_share_one_immutable_placement()
    {
        var (store, baseline) = await CreateAppendBaseAsync();
        var original = File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName));
        store.Vectors = [.. baseline.Vectors, AppendVector(4)];
        var candidates = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            await AppendBuilder(store, new RecordingBaseReadLeaseFactory()).BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None))));

        Assert.Equal(candidates[0].Generation, candidates[1].Generation);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "generations")).Length);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    private sealed class AllocationFailureValidator : UsearchGenerationValidator
    {
        public override void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors) =>
            throw new OutOfMemoryException("synthetic native allocation failure");
    }

    private UsearchGenerationBuilder AppendBuilder(AppendStore store, IIndexGenerationReadLeaseFactory guard) =>
        new(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator(), baseReadLeaseFactory: guard);

    private async Task<(AppendStore Store, IndexGenerationCandidateSnapshot Base)> CreateAppendBaseAsync()
    {
        var store = new AppendStore { Vectors = [AppendVector(1), AppendVector(2), AppendVector(3)] };
        var baseline = await AppendBuilder(store, new RecordingBaseReadLeaseFactory()).BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        using (var distinctive = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 16, 32, 0, 0, false))
        {
            foreach (var vector in baseline.Vectors) distinctive.Add((ulong)vector.VectorId, AppendValues(vector.VectorId));
            distinctive.Save(Path.Combine(baseline.Generation.IndexPath, UsearchGenerationValidator.IndexFileName));
        }
        new UsearchGenerationValidator().Validate(baseline.Generation.IndexPath, baseline.Generation, baseline.Vectors);
        store.Base = new(baseline.Generation, baseline.Vectors.Select(value => value.VectorId).ToArray());
        store.Version = 2;
        return (store, baseline);
    }

    private static float[] AppendValues(long id, bool alternate = false) =>
        Enumerable.Range(0, 16).Select(index => (float)(Math.Sin(id * 2 + index + (alternate ? .7 : 0)) + .1)).ToArray();

    private static CanonicalVector AppendVector(long id, bool alternate = false)
    {
        var values = AppendValues(id, alternate);
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new(id, id, "synthetic-append-v1", 16, bytes, new string('a', 64), Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
    }

    private sealed class RecordingBaseReadLeaseFactory : IIndexGenerationReadLeaseFactory
    {
        public int Acquired { get; private set; }
        public int Disposed { get; private set; }
        public Exception? Failure { get; init; }
        public Action? OnAcquire { get; init; }
        public IDisposable Acquire(string generationDirectory)
        {
            Acquired++;
            if (Failure is not null) throw Failure;
            using (File.OpenRead(Path.Combine(generationDirectory, UsearchGenerationValidator.IndexFileName))) { }
            using (File.OpenRead(Path.Combine(generationDirectory, UsearchGenerationValidator.MetadataFileName))) { }
            OnAcquire?.Invoke();
            return new Guard(() => Disposed++);
        }
        private sealed class Guard(Action release) : IDisposable { public void Dispose() => release(); }
    }

    private sealed class AppendStore : IIndexGenerationStore
    {
        public Guid Epoch { get; } = Guid.NewGuid();
        public long Version { get; set; } = 1;
        public IndexGenerationBaseSnapshot? Base { get; set; }
        public IReadOnlyList<CanonicalVector> Vectors { get; set; } = [];
        public ValueTask<IndexPublicationSnapshot> ReadPublicationSnapshotAsync(Guid draftGenerationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new IndexPublicationSnapshot(Vectors, new(Epoch, Version - 1), new(Epoch, Version), Base));
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(PipelineRecordId id, long revision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult<IndexGenerationDescriptor?>(null);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
