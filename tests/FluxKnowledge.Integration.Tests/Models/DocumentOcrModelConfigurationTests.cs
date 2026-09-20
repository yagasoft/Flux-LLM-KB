using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Documents;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class DocumentOcrModelConfigurationTests
{
    [Fact]
    public async Task Configuration_reader_preserves_quoted_and_arabic_ctc_characters()
    {
        var yaml = """
            PostProcess:
              name: CTCLabelDecode
              character_dict:
              - A
              - ''''
              - 'ا'
              - 　
            Next:
              ignored: true
            """;
        using var lease = new VerifiedLocalModelLease(
            new Dictionary<string, IModelVerificationFile>(StringComparer.Ordinal)
            {
                ["inference.yml"] = new MemoryModelFile(System.Text.Encoding.UTF8.GetBytes(yaml))
            },
            new NoopDisposable());

        var characters = await DocumentOcrModelConfiguration.ReadCtcCharactersAsync(lease, CancellationToken.None);

        Assert.Equal(["A", "'", "ا", "　"], characters);
    }

    private sealed class MemoryModelFile(byte[] content) : IModelVerificationFile
    {
        public long ByteLength => content.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = content.AsMemory(checked((int)offset));
            var count = Math.Min(remaining.Length, buffer.Length);
            remaining[..count].CopyTo(buffer);
            return ValueTask.FromResult(count);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
