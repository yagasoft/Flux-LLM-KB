using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceRootConfigurationTests
{
    [Fact]
    public void Source_text_policy_cannot_enable_code_on_an_unrestricted_filesystem_root()
    {
        Assert.Throws<DomainInvariantException>(() => SourceRootConfiguration.Create(
            @"C:\Corpus", "Corpus", true, false, 16 * 1024 * 1024,
            allowedClassifications: ["text/plain", "text/x-source-code"]));
    }

    [Fact]
    public void Git_mode_preserves_policy_on_restore_and_unknown_modes_fail_closed()
    {
        var root = SourceRootConfiguration.Create(@"C:\Corpus", "Corpus", true, false, 1024,
            allowedClassifications: ["text/plain", "text/x-source-code"], discoveryMode: SourceDiscoveryMode.GitTracked);
        var restored = SourceRootConfiguration.Restore(root.Id, root.CanonicalPath, root.DisplayName,
            root.Recursive, root.FollowLinks, root.MaximumFileBytes, [], [], root.AllowedClassifications,
            root.ReconciliationCadence, root.State, 1, discoveryMode: root.DiscoveryMode);
        Assert.Equal(SourceDiscoveryMode.GitTracked, restored.DiscoveryMode);
        Assert.True(restored.IndexSourceText);
        Assert.Throws<DomainInvariantException>(() => SourceRootConfiguration.Create(@"C:\Corpus", "Corpus", true,
            false, 1024, discoveryMode: (SourceDiscoveryMode)42));
    }

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
