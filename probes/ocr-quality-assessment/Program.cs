using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OcrQualityAssessment;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            return args[0] switch
            {
                "self-test" => SelfTest(),
                "generate" => Generate(args[1..]),
                "evaluate" => Evaluate(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}")
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"Harness failure: {exception.Message}");
            return 2;
        }
    }

    private const string Usage = """
OCR quality assessment harness — synthetic screening only; it is not a release gate.

Commands:
  generate --output <directory> [--degraded-copy]
      Writes 30 public English PNG fixtures, truth.json and contact-sheet.png.
      --degraded-copy also writes a deterministic downsampled/upscaled copy for every page.
  evaluate --truth <truth.json> --results <candidate-results.json> [--report <report.json>]
      Scores Unicode-scalar CER, declared reading order, tables and exact critical values.
  self-test
      Runs dependency-free harness checks. It never loads a model.

Candidate results must supply candidateName, provenance { engine, version, runId }, and samples.
Each sample supplies id, pageText, blockOrder, blocks [{ id, text, x, y, width, height }], and tables [{ id, rows: [["cell"]] }].
Missing provenance makes overallPass false even if text matches.
""";

    private static int SelfTest()
    {
        HarnessSelfTests.Run();
        Console.WriteLine("Self-tests passed. No OCR model was loaded.");
        return 0;
    }

    private static int Generate(string[] args)
    {
        var output = RequiredArgument(args, "--output");
        var degradedCopy = args.Contains("--degraded-copy", StringComparer.Ordinal);
        var outputDirectory = Path.GetFullPath(output);
        Directory.CreateDirectory(outputDirectory);
        var pageDirectory = Path.Combine(outputDirectory, "pages");
        Directory.CreateDirectory(pageDirectory);

        var rendered = new List<RenderedFixture>();
        foreach (var fixture in FixtureCatalog.Create())
        {
            var relativePath = Path.Combine("pages", fixture.Id + ".png").Replace('\\', '/');
            var fullPath = Path.Combine(outputDirectory, relativePath);
            FixtureRenderer.Render(fixture, fullPath, degraded: fixture.DegradedScan);
            rendered.Add(new RenderedFixture(fixture, relativePath));

            if (degradedCopy)
            {
                var copyPath = Path.Combine(outputDirectory, "pages", "degraded", fixture.Id + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
                FixtureRenderer.Render(fixture, copyPath, degraded: true);
            }
        }

        var truth = new BenchmarkTruth(
            "synthetic-screening-not-release-gate",
            "English-only public literal text. No customer, licence or production material.",
            rendered.Select(static item => item.ToTruth()).ToArray());
        var truthPath = Path.Combine(outputDirectory, "truth.json");
        WriteJson(truthPath, truth);
        var contactSheetPath = Path.Combine(outputDirectory, "contact-sheet.png");
        FixtureRenderer.RenderContactSheet(rendered, outputDirectory, contactSheetPath);

        Console.WriteLine("Generated 30 synthetic English screening pages. This is not a release gate.");
        Console.WriteLine($"Truth: {truthPath}");
        Console.WriteLine($"Contact sheet: {contactSheetPath}");
        return 0;
    }

    private static int Evaluate(string[] args)
    {
        var truthPath = Path.GetFullPath(RequiredArgument(args, "--truth"));
        var resultsPath = Path.GetFullPath(RequiredArgument(args, "--results"));
        var reportPath = OptionalArgument(args, "--report") is { } reportOption ? Path.GetFullPath(reportOption) : null;
        var truth = ReadJson<BenchmarkTruth>(truthPath);
        var candidate = ReadJson<CandidateResults>(resultsPath);
        var report = Evaluation.Evaluate(truth, candidate);
        if (reportPath is not null) WriteJson(reportPath, report);
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return report.OverallPass ? 0 : 1;
    }

    private static string RequiredArgument(string[] args, string name) =>
        OptionalArgument(args, name) ?? throw new ArgumentException($"Required argument {name} was not provided.");

    private static string? OptionalArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"No JSON value in {path}.");

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

internal static class FixtureCatalog
{
    public static IReadOnlyList<Fixture> Create()
    {
        var fixtures = new List<Fixture>(30);
        for (var index = 1; index <= 30; index++) fixtures.Add(CreateEnglishFixture(index));
        return fixtures;
    }

    private static Fixture CreateEnglishFixture(int index)
    {
        var id = $"en-{index:D2}";
        var reference = $"REF-{202600 + index:D6}";
        var amount = (120m + (index * 3.45m)).ToString("F2", CultureInfo.InvariantCulture);
        var rotation = index is >= 21 and <= 25 ? (index % 2 == 0 ? 180 : 90) : 0;
        var layout = index is >= 9 and <= 14 ? "columns" : index is >= 15 and <= 20 ? "table" : "single";
        var heading = $"Screening record {index:D2}";
        var lineOne = $"Reference {reference} records an exact amount of {amount} USD.";
        var lineTwo = $"The English fixture is public synthetic text for OCR measurement.";
        var lineThree = $"Review date: 2026-09-{index:D2}; retain the punctuation exactly.";
        var columnFirst = $"Reference {reference}; amount {amount} USD.";
        var columnSecond = "Synthetic English OCR measurement text.";
        var columnThird = $"Review: 2026-09-{index:D2}; punctuation exact.";
        IReadOnlyList<TextBlock> blocks = layout switch
        {
            "columns" =>
            [
                new TextBlock("heading", heading, 80, 90, 1080, 46),
                new TextBlock("left-1", columnFirst, 80, 210, 500, 42),
                new TextBlock("left-2", $"Column A code: A-{index:D2}-2026.", 80, 300, 500, 42),
                new TextBlock("right-1", columnSecond, 650, 210, 500, 42),
                new TextBlock("right-2", columnThird, 650, 320, 500, 42)
            ],
            "table" =>
            [
                new TextBlock("heading", heading, 80, 90, 1080, 46),
                new TextBlock("intro", $"Invoice values for {reference}:", 80, 180, 1080, 42)
            ],
            _ =>
            [
                new TextBlock("heading", heading, 80, 90, 1080, 46),
                new TextBlock("line-1", lineOne, 80, 210, 1080, 42),
                new TextBlock("line-2", lineTwo, 80, 300, 1080, 42),
                new TextBlock("line-3", lineThree, 80, 390, 1080, 42)
            ]
        };
        var table = layout == "table"
            ? new FixtureTable("amounts", [["Item", "Reference", "Amount"], ["Service", reference, amount], ["Tax", $"T-{index:D2}", "15.00"]])
            : null;
        // PageText is deliberately stated at fixture definition time, not recovered from a rendered image.
        var pageText = layout switch
        {
            "columns" => $"{heading}\n{columnFirst}\nColumn A code: A-{index:D2}-2026.\n{columnSecond}\n{columnThird}",
            "table" => $"{heading}\nInvoice values for {reference}:\nItem\tReference\tAmount\nService\t{reference}\t{amount}\nTax\tT-{index:D2}\t15.00",
            _ => $"{heading}\n{lineOne}\n{lineTwo}\n{lineThree}"
        };
        var order = blocks.Select(static block => block.Id)
            .Concat(table is null ? Enumerable.Empty<string>() : [table.Id])
            .ToArray();
        var qualities = new[] { "clean-high-resolution", "low-resolution", "jpeg-compression", "blur-low-contrast", "skew-rotation" };
        var styles = new[] { "sans", "serif", "monospace", "bold", "italic", "small-text" };
        var quality = qualities[(index - 1) % qualities.Length];
        var degraded = quality != "clean-high-resolution";
        var strata = new FixtureStrata(quality, styles[(index - 1) % styles.Length], layout, rotation == 0 ? "upright" : "rotated");
        var criticalTokens = layout == "table"
            ? new[] { reference, amount, "15.00" }
            : new[] { reference, amount, "2026-09-" + index.ToString("D2", CultureInfo.InvariantCulture) };
        return new Fixture(id, "en", layout, rotation, degraded, pageText, blocks, order, table is null ? [] : [table], criticalTokens, strata);
    }
}

internal static class ExpectedBlocks
{
    public static IReadOnlyList<TextBlock> ForFixture(Fixture fixture)
    {
        var blocks = fixture.Blocks.ToList();
        foreach (var table in fixture.Tables)
        {
            blocks.Add(new TextBlock(table.Id, FlattenRows(table.Rows), 80, 270, 1020, 195));
        }

        return blocks;
    }

    private static string FlattenRows(IReadOnlyList<IReadOnlyList<string>> rows) =>
        string.Join("\n", rows.Select(static row => string.Join("\t", row)));
}

internal static class FixtureRenderer
{
    private const int Width = 1240;
    private const int Height = 1754;
    private static readonly Typeface Typeface = new("Segoe UI");

    public static void Render(Fixture fixture, string outputPath, bool degraded)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            context.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.SlateGray, 1), new Rect(45, 45, Width - 90, Height - 90));
            foreach (var block in fixture.Blocks)
            {
                DrawWrappedText(context, block.Text, block.X, block.Y, block.Width, block.Height, block.Id == "heading" ? 32 : fixture.Layout == "columns" ? 18 : 23, fixture.Strata.WritingStyle);
            }
            foreach (var table in fixture.Tables) DrawTable(context, table, fixture.Strata.WritingStyle);
        }

        BitmapSource bitmap = RenderVisual(visual, Width, Height);
        if (degraded || fixture.Strata.Quality is "low-resolution" or "blur-low-contrast") bitmap = Degrade(bitmap);
        if (fixture.Strata.Quality == "jpeg-compression") bitmap = JpegRoundTrip(bitmap);
        if (fixture.Strata.Quality == "blur-low-contrast") bitmap = LowerContrast(bitmap);
        if (fixture.Strata.Quality == "skew-rotation") bitmap = Skew(bitmap);
        if (fixture.RotationDegrees != 0) bitmap = new TransformedBitmap(bitmap, new RotateTransform(fixture.RotationDegrees));
        SavePng(bitmap, outputPath);
    }

    public static void RenderContactSheet(IReadOnlyList<RenderedFixture> fixtures, string root, string outputPath)
    {
        const int columns = 5;
        const int cellWidth = 360;
        const int cellHeight = 300;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, columns * cellWidth, 6 * cellHeight));
            for (var index = 0; index < fixtures.Count; index++)
            {
                var fixture = fixtures[index];
                var x = (index % columns) * cellWidth;
                var y = (index / columns) * cellHeight;
                var imagePath = Path.Combine(root, fixture.RelativeImagePath);
                var decoder = new PngBitmapDecoder(new Uri(imagePath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                context.DrawImage(decoder.Frames[0], new Rect(x + 12, y + 34, cellWidth - 24, cellHeight - 58));
                context.DrawText(CreateText($"{fixture.Fixture.Id} | {fixture.Fixture.Layout} | {fixture.Fixture.RotationDegrees}° | {(fixture.Fixture.DegradedScan ? "degraded" : "clean")}", 14), new Point(x + 12, y + 12));
            }
        }
        SavePng(RenderVisual(visual, columns * cellWidth, 6 * cellHeight), outputPath);
    }

    private static void DrawTable(DrawingContext context, FixtureTable table, string writingStyle)
    {
        const double left = 80;
        const double top = 270;
        const double columnWidth = 340;
        const double rowHeight = 65;
        for (var row = 0; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Rows[row].Count; column++)
            {
                var rectangle = new Rect(left + (column * columnWidth), top + (row * rowHeight), columnWidth, rowHeight);
                context.DrawRectangle(row == 0 ? Brushes.Gainsboro : Brushes.White, new Pen(Brushes.Black, 1), rectangle);
                context.DrawText(CreateText(table.Rows[row][column], 19, writingStyle), new Point(rectangle.X + 10, rectangle.Y + 20));
            }
        }
    }

    private static void DrawWrappedText(DrawingContext context, string text, double x, double y, double width, double height, double fontSize, string writingStyle)
    {
        if (writingStyle == "small-text" && fontSize < 30) fontSize = 15;
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, GetTypeface(writingStyle), fontSize, Brushes.Black, 1d)
        {
            MaxTextWidth = width,
            MaxTextHeight = height,
            Trimming = TextTrimming.None
        };
        context.DrawText(formatted, new Point(x, y));
    }

    private static FormattedText CreateText(string text, double size, string writingStyle = "sans") =>
        new(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, GetTypeface(writingStyle), size, Brushes.Black, 1d);

    private static Typeface GetTypeface(string writingStyle) => writingStyle switch
    {
        "serif" => new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        "monospace" => new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        "bold" => new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
        "italic" => new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal),
        _ => Typeface
    };

    private static BitmapSource RenderVisual(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Degrade(BitmapSource source)
    {
        var smaller = new TransformedBitmap(source, new ScaleTransform(0.62, 0.62));
        var degraded = new TransformedBitmap(smaller, new ScaleTransform(1d / 0.62d, 1d / 0.62d));
        degraded.Freeze();
        return degraded;
    }

    private static BitmapSource JpegRoundTrip(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 38 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource LowerContrast(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        return RenderVisual(visual, source.PixelWidth, source.PixelHeight);
    }

    private static BitmapSource Skew(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.PushTransform(new SkewTransform(3.5, 0));
            context.DrawImage(source, new Rect(-55, 0, source.PixelWidth, source.PixelHeight));
            context.Pop();
        }
        return RenderVisual(visual, source.PixelWidth, source.PixelHeight);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

internal static class Metrics
{
    public static double UnicodeScalarCer(string expected, string actual)
    {
        var expectedRunes = expected.EnumerateRunes().ToArray();
        var actualRunes = actual.EnumerateRunes().ToArray();
        if (expectedRunes.Length == 0) return actualRunes.Length == 0 ? 0d : 1d;
        var previous = Enumerable.Range(0, actualRunes.Length + 1).ToArray();
        for (var row = 1; row <= expectedRunes.Length; row++)
        {
            var current = new int[actualRunes.Length + 1];
            current[0] = row;
            for (var column = 1; column <= actualRunes.Length; column++)
            {
                var substitution = previous[column - 1] + (expectedRunes[row - 1] == actualRunes[column - 1] ? 0 : 1);
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
            }
            previous = current;
        }
        return (double)previous[actualRunes.Length] / expectedRunes.Length;
    }
}

internal static class ReadingOrder
{
    public static bool Matches(
        IReadOnlyList<TextBlock> expectedBlocks,
        IReadOnlyList<string> expectedOrder,
        IReadOnlyList<TextBlock> providerBlocks)
    {
        if (expectedBlocks.Count != expectedOrder.Count || providerBlocks.Count != expectedBlocks.Count)
        {
            return false;
        }

        var remaining = expectedBlocks.ToList();
        var observedOrder = new List<string>(providerBlocks.Count);
        foreach (var providerBlock in providerBlocks)
        {
            var match = remaining
                .Where(expected => string.Equals(expected.Text, providerBlock.Text, StringComparison.Ordinal))
                .OrderBy(expected => CenterDistanceSquared(expected, providerBlock))
                .FirstOrDefault();
            if (match is null)
            {
                return false;
            }

            observedOrder.Add(match.Id);
            remaining.Remove(match);
        }

        return remaining.Count == 0 && expectedOrder.SequenceEqual(observedOrder, StringComparer.Ordinal);
    }

    private static double CenterDistanceSquared(TextBlock expected, TextBlock actual)
    {
        var horizontal = (expected.X + (expected.Width / 2d)) - (actual.X + (actual.Width / 2d));
        var vertical = (expected.Y + (expected.Height / 2d)) - (actual.Y + (actual.Height / 2d));
        return (horizontal * horizontal) + (vertical * vertical);
    }
}

internal static class Evaluation
{
    public static EvaluationReport Evaluate(BenchmarkTruth truth, CandidateResults candidate)
    {
        var provenancePresent = candidate.Provenance is { Engine.Length: > 0, Version.Length: > 0, RunId.Length: > 0 };
        var candidates = candidate.Samples.ToDictionary(static sample => sample.Id, StringComparer.Ordinal);
        var samples = truth.Samples.Select(sample => EvaluateSample(sample, candidates.GetValueOrDefault(sample.Id))).ToArray();
        var language = samples.GroupBy(static sample => sample.Language).ToDictionary(
            static group => group.Key,
            static group => new LanguageSummary(group.Count(), group.Average(static sample => sample.Cer), group.Count(static sample => sample.ExactText)));
        var allChecks = samples.All(static sample => sample.ExactText && sample.BlockOrderPass && sample.TablePass && sample.CriticalValuesPass);
        var strataById = truth.Samples.ToDictionary(static sample => sample.Id, StringComparer.Ordinal);
        var strata = Summarise(samples, sample => strataById[sample.Id].Strata.Label);
        var qualityStrata = Summarise(samples, sample => strataById[sample.Id].Strata.Quality);
        var writingStyleStrata = Summarise(samples, sample => strataById[sample.Id].Strata.WritingStyle);
        var layoutStrata = Summarise(samples, sample => strataById[sample.Id].Strata.Layout);
        var orientationStrata = Summarise(samples, sample => strataById[sample.Id].Strata.Orientation);
        return new EvaluationReport(
            truth.Classification,
            candidate.CandidateName,
            provenancePresent,
            provenancePresent && allChecks,
            samples,
            language,
            strata,
            qualityStrata,
            writingStyleStrata,
            layoutStrata,
            orientationStrata);
    }

    private static SampleEvaluation EvaluateSample(TruthSample truth, CandidateSample? candidate)
    {
        var actualText = candidate?.PageText ?? string.Empty;
        var order = candidate?.BlockOrder ?? [];
        var actualBlocks = candidate?.Blocks ?? [];
        var actualTables = candidate?.Tables ?? [];
        var providerOrderMatchesBlocks = actualBlocks.Select(static block => block.Id).SequenceEqual(order, StringComparer.Ordinal);
        var blockOrderPass = false;
        var tablePass = false;
        if (truth.Blocks is { } expectedBlocks && truth.BlockOrder is { } expectedOrder && truth.Tables is { } expectedTables)
        {
            tablePass = expectedTables.Count == actualTables.Count &&
                expectedTables.Zip(actualTables).All(static pair => RowsEqual(pair.First.Rows, pair.Second.Rows));
            blockOrderPass = providerOrderMatchesBlocks && ReadingOrder.Matches(expectedBlocks, expectedOrder, actualBlocks);
        }
        return new SampleEvaluation(
            truth.Id,
            truth.Language,
            candidate is not null,
            Metrics.UnicodeScalarCer(truth.PageText, actualText),
            string.Equals(truth.PageText, actualText, StringComparison.Ordinal),
            blockOrderPass,
            tablePass,
            truth.CriticalTokens is { Count: > 0 } tokens && tokens.All(token => actualText.Contains(token, StringComparison.Ordinal)));
    }

    private static bool RowsEqual(IReadOnlyList<IReadOnlyList<string>> expected, IReadOnlyList<IReadOnlyList<string>> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(static pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, StratumSummary> Summarise(
        IEnumerable<SampleEvaluation> samples,
        Func<SampleEvaluation, string> key) => samples.GroupBy(key).ToDictionary(
            static group => group.Key,
            static group => new StratumSummary(group.Count(), group.Average(static sample => sample.Cer), group.Count(static sample => sample.ExactText)));
}

internal sealed record Fixture(string Id, string Language, string Layout, int RotationDegrees, bool DegradedScan, string PageText, IReadOnlyList<TextBlock> Blocks, IReadOnlyList<string> BlockOrder, IReadOnlyList<FixtureTable> Tables, IReadOnlyList<string> CriticalTokens, FixtureStrata Strata);
internal sealed record FixtureStrata(string Quality, string WritingStyle, string Layout, string Orientation)
{
    public string Label => $"quality={Quality};style={WritingStyle};layout={Layout};orientation={Orientation}";
}
internal sealed record TextBlock(string Id, string Text, double X, double Y, double Width, double Height);
internal sealed record FixtureTable(string Id, IReadOnlyList<IReadOnlyList<string>> Rows);
internal sealed record RenderedFixture(Fixture Fixture, string RelativeImagePath)
{
    public TruthSample ToTruth()
    {
        var blocks = ExpectedBlocks.ForFixture(Fixture);
        return new TruthSample(Fixture.Id, Fixture.Language, RelativeImagePath, Fixture.Layout, Fixture.RotationDegrees, Fixture.DegradedScan, Fixture.PageText, blocks, Fixture.BlockOrder, Fixture.Tables.Select(static table => new TruthTable(table.Id, table.Rows)).ToArray(), Fixture.CriticalTokens, Fixture.Strata);
    }
}

internal sealed record BenchmarkTruth(string Classification, string Scope, IReadOnlyList<TruthSample> Samples);
internal sealed record TruthSample(string Id, string Language, string ImagePath, string Layout, int RotationDegrees, bool DegradedScan, string PageText, IReadOnlyList<TextBlock> Blocks, IReadOnlyList<string> BlockOrder, IReadOnlyList<TruthTable> Tables, IReadOnlyList<string> CriticalTokens, FixtureStrata Strata);
internal sealed record TruthTable(string Id, IReadOnlyList<IReadOnlyList<string>> Rows);
internal sealed record CandidateResults(string CandidateName, CandidateProvenance? Provenance, IReadOnlyList<CandidateSample> Samples);
internal sealed record CandidateProvenance(string Engine, string Version, string RunId);
internal sealed record CandidateSample(string Id, string PageText, IReadOnlyList<string>? BlockOrder, IReadOnlyList<CandidateTable>? Tables, IReadOnlyList<TextBlock>? Blocks);
internal sealed record CandidateTable(string Id, IReadOnlyList<IReadOnlyList<string>> Rows);
internal sealed record SampleEvaluation(string Id, string Language, bool CandidateSamplePresent, double Cer, bool ExactText, bool BlockOrderPass, bool TablePass, bool CriticalValuesPass);
internal sealed record LanguageSummary(int Samples, double MeanCer, int ExactTextSamples);
internal sealed record StratumSummary(int Samples, double MeanCer, int ExactTextSamples);
internal sealed record EvaluationReport(string Classification, string CandidateName, bool ProvenancePresent, bool OverallPass, IReadOnlyList<SampleEvaluation> Samples, IReadOnlyDictionary<string, LanguageSummary> Languages, IReadOnlyDictionary<string, StratumSummary> Strata, IReadOnlyDictionary<string, StratumSummary> QualityStrata, IReadOnlyDictionary<string, StratumSummary> WritingStyleStrata, IReadOnlyDictionary<string, StratumSummary> LayoutStrata, IReadOnlyDictionary<string, StratumSummary> OrientationStrata);
