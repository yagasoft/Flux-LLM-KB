using FluxKnowledge.Infrastructure.Inference.Documents;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class CtcTextDecoderTests
{
    [Fact]
    public void Decoder_removes_repeated_labels_and_blanks_without_losing_repeated_characters_separated_by_blank()
    {
        var probabilities = new float[,]
        {
            { 0.99f, 0.01f, 0.00f, 0.00f },
            { 0.01f, 0.97f, 0.01f, 0.01f },
            { 0.01f, 0.96f, 0.02f, 0.01f },
            { 0.01f, 0.01f, 0.95f, 0.03f },
            { 0.99f, 0.01f, 0.00f, 0.00f },
            { 0.01f, 0.01f, 0.02f, 0.96f },
            { 0.01f, 0.01f, 0.02f, 0.97f }
        };

        var result = CtcTextDecoder.Decode(probabilities, ["A", "B", "C"]);

        Assert.Equal("ABC", result.Text);
        Assert.InRange(result.MeanConfidence, 0.959f, 0.961f);
    }

    [Fact]
    public void Decoder_ignores_an_explicit_final_unknown_class()
    {
        var probabilities = new float[,]
        {
            { 0.01f, 0.01f, 0.01f, 0.01f, 0.96f },
            { 0.01f, 0.97f, 0.01f, 0.01f, 0.00f }
        };

        var result = CtcTextDecoder.Decode(probabilities, ["A", "B", "C"], hasUnknownClass: true);

        Assert.Equal("A", result.Text);
        Assert.InRange(result.MeanConfidence, 0.969f, 0.971f);
    }
}
