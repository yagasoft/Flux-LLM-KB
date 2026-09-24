using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class CorpusCitationMapperTests
{
    [Fact]
    public void Crossing_canonical_pages_returns_two_page_intersections()
    {
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
        [
            new DocumentPageProvenance(0, 0, 5, "native", null, []),
            new DocumentPageProvenance(1, 7, 5, "ocr", 90,
                [new DocumentBlockProvenance(7, 5, "ocr", "text", 1, 2, 3, 4)],
                SourceWidth: 800, SourceHeight: 600, SourceTransform: "rotate-90")
        ]));

        var result = CorpusCitationMapper.Map(metadata, 3, 8);

        Assert.Equal(2, result.Locations.Count(location => location.Kind == "page"));
        Assert.Contains(result.Locations, location => location.PageNumber == 1 &&
            location.StartOffset == 3 && location.Length == 2);
        Assert.Contains(result.Locations, location => location.PageNumber == 2 &&
            location.StartOffset == 7 && location.Length == 4);
        Assert.Contains(result.Locations, location => location.Kind == "block" &&
            location.OrientationDegrees == 90 && location.SourceTransform == "rotate-90");
    }

    [Fact]
    public void Invalid_metadata_keeps_verified_span_without_inventing_page()
    {
        var result = CorpusCitationMapper.Map("{\"version\":2,\"pages\":[]}", 4, 3);
        Assert.Single(result.Locations);
        Assert.Equal("text-span", result.Locations[0].Kind);
        Assert.Equal(["provenance-invalid"], result.Warnings);
    }

    [Fact]
    public void Image_provenance_is_labelled_as_an_image_without_a_document_page_number()
    {
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
        [
            new DocumentPageProvenance(0, 0, 5, "ocr", 90,
                [new DocumentBlockProvenance(0, 5, "ocr", "text", 2, 3, 10, 12)],
                SourceWidth: 100, SourceHeight: 80, SourceTransform: "rotate-90")
        ]));

        var citation = CorpusCitationMapper.Map(metadata, 0, 5, @"C:\images\scan.png");
        Assert.Contains(citation.Locations, location => location.Kind == "image" &&
            location.PageNumber is null && location.SourceWidth == 100);
        Assert.Contains(citation.Locations, location => location.Kind == "block" &&
            location.PageNumber is null && location.PageIndex == 0);
    }

    [Fact]
    public void Invalid_image_geometry_returns_span_only()
    {
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
        [
            new DocumentPageProvenance(0, 0, 5, "ocr", 45,
                [new DocumentBlockProvenance(0, 5, "ocr", "text", 98, 3, 10, 12)],
                SourceWidth: 100, SourceHeight: 80, SourceTransform: "identity")
        ]));
        var citation = CorpusCitationMapper.Map(metadata, 0, 5, @"C:\images\scan.png");
        Assert.Single(citation.Locations);
        Assert.Equal(["provenance-invalid"], citation.Warnings);
    }

    [Theory]
    [InlineData("{\"version\":1,\"pages\":[null]}")]
    [InlineData("{\"version\":1,\"pages\":[{\"pageIndex\":0,\"startOffset\":2147483640,\"length\":20,\"method\":\"native\",\"blocks\":[]}]}")]
    public void Malformed_or_overflowing_pages_fall_back_to_a_verified_span(string metadata)
    {
        var citation = CorpusCitationMapper.Map(metadata, 4, 3);
        Assert.Single(citation.Locations);
        Assert.Equal(["provenance-invalid"], citation.Warnings);
    }
}
