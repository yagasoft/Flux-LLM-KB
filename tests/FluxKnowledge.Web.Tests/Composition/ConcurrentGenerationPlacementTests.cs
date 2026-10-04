using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class ConcurrentGenerationPlacementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"FluxPlacementRace_{Guid.NewGuid():N}");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Matching_winner_is_reused_at_both_placement_collision_timings(bool beforePlacementCheck)
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(root);
        var store = new Store();
        IndexGenerationCandidateSnapshot? winner = null;
        var creator = new CollisionCreator(beforePlacementCheck, () =>
            winner = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator())
                .BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask().GetAwaiter().GetResult());
        var contender = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(), null, creator);

        var result = await contender.BuildAndPlaceAsync(Guid.NewGuid(), default);

        Assert.NotNull(winner);
        Assert.Equal(winner.Generation, result.Generation);
        Assert.Equal(store.Stamp, result.ExpectedCorpusStamp);
        new UsearchGenerationValidator().Validate(result.Generation.IndexPath, result.Generation, result.Vectors);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "generations")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "staging"), "*", SearchOption.AllDirectories));
    }

    private sealed class CollisionCreator(bool beforePlacementCheck, Action placeWinner) : IUsearchDirectoryCreator
    {
        private bool fired;
        public void CreateDirectory(string path)
        {
            if (!fired && (beforePlacementCheck ? path.Contains("staging", StringComparison.Ordinal) : Path.GetFileName(path) == "generations"))
            {
                fired = true;
                placeWinner();
            }
            Directory.CreateDirectory(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Corrupt_collision_winner_is_refused_and_left_untouched(bool beforePlacementCheck)
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(root);
        var store = new Store();
        string? winnerPath = null;
        var creator = new CollisionCreator(beforePlacementCheck, () =>
        {
            var winner = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator())
                .BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask().GetAwaiter().GetResult();
            winnerPath = winner.Generation.IndexPath;
            File.WriteAllText(Path.Combine(winnerPath, UsearchGenerationValidator.MetadataFileName), "corrupt winning metadata");
        });
        var contender = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(), null, creator);

        await Assert.ThrowsAsync<IndexGenerationValidationException>(() => contender.BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask());

        Assert.Equal("corrupt winning metadata", File.ReadAllText(Path.Combine(winnerPath!, UsearchGenerationValidator.MetadataFileName)));
        Assert.True(File.Exists(Path.Combine(winnerPath!, UsearchGenerationValidator.IndexFileName)));
    }

    [Fact]
    public async Task Unrelated_directory_creation_failure_is_not_reinterpreted_as_a_collision()
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(root);
        var store = new Store();
        var expected = new IOException("unrelated creation failure");
        var creator = new CollisionCreator(false, () =>
        {
            _ = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator())
                .BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask().GetAwaiter().GetResult();
            throw expected;
        });
        var contender = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(), null, creator);
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => contender.BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask()));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "generations")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conflicting_collision_metadata_is_refused_without_modifying_the_winner(bool beforePlacementCheck)
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(root);
        var store = new Store();
        string? metadataPath = null;
        string? conflicting = null;
        var creator = new CollisionCreator(beforePlacementCheck, () =>
        {
            var winner = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator())
                .BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask().GetAwaiter().GetResult();
            metadataPath = Path.Combine(winner.Generation.IndexPath, UsearchGenerationValidator.MetadataFileName);
            var metadata = JsonSerializer.Deserialize<UsearchGenerationValidator.Metadata>(File.ReadAllText(metadataPath))!;
            conflicting = JsonSerializer.Serialize(metadata with { CorpusStamp = store.Stamp with { CorpusVersion = store.Stamp.CorpusVersion + 1 } });
            File.WriteAllText(metadataPath, conflicting);
        });
        await Assert.ThrowsAsync<IndexGenerationValidationException>(() =>
            new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(), null, creator).BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask());
        Assert.Equal(conflicting, File.ReadAllText(metadataPath!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unsafe_winning_path_is_refused_at_both_collision_timings(bool beforePlacementCheck)
    {
        var layout = LiveRootLayout.CreateForIsolatedTests(root);
        var options = UsearchIndexOptions.FromConfiguredRoot(layout.IndexRoot);
        var store = new Store();
        var inspector = new CollisionPathInspector();
        var safety = new LiveRootStorageSafety(layout, inspector);
        var creator = new CollisionCreator(beforePlacementCheck, () =>
        {
            var winner = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator())
                .BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask().GetAwaiter().GetResult();
            inspector.UnsafePath = winner.Generation.IndexPath;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(safety), safety, creator).BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask());
        Assert.True(File.Exists(Path.Combine(inspector.UnsafePath!, UsearchGenerationValidator.MetadataFileName)));
    }

    private sealed class CollisionPathInspector : ILiveRootPathInspector
    {
        public string? UnsafePath { get; set; }
        public LiveRootPathInspection Inspect(string path) => new(true, path == UnsafePath, path);
    }

    [Fact]
    public async Task A_file_at_the_final_path_is_an_explicit_failure()
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(root);
        var store = new Store();
        string? path = null;
        var creator = new CollisionCreator(true, () =>
        {
            var snapshot = store.ReadPublicationSnapshotAsync(Guid.Empty, default).Result;
            var checksum = UsearchGenerationValidator.ComputeChecksum(snapshot.Vectors[0].ModelFingerprint, 2, snapshot.Vectors);
            path = Path.Combine(root, "generations", UsearchGenerationValidator.DeterministicGenerationId(checksum, store.Stamp).ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "winning file");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(), null, creator).BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask());
        Assert.Equal("winning file", File.ReadAllText(path!));
    }

    [Fact]
    public async Task An_unrelated_move_failure_is_not_hidden_by_an_existing_winner()
    {
        var options = UsearchIndexOptions.FromConfiguredRoot(root);
        var store = new Store();
        var creator = new CollisionCreator(false, () =>
        {
            _ = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator())
                .BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask().GetAwaiter().GetResult();
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "staging"), "*", SearchOption.AllDirectories)
                .Where(value => File.Exists(Path.Combine(value, UsearchGenerationValidator.MetadataFileName))).ToArray())
                Directory.Delete(directory, true);
        });
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator(), null, creator).BuildAndPlaceAsync(Guid.NewGuid(), default).AsTask());
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "generations")));
    }

    [Theory]
    [InlineData(17)] // Windows ERROR_NOT_SAME_DEVICE.
    [InlineData(39)] // Windows ERROR_HANDLE_DISK_FULL.
    public void Windows_move_errors_are_not_posix_destination_collisions(int error)
    {
        if (!OperatingSystem.IsWindows()) return;
        var failure = new IOException("unrelated Windows move failure", unchecked((int)0x80070000) | error);
        Assert.False(AtomicGenerationPlacement.IsDestinationCollision(failure));
    }

    private sealed class Store : IIndexGenerationStore
    {
        public CorpusPublicationStamp Stamp { get; } = new(Guid.NewGuid(), 4);
        private readonly CanonicalVector vector = CreateVector();
        public ValueTask<IndexPublicationSnapshot> ReadPublicationSnapshotAsync(Guid id, CancellationToken ct) =>
            ValueTask.FromResult(new IndexPublicationSnapshot([vector], Stamp, Stamp));
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(PipelineRecordId id, long revision, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor descriptor, CancellationToken ct) => throw new NotSupportedException();
        private static CanonicalVector CreateVector()
        {
            var bytes = BitConverter.GetBytes(1f).Concat(BitConverter.GetBytes(0f)).ToArray();
            return new(1, 1, "synthetic-placement", 2, bytes, new string('a', 64), Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
