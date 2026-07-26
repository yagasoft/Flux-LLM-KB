using System.Security.Cryptography;
using FluxKnowledge.Integrations.Files;
using Xunit;

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

    public void Dispose()
    {
        Directory.Delete(_testRoot, recursive: true);
    }
}
