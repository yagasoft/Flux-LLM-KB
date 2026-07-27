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
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(MaximumChunkLength, text.Length - start);
            if (start + length < text.Length &&
                char.IsHighSurrogate(text[start + length - 1]) &&
                char.IsLowSurrogate(text[start + length]))
            {
                length--;
            }
            var content = text.Substring(start, length);
            chunks.Add(new CanonicalTextChunk(
                0,
                chunks.Count,
                start,
                length,
                content,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))));
            start += length;
        }

        return chunks;
    }
}
