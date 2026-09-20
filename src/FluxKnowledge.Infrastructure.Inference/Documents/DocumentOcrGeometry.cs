using SkiaSharp;

namespace FluxKnowledge.Infrastructure.Inference.Documents;

public readonly record struct DocumentOcrPoint(float X, float Y);

public sealed class DocumentOcrQuadrilateral
{
    private readonly DocumentOcrPoint[] _points;

    public DocumentOcrQuadrilateral(IReadOnlyList<DocumentOcrPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count != 4)
        {
            throw new ArgumentException("An OCR quadrilateral must contain four points.", nameof(points));
        }

        _points = points.ToArray();
        if (_points.Any(static point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new ArgumentException("An OCR quadrilateral cannot contain a non-finite point.", nameof(points));
        }
    }

    public IReadOnlyList<DocumentOcrPoint> Points => _points;

    public float Width => MathF.Max(
        Distance(_points[0], _points[1]),
        Distance(_points[2], _points[3]));

    public float Height => MathF.Max(
        Distance(_points[0], _points[3]),
        Distance(_points[1], _points[2]));

    private static float Distance(DocumentOcrPoint first, DocumentOcrPoint second) => MathF.Sqrt(
        ((first.X - second.X) * (first.X - second.X)) +
        ((first.Y - second.Y) * (first.Y - second.Y)));
}

public sealed record DocumentOcrDetectedRegion(
    IReadOnlyList<DocumentOcrPoint> Polygon,
    float Confidence)
{
    public float Width => new DocumentOcrQuadrilateral(Polygon).Width;

    public float Height => new DocumentOcrQuadrilateral(Polygon).Height;
}

/// <summary>
/// DB text-detection postprocessing for the fixed PP-OCR detector configuration.
/// It reduces binary components to their minimum quadrilateral and expands the
/// quadrilateral using DB's area/perimeter unclip distance before source scaling.
/// </summary>
public static class DocumentOcrDbPostProcessor
{
    public const float Threshold = 0.20f;
    public const float BoxThreshold = 0.45f;
    public const float UnclipRatio = 1.40f;
    public const int MaximumCandidates = 3_000;

    public static IReadOnlyList<DocumentOcrDetectedRegion> Extract(
        float[,] probabilities,
        int sourceWidth,
        int sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        var mapHeight = probabilities.GetLength(0);
        var mapWidth = probabilities.GetLength(1);
        if (mapHeight == 0 || mapWidth == 0)
        {
            throw new ArgumentException("The detection probability map cannot be empty.", nameof(probabilities));
        }

        var visited = new bool[checked(mapWidth * mapHeight)];
        var regions = new List<DocumentOcrDetectedRegion>();
        for (var start = 0; start < visited.Length && regions.Count < MaximumCandidates; start++)
        {
            if (visited[start] || probabilities[start / mapWidth, start % mapWidth] < Threshold) continue;

            var component = ReadComponent(start, probabilities, visited);
            if (component.Pixels.Count < 3 || component.MeanConfidence < BoxThreshold) continue;

            var quadrilateral = MinimumAreaQuadrilateral(component.Pixels);
            if (quadrilateral.Width < 3 || quadrilateral.Height < 3) continue;

            var expanded = Expand(quadrilateral, UnclipRatio);
            if (expanded.Width < 3 || expanded.Height < 3) continue;

            var scaled = expanded.Points
                .Select(point => new DocumentOcrPoint(
                    Math.Clamp(point.X * sourceWidth / mapWidth, 0, sourceWidth - 1),
                    Math.Clamp(point.Y * sourceHeight / mapHeight, 0, sourceHeight - 1)))
                .ToArray();
            regions.Add(new DocumentOcrDetectedRegion(scaled, component.MeanConfidence));
        }

        return regions
            .OrderBy(static region => region.Polygon.Min(static point => point.Y))
            .ThenBy(static region => region.Polygon.Min(static point => point.X))
            .ToArray();
    }

    private static Component ReadComponent(int start, float[,] probabilities, bool[] visited)
    {
        var height = probabilities.GetLength(0);
        var width = probabilities.GetLength(1);
        var queue = new Queue<int>();
        var pixels = new List<DocumentOcrPoint>();
        var total = 0f;
        queue.Enqueue(start);
        visited[start] = true;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var y = current / width;
            var x = current % width;
            total += probabilities[y, x];
            pixels.Add(new DocumentOcrPoint(x, y));
            pixels.Add(new DocumentOcrPoint(x + 1, y));
            pixels.Add(new DocumentOcrPoint(x + 1, y + 1));
            pixels.Add(new DocumentOcrPoint(x, y + 1));
            for (var deltaY = -1; deltaY <= 1; deltaY++)
            {
                for (var deltaX = -1; deltaX <= 1; deltaX++)
                {
                    if (deltaX == 0 && deltaY == 0) continue;
                    var neighbourX = x + deltaX;
                    var neighbourY = y + deltaY;
                    if (neighbourX < 0 || neighbourX >= width || neighbourY < 0 || neighbourY >= height) continue;
                    var neighbour = (neighbourY * width) + neighbourX;
                    if (visited[neighbour] || probabilities[neighbourY, neighbourX] < Threshold) continue;
                    visited[neighbour] = true;
                    queue.Enqueue(neighbour);
                }
            }
        }

        return new Component(pixels, total / (pixels.Count / 4f));
    }

    private static DocumentOcrQuadrilateral MinimumAreaQuadrilateral(IReadOnlyList<DocumentOcrPoint> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3)
        {
            throw new InvalidOperationException("document-ocr-detection-contour-invalid");
        }

        var bestArea = float.PositiveInfinity;
        DocumentOcrPoint[]? best = null;
        for (var index = 0; index < hull.Count; index++)
        {
            var first = hull[index];
            var second = hull[(index + 1) % hull.Count];
            var deltaX = second.X - first.X;
            var deltaY = second.Y - first.Y;
            var length = MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (length <= 0.0001f) continue;
            var axisX = deltaX / length;
            var axisY = deltaY / length;
            var normalX = -axisY;
            var normalY = axisX;
            var minAxis = float.PositiveInfinity;
            var maxAxis = float.NegativeInfinity;
            var minNormal = float.PositiveInfinity;
            var maxNormal = float.NegativeInfinity;
            foreach (var point in hull)
            {
                var axis = (point.X * axisX) + (point.Y * axisY);
                var normal = (point.X * normalX) + (point.Y * normalY);
                minAxis = MathF.Min(minAxis, axis);
                maxAxis = MathF.Max(maxAxis, axis);
                minNormal = MathF.Min(minNormal, normal);
                maxNormal = MathF.Max(maxNormal, normal);
            }

            var area = (maxAxis - minAxis) * (maxNormal - minNormal);
            if (area >= bestArea) continue;
            bestArea = area;
            best =
            [
                FromAxes(minAxis, minNormal, axisX, axisY, normalX, normalY),
                FromAxes(maxAxis, minNormal, axisX, axisY, normalX, normalY),
                FromAxes(maxAxis, maxNormal, axisX, axisY, normalX, normalY),
                FromAxes(minAxis, maxNormal, axisX, axisY, normalX, normalY)
            ];
        }

        return new DocumentOcrQuadrilateral(OrderForPerspective(best ?? throw new InvalidOperationException("document-ocr-detection-contour-invalid")));
    }

    private static DocumentOcrQuadrilateral Expand(DocumentOcrQuadrilateral quadrilateral, float ratio)
    {
        var points = quadrilateral.Points;
        var area = MathF.Abs(SignedArea(points));
        var perimeter = 0f;
        for (var index = 0; index < points.Count; index++)
        {
            perimeter += Distance(points[index], points[(index + 1) % points.Count]);
        }

        if (area <= 0.0001f || perimeter <= 0.0001f)
        {
            throw new InvalidOperationException("document-ocr-detection-contour-invalid");
        }

        var distance = (area * ratio) / perimeter;
        var outward = new Line[points.Count];
        var clockwiseInImageSpace = SignedArea(points) > 0;
        for (var index = 0; index < points.Count; index++)
        {
            var first = points[index];
            var second = points[(index + 1) % points.Count];
            var deltaX = second.X - first.X;
            var deltaY = second.Y - first.Y;
            var length = MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (length <= 0.0001f) throw new InvalidOperationException("document-ocr-detection-contour-invalid");
            var normalX = clockwiseInImageSpace ? deltaY / length : -deltaY / length;
            var normalY = clockwiseInImageSpace ? -deltaX / length : deltaX / length;
            outward[index] = new Line(
                new DocumentOcrPoint(first.X + (normalX * distance), first.Y + (normalY * distance)),
                new DocumentOcrPoint(deltaX, deltaY));
        }

        var expanded = new DocumentOcrPoint[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            expanded[index] = Intersect(outward[(index + points.Count - 1) % points.Count], outward[index]);
        }

        return new DocumentOcrQuadrilateral(OrderForPerspective(expanded));
    }

    private static IReadOnlyList<DocumentOcrPoint> ConvexHull(IReadOnlyList<DocumentOcrPoint> points)
    {
        var sorted = points
            .Distinct()
            .OrderBy(static point => point.X)
            .ThenBy(static point => point.Y)
            .ToArray();
        var lower = new List<DocumentOcrPoint>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }

        var upper = new List<DocumentOcrPoint>();
        for (var index = sorted.Length - 1; index >= 0; index--)
        {
            var point = sorted[index];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static DocumentOcrPoint[] OrderForPerspective(IReadOnlyList<DocumentOcrPoint> points)
    {
        var topLeft = points.OrderBy(static point => point.X + point.Y).First();
        var bottomRight = points.OrderByDescending(static point => point.X + point.Y).First();
        var remaining = points.Where(point => point != topLeft && point != bottomRight).ToArray();
        var topRight = remaining.OrderByDescending(static point => point.X - point.Y).First();
        var bottomLeft = remaining.OrderBy(static point => point.X - point.Y).First();
        return [topLeft, topRight, bottomRight, bottomLeft];
    }

    private static DocumentOcrPoint FromAxes(float axis, float normal, float axisX, float axisY, float normalX, float normalY) => new(
        (axis * axisX) + (normal * normalX),
        (axis * axisY) + (normal * normalY));

    private static float SignedArea(IReadOnlyList<DocumentOcrPoint> points)
    {
        var area = 0f;
        for (var index = 0; index < points.Count; index++)
        {
            var first = points[index];
            var second = points[(index + 1) % points.Count];
            area += (first.X * second.Y) - (second.X * first.Y);
        }

        return area / 2f;
    }

    private static float Distance(DocumentOcrPoint first, DocumentOcrPoint second) => MathF.Sqrt(
        ((first.X - second.X) * (first.X - second.X)) +
        ((first.Y - second.Y) * (first.Y - second.Y)));

    private static float Cross(DocumentOcrPoint first, DocumentOcrPoint second, DocumentOcrPoint third) =>
        ((second.X - first.X) * (third.Y - first.Y)) - ((second.Y - first.Y) * (third.X - first.X));

    private static DocumentOcrPoint Intersect(Line first, Line second)
    {
        var denominator = (first.Direction.X * second.Direction.Y) - (first.Direction.Y * second.Direction.X);
        if (MathF.Abs(denominator) <= 0.0001f)
        {
            throw new InvalidOperationException("document-ocr-detection-contour-invalid");
        }

        var deltaX = second.Point.X - first.Point.X;
        var deltaY = second.Point.Y - first.Point.Y;
        var scale = ((deltaX * second.Direction.Y) - (deltaY * second.Direction.X)) / denominator;
        return new DocumentOcrPoint(
            first.Point.X + (scale * first.Direction.X),
            first.Point.Y + (scale * first.Direction.Y));
    }

    private sealed record Component(IReadOnlyList<DocumentOcrPoint> Pixels, float MeanConfidence);

    private readonly record struct Line(DocumentOcrPoint Point, DocumentOcrPoint Direction);
}

public static class DocumentOcrPerspectiveCropper
{
    public static SKBitmap Crop(SKBitmap source, DocumentOcrQuadrilateral quadrilateral)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(quadrilateral);
        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new ArgumentException("The OCR source image cannot be empty.", nameof(source));
        }

        var width = Math.Max(1, (int)Math.Ceiling(quadrilateral.Width));
        var height = Math.Max(1, (int)Math.Ceiling(quadrilateral.Height));
        var transform = Homography.FromDestinationRectangle(width, height, quadrilateral.Points);
        var cropped = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourcePoint = transform.Map(x, y);
                cropped.SetPixel(x, y, SampleBilinear(source, sourcePoint));
            }
        }

        return cropped;
    }

    private static SKColor SampleBilinear(SKBitmap source, DocumentOcrPoint point)
    {
        var x = Math.Clamp(point.X, 0, source.Width - 1);
        var y = Math.Clamp(point.Y, 0, source.Height - 1);
        var left = (int)MathF.Floor(x);
        var top = (int)MathF.Floor(y);
        var right = Math.Min(left + 1, source.Width - 1);
        var bottom = Math.Min(top + 1, source.Height - 1);
        var horizontal = x - left;
        var vertical = y - top;
        var topColor = Blend(source.GetPixel(left, top), source.GetPixel(right, top), horizontal);
        var bottomColor = Blend(source.GetPixel(left, bottom), source.GetPixel(right, bottom), horizontal);
        return Blend(topColor, bottomColor, vertical);
    }

    private static SKColor Blend(SKColor first, SKColor second, float amount) => new(
        (byte)Math.Clamp(MathF.Round(first.Red + ((second.Red - first.Red) * amount)), 0, 255),
        (byte)Math.Clamp(MathF.Round(first.Green + ((second.Green - first.Green) * amount)), 0, 255),
        (byte)Math.Clamp(MathF.Round(first.Blue + ((second.Blue - first.Blue) * amount)), 0, 255),
        (byte)Math.Clamp(MathF.Round(first.Alpha + ((second.Alpha - first.Alpha) * amount)), 0, 255));

    private readonly record struct Homography(
        double A, double B, double C,
        double D, double E, double F,
        double G, double H)
    {
        public DocumentOcrPoint Map(double x, double y)
        {
            var divisor = (G * x) + (H * y) + 1d;
            if (Math.Abs(divisor) < 0.0000001d)
            {
                throw new InvalidOperationException("document-ocr-perspective-transform-invalid");
            }

            return new DocumentOcrPoint(
                (float)(((A * x) + (B * y) + C) / divisor),
                (float)(((D * x) + (E * y) + F) / divisor));
        }

        public static Homography FromDestinationRectangle(int width, int height, IReadOnlyList<DocumentOcrPoint> destination)
        {
            var source = new[]
            {
                new DocumentOcrPoint(0, 0),
                new DocumentOcrPoint(width - 1, 0),
                new DocumentOcrPoint(width - 1, height - 1),
                new DocumentOcrPoint(0, height - 1)
            };
            var equations = new double[8, 9];
            for (var index = 0; index < 4; index++)
            {
                var x = source[index].X;
                var y = source[index].Y;
                var targetX = destination[index].X;
                var targetY = destination[index].Y;
                equations[index * 2, 0] = x;
                equations[index * 2, 1] = y;
                equations[index * 2, 2] = 1;
                equations[index * 2, 6] = -x * targetX;
                equations[index * 2, 7] = -y * targetX;
                equations[index * 2, 8] = targetX;
                equations[(index * 2) + 1, 3] = x;
                equations[(index * 2) + 1, 4] = y;
                equations[(index * 2) + 1, 5] = 1;
                equations[(index * 2) + 1, 6] = -x * targetY;
                equations[(index * 2) + 1, 7] = -y * targetY;
                equations[(index * 2) + 1, 8] = targetY;
            }

            var values = Solve(equations);
            return new Homography(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
        }

        private static double[] Solve(double[,] matrix)
        {
            for (var pivot = 0; pivot < 8; pivot++)
            {
                var best = pivot;
                for (var row = pivot + 1; row < 8; row++)
                {
                    if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
                }

                if (Math.Abs(matrix[best, pivot]) < 0.0000001d)
                {
                    throw new InvalidOperationException("document-ocr-perspective-transform-invalid");
                }

                if (best != pivot)
                {
                    for (var column = pivot; column < 9; column++)
                    {
                        (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
                    }
                }

                var divisor = matrix[pivot, pivot];
                for (var column = pivot; column < 9; column++) matrix[pivot, column] /= divisor;
                for (var row = 0; row < 8; row++)
                {
                    if (row == pivot) continue;
                    var factor = matrix[row, pivot];
                    for (var column = pivot; column < 9; column++) matrix[row, column] -= factor * matrix[pivot, column];
                }
            }

            return Enumerable.Range(0, 8).Select(index => matrix[index, 8]).ToArray();
        }
    }
}
