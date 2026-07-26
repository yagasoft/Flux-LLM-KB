using FluxKnowledge.Infrastructure.Inference;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Indexing;

public sealed class DeterministicTokenHashEmbeddingProviderTests
{
    [Fact]
    public async Task Same_normalised_text_produces_the_same_unit_vector_without_a_model_asset()
    {
        var provider = new DeterministicTokenHashEmbeddingProvider();

        var first = await provider.CreateEmbeddingAsync("Café   PLAN", CancellationToken.None);
        var second = await provider.CreateEmbeddingAsync("Cafe\u0301 plan", CancellationToken.None);

        Assert.Equal("deterministic-tokenhash-v1:256", first.ModelFingerprint);
        Assert.Equal(256, first.Values.Count);
        Assert.Equal(first.Values, second.Values);
        Assert.InRange(first.Values.Sum(static value => value * value), 0.9999F, 1.0001F);
    }

    [Fact]
    public async Task Ascii_alphanumeric_tokens_use_fnv1a_low_byte_dimension_and_high_bit_sign()
    {
        var provider = new DeterministicTokenHashEmbeddingProvider();

        var result = await provider.CreateEmbeddingAsync("abc", CancellationToken.None);

        Assert.Equal(-1F, result.Values[0x4b]);
        Assert.Equal(1, result.Values.Count(static value => value != 0F));
    }

    [Fact]
    public async Task Text_without_ascii_alphanumeric_tokens_produces_a_zero_vector()
    {
        var provider = new DeterministicTokenHashEmbeddingProvider();

        var result = await provider.CreateEmbeddingAsync("— · —", CancellationToken.None);

        Assert.All(result.Values, static value => Assert.Equal(0F, value));
    }
}
