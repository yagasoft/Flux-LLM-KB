using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceRootConfigurationTests
{
    [Fact]
    public void Root_can_only_move_between_enabled_and_paused_once_per_transition()
    {
        var root = SourceRootConfiguration.Create("C:\\Corpus", "Corpus", recursive: true, followLinks: false, 16 * 1024 * 1024);

        var paused = root.Pause("operator request");

        Assert.Equal(SourceRootState.Paused, paused.State);
        Assert.Throws<DomainInvariantException>(() => paused.Pause("operator request"));
        Assert.Throws<DomainInvariantException>(() => root.Resume("operator request"));
        Assert.Equal(SourceRootState.Enabled, paused.Resume("operator request").State);
    }

    [Theory]
    [InlineData("Corpus")]
    [InlineData("C:\\Corpus\\..\\Corpus")]
    public void Root_rejects_paths_that_are_not_canonical_absolute_paths(string path)
    {
        Assert.Throws<DomainInvariantException>(
            () => SourceRootConfiguration.Create(path, "Corpus", recursive: true, followLinks: false, 16 * 1024 * 1024));
    }

    [Fact]
    public void Root_retains_an_immutable_effective_scan_policy()
    {
        var root = SourceRootConfiguration.Create(
            "C:\\Corpus",
            "Corpus",
            recursive: true,
            followLinks: false,
            maximumFileBytes: 16 * 1024 * 1024,
            includePatterns: ["**/*.md"],
            excludePatterns: ["**/.git/**"],
            allowedClassifications: ["utf8-text"],
            reconciliationCadence: TimeSpan.FromMinutes(15));

        Assert.Equal(new[] { "**/*.md" }, root.IncludePatterns);
        Assert.Equal(new[] { "**/.git/**" }, root.ExcludePatterns);
        Assert.Equal(new[] { "utf8-text" }, root.AllowedClassifications);
        Assert.Equal(TimeSpan.FromMinutes(15), root.ReconciliationCadence);
    }
}
