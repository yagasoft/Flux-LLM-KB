using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class WindowsIndexGenerationReadLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "index-base-guard-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Lease_holds_files_and_ancestors_against_write_delete_or_rename_until_disposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateGeneration();
        var file = Path.Combine(path, "index.usearch");
        var metadata = Path.Combine(path, "metadata.json");
        using (new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path))
        {
            Assert.Equal("synthetic-index", File.ReadAllText(file));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(file, "mutated"));
            Assert.ThrowsAny<IOException>(() => File.Delete(metadata));
            Assert.ThrowsAny<IOException>(() => Directory.Move(path, path + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(_root, _root + "-moved"));
        }
        File.WriteAllText(file, "released");
        Assert.Equal("released", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("index.usearch")]
    [InlineData("metadata.json")]
    public void Lease_refuses_a_final_file_link_and_releases_previously_opened_handles(string filename)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateGeneration();
        var external = Path.Combine(_root, "outside-file");
        File.WriteAllText(external, "outside");
        var target = Path.Combine(path, filename);
        File.Delete(target);
        File.CreateSymbolicLink(target, external);

        Assert.ThrowsAny<IOException>(() => new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path));

        File.Delete(target);
        File.WriteAllText(target, "restored");
        using var valid = new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path);
    }

    [Fact]
    public void Lease_distinguishes_a_proved_missing_generation_leaf_from_access_or_path_refusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateGeneration();
        Directory.Delete(path, recursive: true);

        Assert.Throws<DirectoryNotFoundException>(() => new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path));

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "metadata.json"), "{}");
        File.WriteAllText(Path.Combine(path, "index.usearch"), "restored");
        using var valid = new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path);
    }

    [Fact]
    public void Lease_refuses_an_ancestor_link_and_a_generation_outside_its_configured_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateGeneration();
        var generationRoot = Path.GetDirectoryName(path)!;
        var moved = Path.Combine(_root, "outside-generations");
        Directory.Move(generationRoot, moved);
        Directory.CreateSymbolicLink(generationRoot, moved);
        Assert.Throws<UnauthorizedAccessException>(() => new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path));
        Assert.Throws<UnauthorizedAccessException>(() => new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(Path.Combine(moved, Path.GetFileName(path))));
        Directory.Delete(generationRoot);
        Directory.Move(moved, generationRoot);
        using var valid = new WindowsIndexGenerationReadLeaseFactory(_root).Acquire(path);
    }

    private string CreateGeneration()
    {
        var path = Path.Combine(_root, "generations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "index.usearch"), "synthetic-index");
        File.WriteAllText(Path.Combine(path, "metadata.json"), "{}");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
