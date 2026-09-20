using FluxKnowledge.Infrastructure.Inference.Documents;
using SkiaSharp;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class DocumentOcrGeometryTests
{
    [Fact]
    public void Db_postprocessor_returns_a_rotated_quadrilateral_for_a_slanted_text_region()
    {
        var probabilities = new float[20, 20];
        FillConvexQuadrilateral(
            probabilities,
            new DocumentOcrPoint(3, 5),
            new DocumentOcrPoint(15, 2),
            new DocumentOcrPoint(17, 8),
            new DocumentOcrPoint(5, 11),
            0.95f);

        var regions = DocumentOcrDbPostProcessor.Extract(
            probabilities,
            sourceWidth: 200,
            sourceHeight: 200);

        var region = Assert.Single(regions);
        Assert.Equal(4, region.Polygon.Count);
        Assert.InRange(region.Confidence, 0.90f, 1f);
        Assert.Contains(region.Polygon, static point => point.X != MathF.Round(point.X));
        Assert.True(region.Width > region.Height);
    }

    [Fact]
    public void Perspective_cropper_maps_each_destination_corner_to_its_source_quadrilateral()
    {
        using var source = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                source.SetPixel(x, y, new SKColor((byte)x, (byte)y, 0));
            }
        }

        var quadrilateral = new DocumentOcrQuadrilateral(
        [
            new DocumentOcrPoint(20, 20),
            new DocumentOcrPoint(80, 30),
            new DocumentOcrPoint(75, 50),
            new DocumentOcrPoint(15, 40)
        ]);

        using var cropped = DocumentOcrPerspectiveCropper.Crop(source, quadrilateral);

        Assert.InRange(cropped.Width, 60, 62);
        Assert.InRange(cropped.Height, 21, 23);
        AssertClose(cropped.GetPixel(0, 0), expectedRed: 20, expectedGreen: 20);
        AssertClose(cropped.GetPixel(cropped.Width - 1, 0), expectedRed: 80, expectedGreen: 30);
        AssertClose(cropped.GetPixel(0, cropped.Height - 1), expectedRed: 15, expectedGreen: 40);
        AssertClose(cropped.GetPixel(cropped.Width - 1, cropped.Height - 1), expectedRed: 75, expectedGreen: 50);
    }

    private static void FillConvexQuadrilateral(
        float[,] probabilities,
        DocumentOcrPoint first,
        DocumentOcrPoint second,
        DocumentOcrPoint third,
        DocumentOcrPoint fourth,
        float value)
    {
        var polygon = new[] { first, second, third, fourth };
        for (var y = 0; y < probabilities.GetLength(0); y++)
        {
            for (var x = 0; x < probabilities.GetLength(1); x++)
            {
                if (IsInsideConvexPolygon(new DocumentOcrPoint(x + 0.5f, y + 0.5f), polygon))
                {
                    probabilities[y, x] = value;
                }
            }
        }
    }

    private static bool IsInsideConvexPolygon(DocumentOcrPoint point, IReadOnlyList<DocumentOcrPoint> polygon)
    {
        var sign = 0;
        for (var index = 0; index < polygon.Count; index++)
        {
            var first = polygon[index];
            var second = polygon[(index + 1) % polygon.Count];
            var cross = ((second.X - first.X) * (point.Y - first.Y)) - ((second.Y - first.Y) * (point.X - first.X));
            if (MathF.Abs(cross) < 0.001f) continue;
            var currentSign = cross > 0 ? 1 : -1;
            if (sign != 0 && sign != currentSign) return false;
            sign = currentSign;
        }

        return true;
    }

    private static void AssertClose(SKColor actual, byte expectedRed, byte expectedGreen)
    {
        Assert.InRange(actual.Red, expectedRed - 2, expectedRed + 2);
        Assert.InRange(actual.Green, expectedGreen - 2, expectedGreen + 2);
    }
}
