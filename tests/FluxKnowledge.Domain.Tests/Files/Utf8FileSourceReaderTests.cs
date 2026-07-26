using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using FluxKnowledge.Integrations.Files;
using Xunit;
using Xunit.Sdk;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class Utf8FileSourceReaderTests : IDisposable
{
    private readonly string _testRoot =
        Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIngress_{Guid.NewGuid():N}");

    public Utf8FileSourceReaderTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public async Task Read_returns_canonical_path_text_and_hash_of_the_exact_bytes()
    {
        var path = Path.Combine(_testRoot, "a.txt");
        var bytes = "hello\r\ncafé"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var reader = new Utf8FileSourceReader(new LocalIngressOptions([_testRoot]));

        var source = await reader.ReadAsync(path, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(path), source.CanonicalPath);
        Assert.Equal(bytes, source.ExactBytes);
        Assert.Equal("hello\r\ncafé", source.Text);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), source.ContentHash);
    }

    [Fact]
    public async Task Read_rejects_a_path_outside_the_canonical_allowed_roots()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outsidePath, "outside");
        var reader = new Utf8FileSourceReader(new LocalIngressOptions([_testRoot]));

        try
        {
            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await reader.ReadAsync(outsidePath, CancellationToken.None));

            Assert.Contains("outside the configured local ingress roots", exception.Message);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task Read_rejects_bytes_that_are_not_strict_utf8()
    {
        var path = Path.Combine(_testRoot, "invalid.txt");
        await File.WriteAllBytesAsync(path, [0xc3, 0x28]);
        var reader = new Utf8FileSourceReader(new LocalIngressOptions([_testRoot]));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reader.ReadAsync(path, CancellationToken.None));

        Assert.Contains("valid UTF-8", exception.Message);
    }

    [Fact]
    public async Task Filesystem_root_is_a_valid_allowed_root_without_separator_duplication()
    {
        var path = Path.Combine(_testRoot, "root-allowed.txt");
        await File.WriteAllTextAsync(path, "inside");
        var filesystemRoot = Path.GetPathRoot(path);
        Assert.False(string.IsNullOrWhiteSpace(filesystemRoot));
        var reader = new Utf8FileSourceReader(new LocalIngressOptions([filesystemRoot]));

        var source = await reader.ReadAsync(path, CancellationToken.None);

        Assert.Equal("inside", source.Text);
    }

    [Fact]
    public async Task Reparse_point_cannot_escape_an_allowed_root()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"FluxKnowledgeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var outsideFile = Path.Combine(outsideRoot, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "outside secret");
        var linkPath = Path.Combine(_testRoot, "escape");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (Exception linkException) when (
                linkException is UnauthorizedAccessException or
                PlatformNotSupportedException or
                IOException)
            {
                throw SkipException.ForSkip(
                    $"This host cannot create a directory symbolic link: {linkException.Message}");
            }

            var reader = new Utf8FileSourceReader(new LocalIngressOptions([_testRoot]));

            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await reader.ReadAsync(
                    Path.Combine(linkPath, "outside.txt"),
                    CancellationToken.None));

            Assert.Contains("physical target", exception.Message);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Containment_is_validated_against_the_same_open_handle_used_for_reading()
    {
        var insidePath = Path.Combine(_testRoot, "inside.txt");
        await File.WriteAllTextAsync(insidePath, "inside");
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            $"FluxKnowledgeHandleOutside_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outsidePath, "outside secret");
        var reader = new Utf8FileSourceReader(
            new LocalIngressOptions([_testRoot]),
            new RedirectingHandleOpener(outsidePath));

        try
        {
            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await reader.ReadAsync(insidePath, CancellationToken.None));

            Assert.Contains("opened file handle", exception.Message);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_testRoot, recursive: true);
    }

    private sealed class RedirectingHandleOpener(string redirectedPath)
        : IUtf8FileHandleOpener
    {
        public SafeFileHandle OpenRead(string canonicalPath) =>
            File.OpenHandle(
                redirectedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
