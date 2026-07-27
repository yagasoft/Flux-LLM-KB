using FluxKnowledge.Application.Indexing;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Indexing;

public sealed class TextChunkerTests
{
    [Fact]
    public void Boundary_never_splits_a_surrogate_pair()
    {
        var text = new string('a', TextChunker.MaximumChunkLength - 1) + "😀tail";

        var chunks = TextChunker.Chunk(text);

        Assert.Equal("😀", chunks[1].Content[..2]);
        Assert.Equal(TextChunker.MaximumChunkLength - 1, chunks[0].Length);
        Assert.Equal(chunks[0].Length, chunks[1].StartOffset);
    }
}
