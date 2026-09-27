using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public sealed class BgeInferenceException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}

public interface IBgeTokenizer : IPassageTokenizer, IDisposable
{
    long[] EncodeUntruncated(string text);
}

internal interface IBgeTensorRunner : IDisposable
{
    BgeTensorOutput Run(BgeInputBatch batch, CancellationToken cancellationToken);
}

internal sealed record BgeTensorOutput(float[] Values, int[] Dimensions);

internal sealed record BgeInputBatch(long[] InputIds, long[] AttentionMask, int Count, int SequenceLength)
{
    internal const int MaximumTokens = 512;
    internal const int MaximumBatch = 4;

    internal static long[] Pair(long[] query, long[] passage)
    {
        ValidateTokens(query);
        ValidateTokens(passage);
        return [.. query, 2, .. passage.AsSpan(1)];
    }

    internal static BgeInputBatch Create(IReadOnlyList<long[]> sequences)
    {
        if (sequences.Count is < 1 or > MaximumBatch) throw new BgeInferenceException("bge-batch-invalid");
        foreach (var sequence in sequences)
        {
            ValidateTokens(sequence);
            if (sequence.Length > MaximumTokens) throw new BgeInferenceException("bge-input-too-long");
        }
        var width = sequences.Max(static s => s.Length);
        var ids = Enumerable.Repeat(1L, sequences.Count * width).ToArray();
        var mask = new long[ids.Length];
        for (var row = 0; row < sequences.Count; row++)
        {
            sequences[row].CopyTo(ids, row * width);
            Array.Fill(mask, 1L, row * width, sequences[row].Length);
        }
        return new(ids, mask, sequences.Count, width);
    }

    private static void ValidateTokens(long[] tokens)
    {
        if (tokens is null || tokens.Length < 2 || tokens[0] != 0 || tokens[^1] != 2 || tokens.Any(static t => t is < 0 or >= 250002))
            throw new BgeInferenceException("bge-token-input-invalid");
    }
}

internal static class BgeOutputValidation
{
    internal static float[] Embedding(float[] values, int[] dimensions)
        => Embeddings(values, dimensions, 1)[0];

    internal static float[][] Embeddings(float[] values, int[] dimensions, int count)
    {
        if (count is < 1 or > BgeInputBatch.MaximumBatch || !dimensions.SequenceEqual([count, 1024]) ||
            values.Length != count * 1024 || values.Any(static x => !float.IsFinite(x)))
            throw new BgeInferenceException("bge-embedding-output-invalid");
        var results = new float[count][];
        for (var row = 0; row < count; row++)
        {
            var vector = values.AsSpan(row * 1024, 1024).ToArray();
            var norm = Math.Sqrt(vector.Sum(static x => (double)x * x));
            if (!double.IsFinite(norm) || norm < 1e-12) throw new BgeInferenceException("bge-embedding-output-invalid");
            results[row] = vector.Select(x => (float)(x / norm)).ToArray();
        }
        return results;
    }

    internal static float[] Logits(float[] values, int[] dimensions, int count)
    {
        if (!dimensions.SequenceEqual([count, 1]) || values.Length != count || values.Any(static x => !float.IsFinite(x)))
            throw new BgeInferenceException("bge-reranker-output-invalid");
        return values;
    }
}
