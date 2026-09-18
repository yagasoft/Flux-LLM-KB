using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Physical deletion accepts only the SQL-captured identity beneath app-owned roots.</summary>
public sealed class WindowsSourceDeletionFileStoreTests
{
    [Fact]
    public async Task Deletes_only_a_verified_content_addressed_artifact_and_generation_directory()
    {
        await using var fixture = new DeletionFileFixture();
        var bytes = "owned artifact"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactPath = Path.Combine(fixture.ArtifactRoot, "sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllBytesAsync(artifactPath, bytes);
        var generationId = Guid.NewGuid();
        var generationPath = Path.Combine(fixture.IndexRoot, "generations", generationId.ToString("N"));
        Directory.CreateDirectory(generationPath);
        await File.WriteAllTextAsync(Path.Combine(generationPath, "index.usearch"), "owned index");
        var store = new WindowsSourceDeletionFileStore(fixture.ArtifactRoot, fixture.IndexRoot);

        var artifact = await store.DeleteAsync(
            new SourceDeletionFileTarget(Guid.NewGuid(), 1, Path.Combine("sha256", hash[..2], $"{hash}.bin"), hash, bytes.Length),
            CancellationToken.None);
        var generation = await store.DeleteAsync(
            new SourceDeletionFileTarget(Guid.NewGuid(), 2, generationId.ToString("N"), null, null),
            CancellationToken.None);

        Assert.True(artifact.Completed);
        Assert.True(generation.Completed);
        Assert.False(File.Exists(artifactPath));
        Assert.False(Directory.Exists(generationPath));
    }

    [Fact]
    public async Task Refuses_a_mismatched_or_unsafe_artifact_without_deleting_anything()
    {
        await using var fixture = new DeletionFileFixture();
        var bytes = "owned artifact"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactPath = Path.Combine(fixture.ArtifactRoot, "sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllBytesAsync(artifactPath, "different bytes"u8.ToArray());
        var store = new WindowsSourceDeletionFileStore(fixture.ArtifactRoot, fixture.IndexRoot);

        var corrupt = await store.DeleteAsync(
            new SourceDeletionFileTarget(Guid.NewGuid(), 1, Path.Combine("sha256", hash[..2], $"{hash}.bin"), hash, bytes.Length),
            CancellationToken.None);
        var unsafePath = await store.DeleteAsync(
            new SourceDeletionFileTarget(Guid.NewGuid(), 1, "..\\outside.bin", hash, bytes.Length),
            CancellationToken.None);

        Assert.False(corrupt.Completed);
        Assert.Equal("source-delete-artifact-binding-invalid", corrupt.ReasonCode);
        Assert.False(unsafePath.Completed);
        Assert.Equal("source-delete-artifact-path-invalid", unsafePath.ReasonCode);
        Assert.True(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Refuses_a_reparse_point_before_following_an_artifact_path()
    {
        await using var fixture = new DeletionFileFixture();
        var bytes = "owned artifact"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var externalShard = Path.Combine(fixture.Root, "external", hash[..2]);
        Directory.CreateDirectory(externalShard);
        var externalFile = Path.Combine(externalShard, $"{hash}.bin");
        await File.WriteAllBytesAsync(externalFile, bytes);
        var sha256 = Path.Combine(fixture.ArtifactRoot, "sha256");
        Directory.CreateDirectory(sha256);
        Directory.CreateSymbolicLink(Path.Combine(sha256, hash[..2]), externalShard);
        var store = new WindowsSourceDeletionFileStore(fixture.ArtifactRoot, fixture.IndexRoot);

        var result = await store.DeleteAsync(
            new SourceDeletionFileTarget(Guid.NewGuid(), 1, Path.Combine("sha256", hash[..2], $"{hash}.bin"), hash, bytes.Length),
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal("source-delete-path-unsafe", result.ReasonCode);
        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Deletes_an_artifact_when_an_ancestor_allows_only_read_and_traverse()
    {
        await using var fixture = new DeletionFileFixture();
        var bytes = "owned artifact"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactPath = Path.Combine(fixture.ArtifactRoot, "sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllBytesAsync(artifactPath, bytes);
        Directory.Delete(fixture.IndexRoot);
        var originalSecurity = new DirectoryInfo(fixture.Root)
            .GetAccessControl(AccessControlSections.Access);
        try
        {
            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current test identity has no SID.");
            var fullControl = new DirectorySecurity();
            fullControl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fullControl.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            var readOnlyAncestor = new DirectorySecurity();
            readOnlyAncestor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            readOnlyAncestor.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(fixture.Root).SetAccessControl(readOnlyAncestor);
            foreach (var directory in new[] { fixture.ArtifactRoot, Path.Combine(fixture.ArtifactRoot, "sha256"), Path.GetDirectoryName(artifactPath)! })
            {
                new DirectoryInfo(directory).SetAccessControl(fullControl);
            }

            var store = new WindowsSourceDeletionFileStore(fixture.ArtifactRoot, fixture.ArtifactRoot);
            var artifact = await store.DeleteAsync(
                new SourceDeletionFileTarget(Guid.NewGuid(), 1, Path.Combine("sha256", hash[..2], $"{hash}.bin"), hash, bytes.Length),
                CancellationToken.None);

            Assert.True(artifact.Completed);
            Assert.False(File.Exists(artifactPath));
        }
        finally
        {
            new DirectoryInfo(fixture.Root).SetAccessControl(originalSecurity);
        }
    }

    private sealed class DeletionFileFixture : IAsyncDisposable
    {
        public DeletionFileFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeSourceDeletionFiles_{Guid.NewGuid():N}");
            ArtifactRoot = Path.Combine(Root, "artifacts");
            IndexRoot = Path.Combine(Root, "indexes");
            Directory.CreateDirectory(ArtifactRoot);
            Directory.CreateDirectory(IndexRoot);
        }

        public string Root { get; }
        public string ArtifactRoot { get; }
        public string IndexRoot { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
