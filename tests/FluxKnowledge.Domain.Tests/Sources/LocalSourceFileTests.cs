using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;
using Xunit.Sdk;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class LocalSourceFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeSources_{Guid.NewGuid():N}");

    public LocalSourceFileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Artifact_store_places_verified_bytes_under_the_content_addressed_path()
    {
        var storeRoot = Path.Combine(_root, "store");
        var bytes = "retained bytes"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var store = new ContentAddressedSourceArtifactStore(storeRoot);

        var receipt = await store.PutAsync(
            bytes,
            new SourceArtifactMetadata(hash, "text/plain", bytes.Length),
            CancellationToken.None);

        Assert.False(receipt.ExistingArtifact);
        Assert.Equal($"sha256{Path.DirectorySeparatorChar}{hash[..2]}{Path.DirectorySeparatorChar}{hash}.bin", receipt.StoreRelativePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(storeRoot, receipt.StoreRelativePath)));

        var duplicate = await store.PutAsync(
            bytes,
            new SourceArtifactMetadata(hash, "text/plain", bytes.Length),
            CancellationToken.None);
        Assert.True(duplicate.ExistingArtifact);
    }

    [Fact]
    public async Task Artifact_store_rejects_a_declared_checksum_that_does_not_match_the_retained_bytes()
    {
        var store = new ContentAddressedSourceArtifactStore(Path.Combine(_root, "store"));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutAsync(
            "contents"u8.ToArray(),
            new SourceArtifactMetadata(new string('a', 64), "text/plain", 8),
            CancellationToken.None).AsTask());
    }

    [Fact]
    public void Artifact_store_root_rejects_an_overlap_with_a_protected_deployment_root()
    {
        var forbiddenRoot = Path.Combine(_root, "deployment", "source-artifacts");

        Assert.Throws<ArgumentException>(() => new ContentAddressedSourceArtifactStore(
            forbiddenRoot,
            [Path.Combine(_root, "deployment")]));

        Assert.False(Directory.Exists(forbiddenRoot));
    }

    [Fact]
    public void Artifact_store_root_rejects_a_physical_alias_of_a_protected_root()
    {
        var protectedRoot = Path.Combine(_root, "protected");
        var alias = Path.Combine(_root, "protected-alias");
        Directory.CreateDirectory(protectedRoot);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(alias, protectedRoot);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw SkipException.ForSkip($"Directory symbolic links are unavailable: {exception.GetType().Name}");
            }

            Assert.Throws<UnauthorizedAccessException>(() => new ContentAddressedSourceArtifactStore(
                Path.Combine(alias, "source-artifacts"),
                [protectedRoot]));
            Assert.False(Directory.Exists(Path.Combine(protectedRoot, "source-artifacts")));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
        }
    }

    [Fact]
    public async Task Artifact_store_rejects_a_reparse_replacement_of_the_sha256_destination_before_writing()
    {
        var storeRoot = Path.Combine(_root, "store");
        var escapedRoot = Path.Combine(_root, "escaped");
        var sha256 = Path.Combine(storeRoot, "sha256");
        Directory.CreateDirectory(escapedRoot);
        var store = new ContentAddressedSourceArtifactStore(storeRoot);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(sha256, escapedRoot);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw SkipException.ForSkip($"Directory symbolic links are unavailable: {exception.GetType().Name}");
            }

            var bytes = "destination containment"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.PutAsync(
                bytes,
                new SourceArtifactMetadata(hash, "text/plain", bytes.Length),
                CancellationToken.None).AsTask());

            Assert.Empty(Directory.EnumerateFiles(escapedRoot, "*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(escapedRoot, hash[..2])));
        }
        finally
        {
            if (Directory.Exists(sha256))
            {
                Directory.Delete(sha256);
            }
        }
    }

    [Fact]
    public async Task Artifact_store_holds_the_shard_directory_through_the_validation_to_write_window()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Destination directory delete locking is Windows-specific.");
        }

        var storeRoot = Path.Combine(_root, "store");
        var bytes = "held destination"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var shard = Path.Combine(storeRoot, "sha256", hash[..2]);
        var replacementSucceeded = false;
        var store = new ContentAddressedSourceArtifactStore(
            storeRoot,
            beforeArtifactWrite: _ =>
            {
                try
                {
                    Directory.Delete(shard);
                    replacementSucceeded = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }

                return ValueTask.CompletedTask;
            });

        try
        {
            var receipt = await store.PutAsync(
                bytes,
                new SourceArtifactMetadata(hash, "text/plain", bytes.Length),
                CancellationToken.None);
            Assert.False(replacementSucceeded);
            Assert.True(File.Exists(Path.Combine(storeRoot, receipt.StoreRelativePath)));
        }
        catch (IOException)
        {
            Assert.True(replacementSucceeded);
            Assert.False(File.Exists(Path.Combine(shard, $"{hash}.bin")));
        }
    }

    [Fact]
    public async Task Artifact_store_revalidates_sha256_after_its_lease_and_before_shard_creation()
    {
        var storeRoot = Path.Combine(_root, "store");
        var escapedRoot = Path.Combine(_root, "escaped");
        Directory.CreateDirectory(escapedRoot);
        var bytes = "sha replacement"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sha256 = Path.Combine(storeRoot, "sha256");
        var replacementSucceeded = false;
        var store = new ContentAddressedSourceArtifactStore(
            storeRoot,
            beforeShardCreation: () =>
            {
                try
                {
                    Directory.Delete(sha256);
                    Directory.CreateSymbolicLink(sha256, escapedRoot);
                    replacementSucceeded = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            });

        try
        {
            var receipt = await store.PutAsync(
                bytes,
                new SourceArtifactMetadata(hash, "text/plain", bytes.Length),
                CancellationToken.None);
            Assert.False(replacementSucceeded);
            Assert.True(File.Exists(Path.Combine(storeRoot, receipt.StoreRelativePath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.True(replacementSucceeded);
            Assert.False(Directory.Exists(Path.Combine(escapedRoot, hash[..2])));
            Assert.Empty(Directory.EnumerateFiles(escapedRoot, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Enumerator_orders_matching_relative_paths_and_does_not_traverse_a_reparse_directory()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "z.txt"), "z");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_root, "ignored"));
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored", "hidden.txt"), "hidden");
        var configuration = SourceRootConfiguration.Create(
            Path.GetFullPath(_root), "Test root", recursive: true, followLinks: false,
            maximumFileBytes: 16 * 1024 * 1024,
            excludePatterns: ["ignored/**"]);
        var enumerator = new LocalSourceEnumerator();

        var files = await enumerator.EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

        Assert.Equal(["a.txt", "z.txt"], files.Select(file => file.RelativePath));
    }

    [Fact]
    public async Task Enumerator_retains_full_text_only_through_the_sixteen_mebibyte_boundary()
    {
        var boundary = Path.Combine(_root, "boundary.txt");
        var oversized = Path.Combine(_root, "oversized.txt");
        await File.WriteAllBytesAsync(boundary, Enumerable.Repeat((byte)'a', 16 * 1024 * 1024).ToArray());
        await File.WriteAllBytesAsync(oversized, Enumerable.Repeat((byte)'b', (16 * 1024 * 1024) + 1).ToArray());
        var configuration = SourceRootConfiguration.Create(
            Path.GetFullPath(_root), "Test root", recursive: false, followLinks: false,
            maximumFileBytes: (16 * 1024 * 1024) + 1);

        var files = await new LocalSourceEnumerator().EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

        var retained = Assert.Single(files, value => value.RelativePath == "boundary.txt");
        var deferred = Assert.Single(files, value => value.RelativePath == "oversized.txt");
        Assert.True(retained.HasFullBoundedBuffer);
        Assert.Equal(16 * 1024 * 1024, retained.ClassificationBuffer.Length);
        Assert.False(deferred.HasFullBoundedBuffer);
        Assert.True(deferred.ClassificationBuffer.Length < 1024 * 1024);
        Assert.Equal(SourceClassification.DeferredPolicy, deferred.Classification.Classification);
    }

    [Fact]
    public async Task Enumerator_defers_text_larger_than_the_root_file_limit_even_when_below_the_global_limit()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "over-root-limit.txt"), "12345");
        var configuration = SourceRootConfiguration.Create(
            Path.GetFullPath(_root), "Test root", recursive: false, followLinks: false,
            maximumFileBytes: 4);

        var file = Assert.Single(await new LocalSourceEnumerator()
            .EnumerateAsync(configuration, CancellationToken.None)
            .ToListAsync());

        Assert.Equal(SourceClassification.DeferredPolicy, file.Classification.Classification);
        Assert.Null(file.Classification.Text);
        Assert.False(file.HasFullBoundedBuffer);
    }

    [Fact]
    public async Task Enumerator_keeps_a_signature_and_text_extension_disagreement_out_of_text_ingestion()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "misleading.txt"), "%PDF-1.7"u8.ToArray());
        var configuration = SourceRootConfiguration.Create(Path.GetFullPath(_root), "Test root", false, false, 1024);

        var file = Assert.Single(await new LocalSourceEnumerator()
            .EnumerateAsync(configuration, CancellationToken.None)
            .ToListAsync());

        Assert.Equal(SourceClassification.Unknown, file.Classification.Classification);
        Assert.Null(file.Classification.Text);
    }

    [Fact]
    public async Task Enumerator_marks_the_scan_incomplete_when_the_admitted_root_identity_no_longer_matches()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.txt"), "visible");
        var configuration = SourceRootConfiguration.Restore(
            SourceRootId.New(),
            Path.GetFullPath(_root),
            "Test root",
            recursive: false,
            followLinks: false,
            maximumFileBytes: 1024,
            includePatterns: [],
            excludePatterns: [],
            allowedClassifications: [],
            reconciliationCadence: TimeSpan.FromMinutes(15),
            state: SourceRootState.Enabled,
            configurationRevision: 1,
            physicalIdentityFingerprint: new string('0', 64));
        var enumerator = new LocalSourceEnumerator();

        var files = await enumerator.EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

        Assert.Empty(files);
        var evidence = Assert.Single(enumerator.LastEvidence);
        Assert.Equal("identity", evidence.Kind);
        Assert.Equal("SourceRootIdentityMismatch", evidence.Detail);
        Assert.DoesNotContain(_root, evidence.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enumerator_marks_a_claimed_root_incomplete_when_durable_identity_evidence_is_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.txt"), "visible");
        var configuration = SourceRootConfiguration.Restore(
            SourceRootId.New(), Path.GetFullPath(_root), "Test root", false, false, 1024,
            [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1,
            physicalIdentityFingerprint: null,
            requiresPhysicalIdentityValidation: true);
        var enumerator = new LocalSourceEnumerator();

        var files = await enumerator.EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

        Assert.Empty(files);
        var evidence = Assert.Single(enumerator.LastEvidence);
        Assert.Equal("identity", evidence.Kind);
        Assert.Equal("SourceRootIdentityMissing", evidence.Detail);
    }

    [Fact]
    public async Task Enumerator_marks_a_claimed_root_incomplete_when_durable_identity_evidence_is_malformed()
    {
        var configuration = SourceRootConfiguration.Restore(
            SourceRootId.New(), Path.GetFullPath(_root), "Test root", false, false, 1024,
            [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1,
            physicalIdentityFingerprint: "not-a-fingerprint",
            requiresPhysicalIdentityValidation: true);
        var enumerator = new LocalSourceEnumerator();

        _ = await enumerator.EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

        var evidence = Assert.Single(enumerator.LastEvidence);
        Assert.Equal("SourceRootIdentityMalformed", evidence.Detail);
    }

    [Fact]
    public async Task Enumerator_converts_a_per_file_identity_read_failure_to_bounded_evidence()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "locked.txt"), "contents");
        var configuration = SourceRootConfiguration.Create(Path.GetFullPath(_root), "Test root", false, false, 1024);
        var enumerator = new LocalSourceEnumerator(_ => throw new IOException("identity unavailable"));

        var files = await enumerator.EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

        Assert.Empty(files);
        var evidence = Assert.Single(enumerator.LastEvidence);
        Assert.Equal("io", evidence.Kind);
        Assert.Equal("locked.txt", evidence.RelativePath);
        Assert.Equal("IOException", evidence.Detail);
    }

    [Fact]
    public async Task Enumerator_reports_a_real_reparse_directory_without_traversing_it()
    {
        var target = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeSourceTarget_{Guid.NewGuid():N}");
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "outside.txt"), "outside");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw SkipException.ForSkip($"Directory symbolic links are unavailable: {exception.GetType().Name}");
            }

            var configuration = SourceRootConfiguration.Create(Path.GetFullPath(_root), "Test root", true, false, 1024);
            var enumerator = new LocalSourceEnumerator();

            var files = await enumerator.EnumerateAsync(configuration, CancellationToken.None).ToListAsync();

            Assert.Empty(files);
            Assert.Contains(enumerator.LastEvidence, evidence =>
                evidence.Kind == "reparse" && evidence.RelativePath == "linked");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Artifact_store_reopens_and_rejects_a_source_snapshot_that_changed_after_discovery()
    {
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, "before");
        var configuration = SourceRootConfiguration.Create(Path.GetFullPath(_root), "Test root", false, false, 1024);
        var snapshot = Assert.Single(await new LocalSourceEnumerator().EnumerateAsync(configuration, CancellationToken.None).ToListAsync());
        await File.WriteAllTextAsync(source, "after");
        var storeRoot = Path.Combine(_root, "store");
        var store = new ContentAddressedSourceArtifactStore(storeRoot);

        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => store.PutFileAsync(
            snapshot,
            new SourceArtifactMetadata(snapshot.ContentSha256, "text/plain", snapshot.ByteLength),
            CancellationToken.None).AsTask());

        Assert.Empty(Directory.Exists(storeRoot) ? Directory.EnumerateFiles(storeRoot, "*.tmp", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Artifact_store_rehashes_the_source_when_the_content_addressed_artifact_already_exists()
    {
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, "before");
        var configuration = SourceRootConfiguration.Create(Path.GetFullPath(_root), "Test root", false, false, 1024);
        var snapshot = Assert.Single(await new LocalSourceEnumerator().EnumerateAsync(configuration, CancellationToken.None).ToListAsync());
        var store = new ContentAddressedSourceArtifactStore(Path.Combine(_root, "store"));
        var metadata = new SourceArtifactMetadata(snapshot.ContentSha256, "text/plain", snapshot.ByteLength);
        _ = await store.PutFileAsync(snapshot, metadata, CancellationToken.None);

        await File.WriteAllTextAsync(source, "after!");
        File.SetLastWriteTimeUtc(source, snapshot.LastWriteAtUtc.UtcDateTime);

        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() =>
            store.PutFileAsync(snapshot, metadata, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Artifact_store_cleans_the_partial_file_when_the_open_source_mutates_during_streaming()
    {
        var source = Path.Combine(_root, "streamed.txt");
        await File.WriteAllTextAsync(source, "before");
        var configuration = SourceRootConfiguration.Create(Path.GetFullPath(_root), "Test root", false, false, 1024);
        var snapshot = Assert.Single(await new LocalSourceEnumerator().EnumerateAsync(configuration, CancellationToken.None).ToListAsync());
        var storeRoot = Path.Combine(_root, "store");
        var store = new ContentAddressedSourceArtifactStore(
            storeRoot,
            beforeSourceRead: async cancellationToken =>
            {
                await File.WriteAllTextAsync(source, "after!", cancellationToken);
                File.SetLastWriteTimeUtc(source, snapshot.LastWriteAtUtc.UtcDateTime);
            });

        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => store.PutFileAsync(
            snapshot,
            new SourceArtifactMetadata(snapshot.ContentSha256, "text/plain", snapshot.ByteLength),
            CancellationToken.None).AsTask());

        Assert.Empty(Directory.EnumerateFiles(storeRoot, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
