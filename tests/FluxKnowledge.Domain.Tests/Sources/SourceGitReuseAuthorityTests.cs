using System.Text.Json;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceGitReuseAuthorityTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("evidence-repository")]
    [InlineData("observed-repository")]
    [InlineData("root-repository")]
    [InlineData("generation")]
    [InlineData("malformed-generation")]
    [InlineData("incomplete")]
    public void Git_completion_returns_only_the_same_validated_membership_snapshot(string change)
    {
        var repository = new string('b', 64);
        var generation = change == "malformed-generation" ? "unknown" : new string('c', 64);
        var root = SourceRootConfiguration.Create(@"I:\SyntheticGitFixture", "git", true, false, 1024,
            discoveryMode: SourceDiscoveryMode.GitTracked, repositoryIdentityFingerprint: change == "root-repository" ? null : repository);
        GitInventoryEvidence? evidence = change == "missing" ? null : new(
            change == "evidence-repository" ? new string('d', 64) : repository, generation, 1, 0);
        var current = new GitTrackedSourceDiscovery.Inventory(new("git", "common",
            change == "observed-repository" ? new string('e', 64) : repository),
            change == "generation" ? new string('f', 64) : generation, ["one.txt"], 1, 0) { IsComplete = change != "incomplete" };
        if (change == "valid")
            Assert.Same(current, SqlSourceIncrementalCoordinator.RequireGitCompletionInventory(root, evidence, current));
        else
            Assert.Throws<SourceSnapshotChangedException>(() => SqlSourceIncrementalCoordinator.RequireGitCompletionInventory(root, evidence, current));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("malformed", false)]
    [InlineData("observed-only", false)]
    [InlineData("unauthorised", false)]
    [InlineData("version", false)]
    [InlineData("source", false)]
    [InlineData("artifact", false)]
    [InlineData("hash", false)]
    [InlineData("bytes", false)]
    [InlineData("configuration", false)]
    [InlineData("policy", false)]
    [InlineData("repository", false)]
    [InlineData("request", false)]
    [InlineData("lease", false)]
    [InlineData("inventory", false)]
    [InlineData("suppressed", false)]
    public void Reuse_requires_an_existing_current_authoritative_Git_binding(string change, bool expected)
    {
        var repository = new string('b', 64);
        var root = new SourceRootConfigurationEntity { Id = Guid.NewGuid(), State = (int)SourceRootState.Enabled,
            CanonicalPath = @"I:\SyntheticGitFixture", CrawlMode = (int)SourceDiscoveryMode.GitTracked, ConfigurationRevision = 4,
            HealthEvidenceJson = JsonSerializer.Serialize(new { gitRepositoryIdentityFingerprint = repository }) };
        var source = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = root.Id, ContentSha256 = new string('a', 64), ByteLength = 64 };
        var artifact = new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = source.Id, ContentSha256 = source.ContentSha256, ByteLength = source.ByteLength };
        var proof = new SqlRepositoryWorkRecovery.Discovery(1, true, source.Id, artifact.Id, source.ContentSha256,
            source.ByteLength, root.ConfigurationRevision, SqlRepositoryWorkRecovery.Policy(root), repository,
            Guid.NewGuid(), 2, new string('c', 64), DateTimeOffset.UtcNow, true);
        proof = change switch
        {
            "unauthorised" => proof with { Authoritative = false }, "version" => proof with { Version = 9 },
            "source" => proof with { SourceId = Guid.NewGuid() }, "artifact" => proof with { ArtifactId = Guid.NewGuid() },
            "hash" => proof with { Hash = new string('d', 64) }, "bytes" => proof with { Bytes = 65 },
            "configuration" => proof with { ConfigurationRevision = 3 }, "policy" => proof with { Policy = "stale" },
            "repository" => proof with { Repository = new string('e', 64) }, "request" => proof with { ScanRequestId = Guid.Empty },
            "lease" => proof with { LeaseGeneration = 0 }, "inventory" => proof with { Inventory = "unknown" }, _ => proof
        };
        source.CurrentDiscoveryEvidenceJson = change switch
        {
            "missing" => null, "malformed" => "{", "observed-only" => JsonSerializer.Serialize(new { Version = 1, Current = (object?)null, Observed = proof }),
            _ => JsonSerializer.Serialize(new { Version = 1, Current = proof, Observed = (object?)null })
        };
        if (change == "suppressed") source.SuppressedAtUtc = DateTimeOffset.UtcNow;
        Assert.Equal(expected, SqlRepositoryWorkRecovery.CanReuseCurrentDiscovery(root, source, artifact));
    }
}
