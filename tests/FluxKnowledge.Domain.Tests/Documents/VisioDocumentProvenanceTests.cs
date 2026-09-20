using System.Text.Json;
using FluxKnowledge.Application.Documents;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class VisioDocumentProvenanceTests
{
    [Fact]
    public void NormaliseTextAndMetadata_preserves_Visio_shape_identity_and_remaps_Arabic_shape_text_offsets()
    {
        const string source = "A\r\nمرحبا\u00a0";
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
        [
            new DocumentPageProvenance(
                7,
                0,
                source.Length,
                "visio",
                null,
                [
                    new DocumentBlockProvenance(
                        3,
                        6,
                        "visio",
                        "shape",
                        null,
                        null,
                        null,
                        null,
                        ShapeId: 42,
                        ParentShapeId: 9,
                        MasterId: 2,
                        MasterShapeId: "4")
                ],
                VisioPageId: 7,
                IsBackground: false)
        ]));

        var normalised = DocumentOcrProvenance.NormaliseTextAndMetadata(source, metadata, out var normalisedMetadata);
        var block = Assert.Single(Assert.Single(DocumentOcrProvenance.Parse(normalisedMetadata).Pages).Blocks);

        Assert.Equal("A\nمرحبا ", normalised);
        Assert.Equal(2, block.StartOffset);
        Assert.Equal(6, block.Length);
        Assert.Equal(42, block.ShapeId);
        Assert.Equal(9, block.ParentShapeId);
        Assert.Equal(2, block.MasterId);
        Assert.Equal("4", block.MasterShapeId);
    }

    [Fact]
    public void NormaliseTextAndMetadata_keeps_a_single_newline_when_adjacent_shape_blocks_split_a_CRLF()
    {
        const string source = "A\r\nB";
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
        [
            new DocumentPageProvenance(1, 0, source.Length, "visio", null,
            [
                new DocumentBlockProvenance(0, 2, "visio", "shape", null, null, null, null, ShapeId: 1),
                new DocumentBlockProvenance(2, 2, "visio", "shape", null, null, null, null, ShapeId: 2)
            ], VisioPageId: 1, IsBackground: false)
        ]));

        var normalised = DocumentOcrProvenance.NormaliseTextAndMetadata(source, metadata, out var normalisedMetadata);
        var blocks = Assert.Single(DocumentOcrProvenance.Parse(normalisedMetadata).Pages).Blocks;

        Assert.Equal("A\nB", normalised);
        Assert.Collection(blocks,
            first => Assert.Equal((0, 2), (first.StartOffset, first.Length)),
            second => Assert.Equal((2, 1), (second.StartOffset, second.Length)));
    }
}
