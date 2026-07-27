using System.Text;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference;

/// <summary>
/// A local, model-free embedding for Phase 1. Text is normalised with FormKC
/// and invariant casing, then split at every non-ASCII-letter-or-digit rune.
/// Each token's FNV-1a 64 hash selects its low-byte dimension; bit 63 supplies
/// a signed unit term contribution. If no ASCII token exists, or signed terms
/// exactly cancel, a deterministic FormKC text hash contributes one fallback
/// term so every non-empty normalised input remains non-zero. Non-empty vectors are L2 normalised.
/// </summary>
public sealed class DeterministicTokenHashEmbeddingProvider : IEmbeddingProvider
{
    public const int Dimensions = 256;
    public const string Fingerprint = "deterministic-tokenhash-v1:256";
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public ValueTask<EmbeddingResult> CreateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var values = new float[Dimensions];
        var normalised = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var start = 0;
        while (start < normalised.Length)
        {
            while (start < normalised.Length && !IsAsciiAlphaNumeric(normalised[start]))
            {
                start++;
            }

            var end = start;
            while (end < normalised.Length && IsAsciiAlphaNumeric(normalised[end]))
            {
                end++;
            }

            if (end > start)
            {
                var hash = HashAscii(normalised.AsSpan(start, end - start));
                var dimension = (int)(hash & 0xffUL);
                values[dimension] += (hash & (1UL << 63)) == 0UL ? 1F : -1F;
                start = end;
            }
        }

        var sumSquares = values.Sum(static value => value * value);
        if (sumSquares == 0F && normalised.Length > 0)
        {
            var fallback = HashBytes(Encoding.UTF8.GetBytes(normalised));
            values[(int)(fallback & 0xffUL)] = 1F;
            sumSquares = 1F;
        }
        if (sumSquares > 0F)
        {
            var inverseLength = 1F / MathF.Sqrt(sumSquares);
            for (var index = 0; index < values.Length; index++)
            {
                values[index] *= inverseLength;
            }
        }

        return ValueTask.FromResult(
            new EmbeddingResult(values, Fingerprint));
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static ulong HashAscii(ReadOnlySpan<char> token)
    {
        var hash = FnvOffsetBasis;
        foreach (var character in token)
        {
            hash ^= (byte)character;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = FnvOffsetBasis;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= FnvPrime;
        }

        return hash;
    }
}
