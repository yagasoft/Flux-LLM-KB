using FluxKnowledge.Web.Configuration;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingHostArgumentsTests
{
    [Theory]
    [InlineData(null, "Run")]
    [InlineData("--migrate", "Migrate")]
    [InlineData("--candidate-quality", "CandidateQuality")]
    [InlineData("--expanded-gpu", "ExpandedGpu")]
    [InlineData("--expanded-gpu-qualification", "ExpandedGpuQualification")]
    [InlineData("--expanded-gpu-indexing", "ExpandedGpuIndexing")]
    [InlineData("--payload-readiness", "PayloadReadiness")]
    [InlineData("--journal-iis", "JournalIis")]
    public void Optional_modes_are_explicit_and_quality_never_selects_migration(string? flag, string expected)
    {
        string[] args = flag is null ? ["--settings-file", "owned"] : ["--settings-file", "owned", flag];
        Assert.Equal(expected, StagingHostArguments.ParseMode(args).ToString());
    }

    [Fact]
    public void Cli_preserves_its_command_arguments() =>
        Assert.Equal(StagingHostMode.Cli, StagingHostArguments.ParseMode(["--settings-file", "owned", "--cli", "corpus", "search"]));

    [Fact]
    public void Cleanup_requires_both_fixture_and_exact_prior_window_arguments()
    {
        Assert.Equal(StagingHostMode.IndexFixtureCleanup,StagingHostArguments.ParseMode(["--settings-file","owned","--index-fixture-cleanup","--fixture-output","fixture","--prior-window","window"]));
        Assert.Throws<ArgumentException>(()=>StagingHostArguments.ParseMode(["--settings-file","owned","--index-fixture-cleanup"]));
    }

    [Fact]
    public void Legacy_cleanup_requires_an_explicit_plan_and_keeps_the_model_free_cleanup_mode()
    {
        string[] arguments=["--settings-file","owned","--index-fixture-cleanup","--fixture-output","fixture","--prior-window","window","--legacy-plan","plan"];
        Assert.Equal(StagingHostMode.IndexFixtureCleanup,StagingHostArguments.ParseMode(arguments));
        arguments[7]="--migrate";
        Assert.Throws<ArgumentException>(()=>StagingHostArguments.ParseMode(arguments));
    }

    [Theory]
    [InlineData("--candidate-quality", "--migrate")]
    [InlineData("--migrate", "--candidate-quality")]
    [InlineData("--candidate-quality", "--cli")]
    [InlineData("--unknown", null)]
    [InlineData("--cli", null)]
    public void Ambiguous_or_unknown_modes_fail_before_loading_settings(string first, string? second)
    {
        string[] args = second is null ? ["--settings-file", "owned", first] : ["--settings-file", "owned", first, second];
        Assert.Throws<ArgumentException>(() => StagingHostArguments.ParseMode(args));
    }

    [Theory]
    [InlineData("5137")]
    [InlineData("56137")]
    [InlineData("0")]
    [InlineData(null)]
    public void Journal_IIS_requires_a_separate_backend_port_supplied_by_the_host(string? port)
    {
        Assert.Throws<InvalidOperationException>(() => StagingHostArguments.RequireJournalIisPort("host-token", port, "/", 56137));
        Assert.Throws<InvalidOperationException>(() => StagingHostArguments.RequireJournalIisPort(null, "23000", "/", 56137));
        Assert.Throws<InvalidOperationException>(() => StagingHostArguments.RequireJournalIisPort("host-token", "23000", "/nested", 56137));
        Assert.Equal(23000, StagingHostArguments.RequireJournalIisPort("host-token", "23000", "/", 56137));
    }
}
