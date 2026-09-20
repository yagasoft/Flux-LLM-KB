using System.Text;

namespace OcrQualityAssessment;

internal static class HarnessSelfTests
{
    public static void Run()
    {
        AssertEqual(0d, Metrics.UnicodeScalarCer("screening", "screening"), "identical English text");
        AssertEqual(1d / 3d, Metrics.UnicodeScalarCer("A😀B", "A😎B"), "Unicode scalar CER counts emoji once");
        AssertEqual(1d, Metrics.UnicodeScalarCer(string.Empty, "X"), "empty reference CER");
        AssertEqual(0d, Metrics.UnicodeScalarCer(string.Empty, string.Empty), "two empty strings CER");

        var fixtures = FixtureCatalog.Create();
        if (fixtures.Count != 30 || fixtures.Any(static fixture => fixture.Language != "en"))
        {
            throw new InvalidOperationException("Self-test failed: the screening corpus must contain exactly 30 English pages.");
        }

        if (!fixtures.Any(static fixture => fixture.Layout == "columns") ||
            !fixtures.Any(static fixture => fixture.Layout == "table") ||
            !fixtures.Any(static fixture => fixture.RotationDegrees != 0) ||
            !fixtures.Any(static fixture => fixture.DegradedScan))
        {
            throw new InvalidOperationException("Self-test failed: expected layout and degradation coverage is absent.");
        }

        if (fixtures.Select(static fixture => fixture.Strata.Quality).Distinct(StringComparer.Ordinal).Count() < 4 ||
            fixtures.Select(static fixture => fixture.Strata.WritingStyle).Distinct(StringComparer.Ordinal).Count() < 5)
        {
            throw new InvalidOperationException("Self-test failed: quality and writing-style strata are incomplete.");
        }

        var twoColumns = fixtures.First(static fixture => fixture.Layout == "columns");
        var providerOwnedBlocks = twoColumns.Blocks.Select((block, index) => new TextBlock(
            $"provider-{index}",
            block.Text,
            block.X,
            block.Y,
            block.Width,
            block.Height)).ToArray();
        if (!ReadingOrder.Matches(twoColumns.Blocks, twoColumns.BlockOrder, providerOwnedBlocks))
        {
            throw new InvalidOperationException("Self-test failed: matching block geometry and text must not depend on provider-owned IDs.");
        }

        var providerWrongOrder = new[] { providerOwnedBlocks[0], providerOwnedBlocks[1], providerOwnedBlocks[3], providerOwnedBlocks[2], providerOwnedBlocks[4] };
        if (ReadingOrder.Matches(twoColumns.Blocks, twoColumns.BlockOrder, providerWrongOrder))
        {
            throw new InvalidOperationException("Self-test failed: a row-interleaved provider block sequence must fail the column reading-order check.");
        }

        var expectedBlocks = fixtures.ToDictionary(static fixture => fixture.Id, ExpectedBlocks.ForFixture, StringComparer.Ordinal);
        var truth = new BenchmarkTruth(
            "synthetic-screening-not-release-gate",
            "self-test",
            fixtures.Select(fixture => new TruthSample(
                fixture.Id,
                fixture.Language,
                $"pages/{fixture.Id}.png",
                fixture.Layout,
                fixture.RotationDegrees,
                fixture.DegradedScan,
                fixture.PageText,
                expectedBlocks[fixture.Id],
                fixture.BlockOrder,
                fixture.Tables.Select(static table => new TruthTable(table.Id, table.Rows)).ToArray(),
                fixture.CriticalTokens,
                fixture.Strata)).ToArray());
        if (truth.Samples.Any(static sample => sample.CriticalTokens.Any(token => !sample.PageText.Contains(token, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Self-test failed: a critical token is not represented in its declared page text.");
        }

        var perfect = new CandidateResults(
            "self-test-perfect-candidate",
            new CandidateProvenance("self-test", "1", "self-test-run"),
            truth.Samples.Select(static sample => new CandidateSample(
                sample.Id,
                sample.PageText,
                sample.Blocks.Select((_, index) => $"provider-block-{index}").ToArray(),
                sample.Tables.Select(static table => new CandidateTable(table.Id, table.Rows)).ToArray(),
                sample.Blocks.Select((block, index) => new TextBlock(
                    $"provider-block-{index}",
                    block.Text,
                    block.X,
                    block.Y,
                    block.Width,
                    block.Height)).ToArray())).ToArray());
        var report = Evaluation.Evaluate(truth, perfect);
        if (!report.OverallPass || report.Samples.Any(static sample => sample.Cer != 0d || !sample.ExactText || !sample.BlockOrderPass || !sample.TablePass || !sample.CriticalValuesPass))
        {
            throw new InvalidOperationException("Self-test failed: a perfect candidate did not score perfectly.");
        }

        var providerOwnedTableIds = new CandidateResults(
            "self-test-provider-table-ids",
            new CandidateProvenance("self-test", "1", "self-test-run"),
            truth.Samples.Select(static sample => new CandidateSample(
                sample.Id,
                sample.PageText,
                sample.Blocks.Select((_, index) => $"provider-block-{index}").ToArray(),
                sample.Tables.Select((table, index) => new CandidateTable($"provider-table-{index}", table.Rows)).ToArray(),
                sample.Blocks.Select((block, index) => new TextBlock(
                    $"provider-block-{index}",
                    block.Text,
                    block.X,
                    block.Y,
                    block.Width,
                    block.Height)).ToArray())).ToArray());
        var providerOwnedTableReport = Evaluation.Evaluate(truth, providerOwnedTableIds);
        if (providerOwnedTableReport.Samples.Where((_, index) => truth.Samples[index].Tables.Count > 0).Any(static sample => !sample.TablePass))
        {
            throw new InvalidOperationException("Self-test failed: exact table rows must not depend on provider-owned table IDs.");
        }

        var textOnlyDiagnosticTruth = new BenchmarkTruth(
            "public-scan-diagnostic-not-release-gate",
            "self-test text-only diagnostic",
            [new TruthSample(
                "diagnostic-page",
                "en",
                "diagnostic-page.png",
                "columns",
                0,
                false,
                "Known diagnostic text.",
                null!,
                [],
                [],
                [],
                new FixtureStrata("clean", "serif", "columns", "upright"))]);
        var textOnlyCandidate = new CandidateResults(
            "self-test-text-only-diagnostic",
            new CandidateProvenance("self-test", "1", "diagnostic-run"),
            [new CandidateSample("diagnostic-page", "Known diagnostic text.", [], [], [])]);
        var textOnlyReport = Evaluation.Evaluate(textOnlyDiagnosticTruth, textOnlyCandidate);
        var textOnlySample = textOnlyReport.Samples.Single();
        if (textOnlyReport.Classification != "public-scan-diagnostic-not-release-gate" ||
            textOnlyReport.OverallPass ||
            !textOnlySample.ExactText ||
            textOnlySample.BlockOrderPass ||
            textOnlySample.TablePass ||
            textOnlySample.CriticalValuesPass)
        {
            throw new InvalidOperationException("Self-test failed: text-only diagnostic truth must retain its classification and cannot pass unprovided structural checks.");
        }
    }

    private static void AssertEqual(double expected, double actual, string name)
    {
        if (Math.Abs(expected - actual) > 0.000001d)
        {
            throw new InvalidOperationException($"Self-test failed for {name}: expected {expected}, actual {actual}.");
        }
    }
}
