using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceRootConfigurationTests
{
    [Fact]
    public void Source_text_policy_allows_inert_code_text_without_changing_filesystem_discovery()
    {
        var root = SourceRootConfiguration.Create(
            @"C:\Corpus", "Corpus", true, false, 16 * 1024 * 1024,
            allowedClassifications: ["text/plain", "text/x-source-code"]);
        Assert.True(root.IndexSourceText);
        Assert.Equal(SourceDiscoveryMode.Filesystem, root.DiscoveryMode);
        Assert.False(root.FollowLinks);
        Assert.False(FluxKnowledge.Application.Sources.RepositorySourceTextPolicy.IsEnabled(root, "query.sql"));
    }

    [Fact]
    public void New_root_uses_the_workspace_text_default_but_explicit_restrictions_are_preserved()
    {
        var root = SourceRootConfiguration.Create(@"C:\Corpus", "Corpus", true, false, 32 * 1024 * 1024);
        Assert.True(root.IndexSourceText);
        var restricted = SourceRootConfiguration.Create(@"C:\Corpus", "Corpus", true, false, 1024,
            allowedClassifications: ["text/plain"]);
        Assert.False(restricted.IndexSourceText);
        Assert.Equal(1024, restricted.MaximumFileBytes);
    }

    [Fact]
    public void Native_creation_uses_the_same_default_and_preserves_explicit_code_opt_out()
    {
        using var defaults = System.Text.Json.JsonDocument.Parse("{\"path\":\"C:\\\\Corpus\",\"displayName\":\"Corpus\"}");
        var request = FluxKnowledge.Application.IntegrationV1.Corpus.NativeSourceRootCreation.Parse(defaults.RootElement, "test");
        Assert.Equal(32 * 1024 * 1024, request.MaximumFileBytes);
        Assert.Contains("text/x-source-code", request.AllowedClassifications);
        Assert.Equal(SourceDiscoveryMode.Filesystem, request.DiscoveryMode);
        Assert.False(request.FollowLinks);
        using var restricted = System.Text.Json.JsonDocument.Parse("{\"path\":\"C:\\\\Corpus\",\"displayName\":\"Corpus\",\"indexSourceText\":false,\"maximumFileBytes\":1024}");
        var explicitRequest = FluxKnowledge.Application.IntegrationV1.Corpus.NativeSourceRootCreation.Parse(restricted.RootElement, "test");
        Assert.DoesNotContain("text/x-source-code", explicitRequest.AllowedClassifications);
        Assert.Equal(1024, explicitRequest.MaximumFileBytes);
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
