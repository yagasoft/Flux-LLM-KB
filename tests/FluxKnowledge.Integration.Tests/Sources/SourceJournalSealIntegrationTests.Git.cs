using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualified_Git_capture_reuses_quiet_content_and_preserves_untouched_siblings(bool watcherFirst)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await File.WriteAllTextAsync(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Path)!, "sibling.txt"), new string('s', 64));
        scenario.Git("add", ".");
        using var persistent = new FileStream(scenario.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await scenario.ScanAsync("git-baseline");
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            scenario.Overwrite(cycle, (byte)(66 + cycle));
            if (!watcherFirst) await scenario.IntakeAsync();
            await scenario.SignalAsync();
            scenario.Restart();
            scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
            scenario.CapturedPaths.Clear();
            await scenario.DrainAsync();
            Assert.NotEmpty(scenario.CapturedPaths);
            Assert.All(scenario.CapturedPaths, path => Assert.Equal("one.txt", path));
            await using var db = scenario.Factory.CreateDbContext();
            var latest = await db.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenario.Path))), latest.ContentSha256);
            Assert.True(SqlRepositoryWorkRecovery.ReadDiscovery(latest.CurrentDiscoveryEvidenceJson)?.Authoritative);
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes);
    }

    [NativeSqlServerFact]
    public async Task A_Git_seal_before_failed_completion_cannot_strand_an_unauthorised_revision_on_a_higher_claim()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await scenario.ScanAsync("git-baseline");
        await scenario.DrainAsync();
        scenario.Overwrite(0, 66);
        await scenario.IntakeAsync(); await scenario.IntakeAsync();
        var failed = await scenario.ClaimAsync("git-before-completion");
        scenario.FailNextCompletion = true;
        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => scenario.RunAsync(failed));
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var latest = await db.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
            Assert.Null(SqlRepositoryWorkRecovery.ReadDiscovery(latest.CurrentDiscoveryEvidenceJson));
            Assert.True((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).IsSealed);
        }
        scenario.Clock.Now += TimeSpan.FromMinutes(11);
        scenario.Restart();
        var retry = await scenario.ClaimAsync("git-completion-retry");
        Assert.Equal(failed.ScanRequest.Id, retry.ScanRequest.Id);
        Assert.True(retry.ScanRequest.Lease!.Generation > failed.ScanRequest.Lease!.Generation);
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        await scenario.RunAsync(retry);
        Assert.True(scenario.SourceBytes >= 64, "Unauthorised sealed content must pass through capture/Observe again.");
        await using var verified = scenario.Factory.CreateDbContext();
        var current = await verified.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
        var authority = Assert.IsType<SqlRepositoryWorkRecovery.Discovery>(SqlRepositoryWorkRecovery.ReadDiscovery(current.CurrentDiscoveryEvidenceJson));
        Assert.True(authority.Authoritative);
        Assert.Equal(retry.ScanRequest.Lease.Generation, authority.LeaseGeneration);
    }
}
