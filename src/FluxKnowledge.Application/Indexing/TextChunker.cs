using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Indexing;

public static class TextChunker
{
    public const int MaximumChunkLength = 2_048;

    public static IReadOnlyList<CanonicalTextChunk> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = new List<CanonicalTextChunk>();
        for (var start = 0; start < text.Length; start += MaximumChunkLength)
        {
            var length = Math.Min(MaximumChunkLength, text.Length - start);
            var content = text.Substring(start, length);
            chunks.Add(new CanonicalTextChunk(
                0,
                chunks.Count,
                start,
                length,
                content,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))));
        }

        return chunks;
    }
}
