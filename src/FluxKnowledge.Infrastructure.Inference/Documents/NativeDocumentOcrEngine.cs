using System.Text;
using FluxKnowledge.Application.Documents;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FluxKnowledge.Infrastructure.Inference.Documents;

public sealed record NativeDocumentOcrTextBlock(
    int PageIndex,
    int Left,
    int Top,
    int Width,
    int Height,
    string Text,
    float Confidence);

public sealed record NativeDocumentOcrResult(
    bool Succeeded,
    string ReasonCode,
    string Text,
    IReadOnlyList<NativeDocumentOcrTextBlock> TextBlocks);

/// <summary>
/// Native DirectML OCR for already-rendered, app-owned PDF page PNGs. It has no source
/// paths, provider fallback or acquisition behaviour; model sessions arrive from the
/// verified local runtime.
/// </summary>
public sealed class NativeDocumentOcrEngine(DocumentOcrRuntime runtime)
{
    private const int MaximumPagePixels = 16 * 1024 * 1024;
    private const int MaximumDetectionSide = 960;
    private const int DetectionStride = 32;
    private const int RecognitionHeight = 48;
    private const int RecognitionWidth = 320;
    private const int MaximumBlocksPerPage = 3_000;
    private const int MaximumExtractedUtf8Bytes = 16 * 1024 * 1024;

    public async ValueTask<NativeDocumentOcrResult> ExtractAsync(
        IReadOnlyList<PdfRasterizedPage> pages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            return Refused("document-ocr-no-pages");
        }

        var opened = await runtime.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!opened.Succeeded || opened.Lease is null)
        {
            return new NativeDocumentOcrResult(false, opened.ReasonCode, string.Empty, []);
        }

        using var lease = opened.Lease;
        try
        {
            var englishCharacters = await DocumentOcrModelConfiguration
                .ReadCtcCharactersAsync(lease.GetModelLease(DocumentOcrModelRole.EnglishRecognition), cancellationToken)
                .ConfigureAwait(false);
            var arabicCharacters = await DocumentOcrModelConfiguration
                .ReadCtcCharactersAsync(lease.GetModelLease(DocumentOcrModelRole.ArabicRecognition), cancellationToken)
                .ConfigureAwait(false);
            var detector = lease.GetSession<DirectMlDocumentOcrSession>(DocumentOcrModelRole.TextDetection);
            var englishRecognizer = lease.GetSession<DirectMlDocumentOcrSession>(DocumentOcrModelRole.EnglishRecognition);
            var arabicRecognizer = lease.GetSession<DirectMlDocumentOcrSession>(DocumentOcrModelRole.ArabicRecognition);
            var blocks = new List<NativeDocumentOcrTextBlock>();
            foreach (var page in pages.OrderBy(static page => page.PageIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = SKBitmap.Decode(page.PngBytes);
                if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 ||
                    checked((long)bitmap.Width * bitmap.Height) > MaximumPagePixels)
                {
                    return Refused("document-ocr-page-invalid");
                }

                var pageBlocks = Detect(bitmap, detector, cancellationToken);
                if (pageBlocks.Count > MaximumBlocksPerPage)
                {
                    return Refused("document-ocr-block-limit-exceeded");
                }

                foreach (var block in pageBlocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var english = Recognize(bitmap, block, englishRecognizer, englishCharacters, cancellationToken);
                    var selected = english;
                    if (english.Text.Length == 0 || english.MeanConfidence < 0.80f)
                    {
                        var arabic = Recognize(bitmap, block, arabicRecognizer, arabicCharacters, cancellationToken);
                        if (arabic.MeanConfidence > english.MeanConfidence)
                        {
                            selected = arabic;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(selected.Text)) continue;
                    blocks.Add(new NativeDocumentOcrTextBlock(
                        page.PageIndex,
                        block.Left,
                        block.Top,
                        block.Width,
                        block.Height,
                        selected.Text,
                        selected.MeanConfidence));
                }
            }

            var text = BuildText(blocks);
            return string.IsNullOrWhiteSpace(text)
                ? Refused("document-ocr-no-text")
                : new NativeDocumentOcrResult(true, "document-ocr-complete", text, blocks.AsReadOnly());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("document-ocr-", StringComparison.Ordinal))
        {
            return Refused(exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Refused("document-ocr-inference-failed");
        }
    }

    private static List<DetectedBlock> Detect(
        SKBitmap bitmap,
        DirectMlDocumentOcrSession session,
        CancellationToken cancellationToken)
    {
        var (width, height) = DetectionDimensions(bitmap.Width, bitmap.Height);
        var input = new DenseTensor<float>([1, 3, height, width]);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = Math.Min(bitmap.Height - 1, (int)((long)y * bitmap.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(bitmap.Width - 1, (int)((long)x * bitmap.Width / width));
                var color = bitmap.GetPixel(sourceX, sourceY);
                input[0, 0, y, x] = (color.Blue / 255f - 0.485f) / 0.229f;
                input[0, 1, y, x] = (color.Green / 255f - 0.456f) / 0.224f;
                input[0, 2, y, x] = (color.Red / 255f - 0.406f) / 0.225f;
            }
        }

        using var results = session.Session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames.Single(), input)]);
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions;
        if (dimensions.Length is < 2 or > 4)
        {
            throw new InvalidOperationException("document-ocr-detection-output-invalid");
        }

        var mapHeight = dimensions[^2];
        var mapWidth = dimensions[^1];
        if (mapHeight <= 0 || mapWidth <= 0)
        {
            throw new InvalidOperationException("document-ocr-detection-output-invalid");
        }

        var values = output.ToArray();
        return ConnectedComponents(values, mapWidth, mapHeight, bitmap.Width, bitmap.Height);
    }

    private static CtcTextDecoding Recognize(
        SKBitmap bitmap,
        DetectedBlock block,
        DirectMlDocumentOcrSession session,
        IReadOnlyList<string> characters,
        CancellationToken cancellationToken)
    {
        var contentWidth = Math.Clamp(
            (int)Math.Ceiling((double)block.Width * RecognitionHeight / block.Height),
            16,
            RecognitionWidth);
        var input = new DenseTensor<float>([1, 3, RecognitionHeight, RecognitionWidth]);
        for (var y = 0; y < RecognitionHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = Math.Min(block.Top + block.Height - 1, block.Top + (int)((long)y * block.Height / RecognitionHeight));
            for (var x = 0; x < contentWidth; x++)
            {
                var sourceX = Math.Min(block.Left + block.Width - 1, block.Left + (int)((long)x * block.Width / contentWidth));
                var color = bitmap.GetPixel(sourceX, sourceY);
                input[0, 0, y, x] = color.Blue / 127.5f - 1f;
                input[0, 1, y, x] = color.Green / 127.5f - 1f;
                input[0, 2, y, x] = color.Red / 127.5f - 1f;
            }
        }

        using var results = session.Session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames.Single(), input)]);
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions;
        var hasUnknownClass = dimensions.Length == 3 && dimensions[2] == characters.Count + 2;
        if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[1] < 1 ||
            dimensions[2] != characters.Count + 1 + (hasUnknownClass ? 1 : 0))
        {
            throw new InvalidOperationException(
                $"document-ocr-recognition-output-invalid:{string.Join('x', dimensions.ToArray())}:{characters.Count + 1}");
        }

        var values = output.ToArray();
        var probabilities = new float[dimensions[1], dimensions[2]];
        for (var timeStep = 0; timeStep < dimensions[1]; timeStep++)
        {
            for (var classIndex = 0; classIndex < dimensions[2]; classIndex++)
            {
                probabilities[timeStep, classIndex] = values[(timeStep * dimensions[2]) + classIndex];
            }
        }

        return CtcTextDecoder.Decode(probabilities, characters, hasUnknownClass);
    }

    private static List<DetectedBlock> ConnectedComponents(
        float[] values,
        int mapWidth,
        int mapHeight,
        int originalWidth,
        int originalHeight)
    {
        if (values.Length < checked(mapWidth * mapHeight))
        {
            throw new InvalidOperationException("document-ocr-detection-output-invalid");
        }

        var visited = new bool[checked(mapWidth * mapHeight)];
        var blocks = new List<DetectedBlock>();
        for (var start = 0; start < visited.Length; start++)
        {
            if (visited[start] || values[start] < 0.20f) continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            var count = 0;
            var confidenceTotal = 0f;
            var left = mapWidth;
            var top = mapHeight;
            var right = -1;
            var bottom = -1;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var y = current / mapWidth;
                var x = current % mapWidth;
                count++;
                confidenceTotal += values[current];
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                EnqueueIfCandidate(x - 1, y);
                EnqueueIfCandidate(x + 1, y);
                EnqueueIfCandidate(x, y - 1);
                EnqueueIfCandidate(x, y + 1);
            }

            if (count < 4 || confidenceTotal / count < 0.45f) continue;
            var sourceLeft = Math.Max(0, (int)Math.Floor((double)left * originalWidth / mapWidth) - 2);
            var sourceTop = Math.Max(0, (int)Math.Floor((double)top * originalHeight / mapHeight) - 2);
            var sourceRight = Math.Min(originalWidth, (int)Math.Ceiling((double)(right + 1) * originalWidth / mapWidth) + 2);
            var sourceBottom = Math.Min(originalHeight, (int)Math.Ceiling((double)(bottom + 1) * originalHeight / mapHeight) + 2);
            if (sourceRight - sourceLeft < 8 || sourceBottom - sourceTop < 8) continue;
            blocks.Add(new DetectedBlock(sourceLeft, sourceTop, sourceRight - sourceLeft, sourceBottom - sourceTop));

            void EnqueueIfCandidate(int x, int y)
            {
                if (x < 0 || x >= mapWidth || y < 0 || y >= mapHeight) return;
                var index = (y * mapWidth) + x;
                if (visited[index] || values[index] < 0.20f) return;
                visited[index] = true;
                queue.Enqueue(index);
            }
        }

        return blocks
            .OrderBy(static block => block.Top)
            .ThenBy(static block => block.Left)
            .ToList();
    }

    private static (int Width, int Height) DetectionDimensions(int originalWidth, int originalHeight)
    {
        var scale = Math.Min(1d, MaximumDetectionSide / (double)Math.Max(originalWidth, originalHeight));
        var width = Math.Max(DetectionStride, (int)Math.Ceiling(originalWidth * scale / DetectionStride) * DetectionStride);
        var height = Math.Max(DetectionStride, (int)Math.Ceiling(originalHeight * scale / DetectionStride) * DetectionStride);
        return (width, height);
    }

    private static string BuildText(IReadOnlyList<NativeDocumentOcrTextBlock> blocks)
    {
        var builder = new StringBuilder();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var previousPage = -1;
        var totalBytes = 0;
        foreach (var block in blocks)
        {
            var separator = builder.Length == 0 ? string.Empty :
                block.PageIndex == previousPage ? Environment.NewLine : Environment.NewLine + Environment.NewLine;
            var addition = separator + block.Text;
            totalBytes = checked(totalBytes + utf8.GetByteCount(addition));
            if (totalBytes > MaximumExtractedUtf8Bytes)
            {
                throw new InvalidOperationException("document-ocr-output-too-large");
            }

            builder.Append(addition);
            previousPage = block.PageIndex;
        }

        return builder.ToString();
    }

    private static NativeDocumentOcrResult Refused(string reasonCode) => new(false, reasonCode, string.Empty, []);

    private sealed record DetectedBlock(int Left, int Top, int Width, int Height);
}
