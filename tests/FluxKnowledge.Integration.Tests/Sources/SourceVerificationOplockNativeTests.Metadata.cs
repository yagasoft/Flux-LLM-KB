using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Fact]
    public async Task Owned_blob_deletion_uses_path_identity_and_length_without_a_content_checksum_pass()
    {
        using var fixture = new Fixture();
        var retained = Path.Combine(fixture.Root, "Retained");
        var indexes = Path.Combine(fixture.Root, "Indexes");
        var recordedDigest = new string('a', 64);
        var relative = Path.Combine("sha256", "aa", recordedDigest + ".bin");
        var path = Path.Combine(retained, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(indexes);
        await File.WriteAllTextAsync(path, "owned retained bytes");
        var length = new FileInfo(path).Length;
        var result = await new WindowsSourceDeletionFileStore(retained, indexes)
            .DeleteAsync(new(Guid.NewGuid(), 1, relative, recordedDigest, length), default);
        Assert.True(result.Completed, result.ReasonCode);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task Retained_metadata_observation_reads_no_body_and_holds_its_file_until_disposal()
    {
        using var fixture = new Fixture();
        var bytes = new byte[256 * 1024];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var retained = Path.Combine(fixture.Root, "Retained");
        var relative = Path.Combine("sha256", hash[..2], hash + ".bin");
        var path = Path.Combine(retained, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var bodyReads = 0;
        using var store = new ContentAddressedSourceArtifactStore(retained) { RetainedBytesRead = count => bodyReads += count };
        await using (var observation = await store.OpenObservationAsync(relative, hash, bytes.Length, default))
        {
            Assert.Equal(bytes.Length, observation.Observation.ByteLength);
            Assert.Null(observation.ChecksumVerifiedAtUtc);
            observation.ValidateCurrent();
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read));
        }
        Assert.Equal(0, bodyReads);
        using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Retained_metadata_observation_rejects_the_wrong_length_without_reading_the_body()
    {
        using var fixture = new Fixture();
        var retained = Path.Combine(fixture.Root, "Retained");
        var hash = new string('a', 64);
        var relative = Path.Combine("sha256", "aa", hash + ".bin");
        Directory.CreateDirectory(Path.Combine(retained, "sha256", "aa"));
        await File.WriteAllTextAsync(Path.Combine(retained, relative), "short");
        using var store = new ContentAddressedSourceArtifactStore(retained);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenObservationAsync(relative, hash, 100, default).AsTask());
        using var released = File.Open(Path.Combine(retained, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
