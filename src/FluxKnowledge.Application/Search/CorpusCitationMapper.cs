using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;

namespace FluxKnowledge.Application.Search;

public sealed record CorpusCitation(
    IReadOnlyList<CorpusLocation> Locations,
    string? ExtractionMethod,
    IReadOnlyList<string> Warnings);

/// <summary>Maps only intersecting canonical provenance to a bounded citation.</summary>
public static class CorpusCitationMapper
{
    private const int MaximumLocations = 16;

    public static CorpusCitation Map(string? metadataJson, int start, int length, string? sourceIdentity = null)
    {
        if (start < 0 || length < 1 || (long)start + length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(start));
        var span = new CorpusLocation("text-span", start, length);
        if (metadataJson is null) return new CorpusCitation([span], null, ["location-unavailable"]);

        try
        {
            var provenance = DocumentOcrProvenance.Parse(metadataJson);
            if (!IsTrustedGeometry(provenance))
                return new CorpusCitation([span], null, ["provenance-invalid"]);
            var locations = new List<CorpusLocation> { span };
            var methods = new HashSet<string>(StringComparer.Ordinal);
            var end = start + length;
            foreach (var page in provenance.Pages.OrderBy(static page => page.PageIndex))
            {
                if (page.Length == 0 || page.StartOffset >= end ||
                    (long)page.StartOffset + page.Length <= start) continue;
                var pageStart = Math.Max(start, page.StartOffset);
                var pageEnd = Math.Min(end, page.StartOffset + page.Length);
                methods.Add(page.Method);
                var isImage = IsImagePath(sourceIdentity);
                locations.Add(new CorpusLocation(
                    isImage ? "image" : "page", pageStart, pageEnd - pageStart,
                    isImage ? null : page.PageIndex + 1, page.PageIndex, page.VisioPageId,
                    page.Method, OrientationDegrees: page.OrientationDegrees,
                    SourceWidth: page.SourceWidth, SourceHeight: page.SourceHeight,
                    SourceTransform: page.SourceTransform, IsBackground: page.IsBackground));
                foreach (var block in page.Blocks)
                {
                    if (block.Length == 0 || block.StartOffset >= pageEnd ||
                        (long)block.StartOffset + block.Length <= pageStart) continue;
                    var blockStart = Math.Max(pageStart, block.StartOffset);
                    var blockEnd = Math.Min(pageEnd, block.StartOffset + block.Length);
                    locations.Add(new CorpusLocation(
                        "block", blockStart, blockEnd - blockStart,
                        isImage ? null : page.PageIndex + 1, page.PageIndex, page.VisioPageId,
                        block.Method, block.ShapeId, block.Left, block.Top,
                        block.Width, block.Height, page.OrientationDegrees,
                        page.SourceWidth, page.SourceHeight, page.SourceTransform,
                        block.ParentShapeId, block.MasterId, block.MasterShapeId,
                        block.ConnectorFromShapeId, block.ConnectorToShapeId,
                        block.BeginArrow, block.EndArrow, page.IsBackground));
                    if (locations.Count >= MaximumLocations) break;
                }
                if (locations.Count >= MaximumLocations) break;
            }

            if (locations.Count == 1)
                return new CorpusCitation([span], null, ["location-unavailable"]);
            var warnings = locations.Count >= MaximumLocations ? new[] { "location-budget-reached" } : [];
            return new CorpusCitation(locations, methods.Count == 1 ? methods.Single() : null, warnings);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           ArgumentException or NullReferenceException)
        {
            return new CorpusCitation([span], null, ["provenance-invalid"]);
        }
    }

    private static bool IsImagePath(string? sourceIdentity) =>
        Path.GetExtension(sourceIdentity)?.ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".webp" or ".gif";

    private static bool IsTrustedGeometry(DocumentProvenance provenance)
    {
        var previousEnd = 0L;
        foreach (var page in provenance.Pages.OrderBy(static page => page.StartOffset))
        {
            if (page.StartOffset < previousEnd ||
                (long)page.StartOffset + page.Length > int.MaxValue ||
                page.PageIndex == int.MaxValue ||
                page.OrientationDegrees is not (null or 0 or 90 or 180 or 270) ||
                page.VisioPageId is < 0 ||
                page.SourceWidth.HasValue != page.SourceHeight.HasValue ||
                page.SourceTransform is not null && !page.SourceWidth.HasValue)
                return false;
            previousEnd = (long)page.StartOffset + page.Length;
            foreach (var block in page.Blocks)
            {
                if ((long)block.StartOffset + block.Length > int.MaxValue)
                    return false;
                var rectangleCount = (block.Left.HasValue ? 1 : 0) + (block.Top.HasValue ? 1 : 0) +
                                     (block.Width.HasValue ? 1 : 0) + (block.Height.HasValue ? 1 : 0);
                if (rectangleCount is not (0 or 4) ||
                    rectangleCount == 4 && (block.Left < 0 || block.Top < 0 ||
                                            block.Width <= 0 || block.Height <= 0 ||
                                            page.SourceWidth.HasValue &&
                                            ((long)block.Left.GetValueOrDefault() + block.Width.GetValueOrDefault() > page.SourceWidth ||
                                             (long)block.Top.GetValueOrDefault() + block.Height.GetValueOrDefault() > page.SourceHeight)))
                    return false;
                if (!IsSafeIdentifier(block.MasterShapeId) ||
                    !IsSafeIdentifier(block.BeginArrow) || !IsSafeIdentifier(block.EndArrow) ||
                    block.ParentShapeId is < 0 || block.MasterId is < 0 ||
                    block.ConnectorFromShapeId is < 0 || block.ConnectorToShapeId is < 0)
                    return false;
            }
        }
        return true;
    }

    private static bool IsSafeIdentifier(string? value) => value is null ||
        value.Length <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
