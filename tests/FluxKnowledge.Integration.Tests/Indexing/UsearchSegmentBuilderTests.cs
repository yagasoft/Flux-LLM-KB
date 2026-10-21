using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class UsearchSegmentBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeSegments_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Low_capacity_defers_before_native_placement_and_reuses_existing_segments()
    {
        var builder = Builder(2);
        var input = new[] { Vector(1), Vector(2) };
        var first = Assert.Single(await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None));
        var constrained = new UsearchSegmentBuilder(UsearchIndexOptions.FromConfiguredRoot(_root),
            new WindowsIndexSegmentReadLeaseFactory(_root), new(2)) { AvailableBytesOverride = () => 0 };
        Assert.Equal(first, Assert.Single(await constrained.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None)));
        await Assert.ThrowsAsync<IndexStorageCapacityException>(async () => await constrained.BuildAsync(
            "synthetic-segment-profile", 4, Stream([Vector(3)]), CancellationToken.None));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "segments")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "staging", "segments")));
    }

    [Fact]
    public async Task Capacity_is_rechecked_between_units_and_completed_units_resume_without_rewrites()
    {
        var calls = 0;
        var constrained = new UsearchSegmentBuilder(UsearchIndexOptions.FromConfiguredRoot(_root),
            new WindowsIndexSegmentReadLeaseFactory(_root), new(2)) { AvailableBytesOverride = () => ++calls == 1 ? long.MaxValue : 0 };
        var input = Enumerable.Range(1, 4).Select(id => Vector(id)).ToArray();
        await Assert.ThrowsAsync<IndexStorageCapacityException>(async () => await constrained.BuildAsync(
            "synthetic-segment-profile", 4, Stream(input), CancellationToken.None));
        var firstPath = Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "segments")));
        var timestamp = File.GetLastWriteTimeUtc(Path.Combine(firstPath, "index.usearch"));
        var resumed = await Builder(2).BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None);
        Assert.Equal(2, resumed.Count);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(Path.Combine(firstPath, "index.usearch")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_retired_segment_deletion_is_guarded_and_idempotent(bool distinctIncarnation)
    {
        var segment = Assert.Single(await Builder(2).BuildAsync("synthetic-segment-profile", 4, Stream([Vector(1), Vector(2)]), CancellationToken.None,
            incarnation: distinctIncarnation ? Guid.NewGuid() : Guid.Empty));
        var files = new WindowsIndexProjectionFileStore(_root);
        await files.DeleteSegmentAsync(segment, CancellationToken.None);
        Assert.False(Directory.Exists(segment.DirectoryPath));
        await files.DeleteSegmentAsync(segment, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_segment_deletion_does_not_treat_a_missing_ancestor_as_a_missing_object(bool moveRoot)
    {
        var segment = Assert.Single(await Builder(2).BuildAsync("synthetic-segment-profile", 4, Stream([Vector(1)]), CancellationToken.None));
        var original = moveRoot ? _root : Path.Combine(_root, "segments");
        var relocated = original + ".held";
        Directory.Move(original, relocated);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await new WindowsIndexProjectionFileStore(_root).DeleteSegmentAsync(segment, CancellationToken.None));
            var retained = Path.Combine(relocated, moveRoot ? "segments" : "", segment.Id);
            Assert.True(File.Exists(Path.Combine(retained, "index.usearch")));
            Assert.True(File.Exists(Path.Combine(retained, "metadata.json")));
        }
        finally { Directory.Move(relocated, original); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_segment_deletion_holds_ancestors_in_place_until_unlink(bool moveRoot)
    {
        var segment = Assert.Single(await Builder(2).BuildAsync("synthetic-segment-profile", 4, Stream([Vector(1)]), CancellationToken.None));
        var original = moveRoot ? _root : Path.Combine(_root, "segments");
        var relocated = original + ".moved";
        var attempted = false;
        var files = new WindowsIndexProjectionFileStore(_root)
        {
            BeforeUnlink = () =>
            {
                attempted = true;
                Assert.ThrowsAny<IOException>(() => Directory.Move(original, relocated));
            }
        };
        await files.DeleteSegmentAsync(segment, CancellationToken.None);
        Assert.True(attempted);
        Assert.True(Directory.Exists(original));
        Assert.False(Directory.Exists(relocated));
        Assert.False(Directory.Exists(segment.DirectoryPath));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("foreign-child")]
    [InlineData("escape")]
    [InlineData("incarnation")]
    public async Task Retired_segment_deletion_refuses_unproven_bytes_or_paths(string fault)
    {
        var segment = Assert.Single(await Builder(2).BuildAsync("synthetic-segment-profile", 4, Stream([Vector(1)]), CancellationToken.None));
        var original = Path.Combine(segment.DirectoryPath, "index.usearch");
        if (fault == "tampered") await File.AppendAllTextAsync(original, "unexpected bytes");
        if (fault == "foreign-child") await File.WriteAllTextAsync(Path.Combine(segment.DirectoryPath, "unowned.bin"), "keep");
        var target = fault == "escape" ? segment with { DirectoryPath = Path.GetDirectoryName(_root)! } :
            fault == "incarnation" ? segment with { PhysicalIncarnation = Guid.NewGuid() } : segment;
        await Assert.ThrowsAnyAsync<Exception>(async () => await new WindowsIndexProjectionFileStore(_root).DeleteSegmentAsync(target, CancellationToken.None));
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(Path.Combine(segment.DirectoryPath, "metadata.json")));
    }

    [Fact]
    public async Task Bounded_segments_reopen_with_exact_membership_and_reuse_their_immutable_files()
    {
        var builder = Builder(2);
        var input = Enumerable.Range(1, 5).Select(id => Vector(id)).ToArray();
        var first = await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None);
        Assert.Equal(new long[] { 2, 2, 1 }, first.Select(segment => segment.VectorCount));
        var timestamps = first.ToDictionary(segment => segment.Id,
            segment => File.GetLastWriteTimeUtc(Path.Combine(segment.DirectoryPath, "index.usearch")));

        var second = await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None);
        Assert.Equal(first, second);
        foreach (var segment in second)
        {
            var path = Path.Combine(segment.DirectoryPath, "index.usearch");
            Assert.Equal(timestamps[segment.Id], File.GetLastWriteTimeUtc(path));
            Assert.Equal(segment.FileChecksum, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            using var held = new WindowsIndexSegmentReadLeaseFactory(_root).Acquire(segment.DirectoryPath);
            using var native = new USearchIndex(path, false);
            Assert.Equal((ulong)segment.VectorCount, native.Size());
            Assert.Equal(4UL, native.Dimensions());
        }
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "staging", "segments")));
    }

    [Fact]
    public async Task A_fresh_operation_can_rebuild_corrupt_bytes_without_overwriting_the_previous_incarnation()
    {
        var builder = Builder(2);
        var input = new[] { Vector(1), Vector(2) };
        var operation = Guid.NewGuid();
        var first = Assert.Single(await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None, incarnation: operation));
        var replay = Assert.Single(await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None, incarnation: operation));
        Assert.Equal(first, replay);
        var oldPath = Path.Combine(first.DirectoryPath, "index.usearch");
        await File.WriteAllTextAsync(oldPath, "damaged derived bytes");
        var damagedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(oldPath)));
        await Assert.ThrowsAsync<IndexGenerationValidationException>(async () => await builder.BuildAsync(
            "synthetic-segment-profile", 4, Stream(input), CancellationToken.None, incarnation: operation));
        var repaired = Assert.Single(await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None, incarnation: Guid.NewGuid()));
        Assert.NotEqual(first.Id, repaired.Id);
        Assert.Equal(first.MembershipChecksum, repaired.MembershipChecksum);
        Assert.Equal(damagedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(oldPath))));
    }

    [Fact]
    public async Task Payload_budget_splits_work_without_refusing_the_source()
    {
        var segments = await Builder(100, 32).BuildAsync("synthetic-segment-profile", 4,
            Stream(Enumerable.Range(1, 7).Select(id => Vector(id))), CancellationToken.None);
        Assert.Equal(new long[] { 2, 2, 2, 1 }, segments.Select(segment => segment.VectorCount));
        Assert.Equal(7, segments.Sum(segment => segment.VectorCount));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("dimensions")]
    [InlineData("checksum")]
    [InlineData("nonfinite")]
    [InlineData("zero")]
    [InlineData("duplicate")]
    [InlineData("order")]
    public async Task Invalid_canonical_input_is_refused_before_an_invalid_segment_is_sealed(string fault)
    {
        var vectors = new[] { Vector(1), Vector(2) };
        vectors[1] = fault switch
        {
            "profile" => vectors[1] with { ModelFingerprint = "other" },
            "dimensions" => vectors[1] with { Dimensions = 3 },
            "checksum" => vectors[1] with { PayloadChecksum = new string('0', 64) },
            "nonfinite" => Vector(2, float.NaN),
            "zero" => Vector(2, 0),
            "duplicate" => Vector(1),
            "order" => Vector(0),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        await Assert.ThrowsAsync<IndexGenerationValidationException>(async () =>
            await Builder(8).BuildAsync("synthetic-segment-profile", 4, Stream(vectors), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_root, "segments")) &&
            Directory.EnumerateDirectories(Path.Combine(_root, "segments")).Any());
    }

    [Fact]
    public async Task Reuse_refuses_tampered_native_payload_without_overwriting_it()
    {
        var builder = Builder(2);
        var input = new[] { Vector(1), Vector(2) };
        var segment = Assert.Single(await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None));
        var path = Path.Combine(segment.DirectoryPath, "index.usearch");
        var corrupt = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, corrupt);
        await Assert.ThrowsAsync<IndexGenerationValidationException>(async () =>
            await builder.BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Concurrent_identical_builds_reuse_one_fully_validated_placement()
    {
        var input = new[] { Vector(1), Vector(2) };
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            await Builder(2).BuildAsync("synthetic-segment-profile", 4, Stream(input), CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "segments")));
    }

    [Fact]
    public async Task Segment_path_escape_or_reparse_parent_is_refused_without_writing_outside_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "FluxKnowledgeSegmentOutside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "segments");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () => await Builder(2).BuildAsync(
                "synthetic-segment-profile", 4, Stream([Vector(1)]), CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.Throws<UnauthorizedAccessException>(() => new WindowsIndexSegmentReadLeaseFactory(_root).Acquire(outside));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside);
        }
    }

    [Fact]
    public async Task Cancellation_before_work_does_not_place_or_create_any_segment()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Builder(2).BuildAsync(
            "synthetic-segment-profile", 4, Stream([Vector(1)]), cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    private UsearchSegmentBuilder Builder(int maximumVectors, long maximumPayloadBytes = 1024) =>
        new(UsearchIndexOptions.FromConfiguredRoot(_root), new WindowsIndexSegmentReadLeaseFactory(_root),
            new IndexSegmentBuildOptions(maximumVectors, maximumPayloadBytes));

    private static CanonicalVector Vector(long id, float value = 1)
    {
        var values = new[] { value, value * 2, value * 3, value * 4 };
        var bytes = new byte[16];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new CanonicalVector(id, id, "synthetic-segment-profile", 4, bytes, new string('a', 64),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
    }

    private static async IAsyncEnumerable<CanonicalVector> Stream(IEnumerable<CanonicalVector> vectors,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var vector in vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return vector;
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
