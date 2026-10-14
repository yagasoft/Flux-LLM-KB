using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class UnavailableSourceContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeUnavailable_{Guid.NewGuid():N}");

    public UnavailableSourceContentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Discovery_does_not_read_offline_content_and_still_discovers_available_siblings()
    {
        var unavailable = Path.Combine(_root, "offline.txt");
        await File.WriteAllTextAsync(unavailable, "These bytes must not be requested while offline.");
        File.SetAttributes(unavailable, File.GetAttributes(unavailable) | FileAttributes.Offline);
        await File.WriteAllTextAsync(Path.Combine(_root, "available.txt"), "Available content");
        var root = SourceRootConfiguration.Create(_root, "Available sample", false, false, 1024);
        var enumerator = new LocalSourceEnumerator();

        var files = new List<string>();
        await foreach (var file in enumerator.EnumerateAsync(root, CancellationToken.None))
            files.Add(file.RelativePath);

        Assert.Equal(["available.txt"], files);
        Assert.Contains(enumerator.LastEvidence, item => item.Kind == "unavailable" && item.RelativePath == "offline.txt");
    }

    [Fact]
    public async Task Ingress_does_not_return_offline_file_bytes_as_available_content()
    {
        var unavailable = Path.Combine(_root, "offline.txt");
        await File.WriteAllTextAsync(unavailable, "These bytes must not be requested while offline.");
        File.SetAttributes(unavailable, File.GetAttributes(unavailable) | FileAttributes.Offline);
        var reader = new Utf8FileSourceReader(new LocalIngressOptions([_root]));

        await Assert.ThrowsAnyAsync<IOException>(async () => await reader.ReadAsync(unavailable, CancellationToken.None));
    }

    [Fact]
    public async Task Retention_rejects_a_replacement_of_the_discovered_root()
    {
        var directory = Path.Combine(_root, "source");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "available.txt"), "original content");
        var files = new List<SourceDiscoveredFile>();
        await foreach (var file in new LocalSourceEnumerator().EnumerateAsync(
            SourceRootConfiguration.Create(directory, "Source", false, false, 1024), default)) files.Add(file);
        var snapshot = Assert.Single(files);
        Directory.Move(directory, directory + "-original");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(snapshot.CanonicalPath, "original content");
        using var store = new ContentAddressedSourceArtifactStore(Path.Combine(_root, "retained"));

        var error = await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => store.PutFileAsync(snapshot,
            new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default).AsTask());

        Assert.Contains("root identity", error.Message);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
