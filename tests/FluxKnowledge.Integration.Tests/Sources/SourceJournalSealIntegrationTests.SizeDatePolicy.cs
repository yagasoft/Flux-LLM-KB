using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Size_date_policy_rebinds_same_stamp_replacements_and_preserves_later_journal_changes(bool gitTracked)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, gitTracked: gitTracked);
        await scenario.ScanAsync("replacement-initial");
        await scenario.DrainAsync();
        string priorFileId;
        Guid revision;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var entry = await db.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory);
            priorFileId = entry.FileId;
            revision = entry.SourceRevisionId!.Value;
            entry.LatestJournalUsn = long.MaxValue - 1;
            await db.SaveChangesAsync();
        }
        var stamp = File.GetLastWriteTimeUtc(scenario.Path);
        var retired = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(scenario.Path))!, "retired-original.txt");
        File.Move(scenario.Path, retired);
        await File.WriteAllTextAsync(scenario.Path, new string('z', 64));
        File.SetLastWriteTimeUtc(scenario.Path, stamp);
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using (var rebound = scenario.Factory.CreateDbContext())
        {
            var entry = await rebound.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory);
            Assert.NotEqual(priorFileId, entry.FileId);
            Assert.Equal(revision, entry.SourceRevisionId);
            Assert.NotEqual(long.MaxValue - 1, entry.LatestJournalUsn);
            Assert.False(entry.IsSealed);
            var observation = System.Text.Json.JsonDocument.Parse(entry.ObservationJson!);
            Assert.True(observation.RootElement.GetProperty("MetadataOnly").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, observation.RootElement.GetProperty("CaptureGeneration").ValueKind);
        }
        File.Delete(retired); // A later deletion of the previous physical ID must not remove the replacement.
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        await using (var current = scenario.Factory.CreateDbContext())
            Assert.Null((await current.SourceRevisions.SingleAsync(x => x.Id == revision)).SuppressedAtUtc);
        scenario.Overwrite(0, (byte)'q');
        await scenario.IntakeAsync(); // Exercise the new physical ID through the real journal, without a watcher hint.
        await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
        Assert.Equal(0, scenario.ArtifactSourceBytes + scenario.RetainedBytes);
    }

    [NativeSqlServerFact]
    public async Task Size_date_policy_does_not_rehash_the_source_after_a_retained_metadata_change()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.ScanAsync("retained-metadata-initial");
        await scenario.DrainAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var artifact = await db.SourceArtifacts.SingleAsync();
            var retained = System.IO.Path.Combine(scenario.RetainedRoot, artifact.StoreRelativePath);
            File.SetLastWriteTimeUtc(retained, File.GetLastWriteTimeUtc(retained).AddSeconds(1));
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
    }

    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Size_date_policy_unchanged_watcher_events_do_not_read_content(bool gitTracked, bool missingInventory)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, gitTracked: gitTracked);
        await scenario.ScanAsync("size-date-initial");
        await scenario.DrainAsync();
        Guid revision;
        DateTimeOffset checksumAt;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            revision = (await db.SourceRevisions.SingleAsync()).Id;
            checksumAt = (await db.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc;
            if (missingInventory)
            {
                db.SourceInventoryEntries.Remove(await db.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory));
                await db.SaveChangesAsync();
            }
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(revision, (await after.SourceRevisions.SingleAsync()).Id);
        Assert.Equal(checksumAt, (await after.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc);
        var pending = await after.SourcePendingChanges.Where(x => x.RelativePath == "one.txt").ToArrayAsync();
        Assert.All(pending, item => Assert.Equal(SourcePendingReasons.RemovalPending, item.ReasonFlags));
    }

    [NativeSqlServerFact]
    public async Task Size_date_policy_initial_capture_hashes_source_once_without_retained_rechecks()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.ScanAsync("size-date-first-ingestion");
        Assert.Equal(64, scenario.SourceBytes);
        Assert.Equal(0, scenario.ArtifactSourceBytes);
        Assert.Equal(0, scenario.RetainedBytes);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(64, (await db.SourceArtifacts.SingleAsync()).ByteLength);
        Assert.Equal(64, (await db.SourceArtifacts.SingleAsync()).ContentSha256.Length);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Size_date_policy_rehashes_only_after_size_or_date_changes(bool changeSize)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.ScanAsync("size-date-before-change");
        await scenario.DrainAsync();
        var stamp = File.GetLastWriteTimeUtc(scenario.Path);
        if (changeSize)
        {
            await File.AppendAllTextAsync(scenario.Path, "y");
            File.SetLastWriteTimeUtc(scenario.Path, stamp);
        }
        else
        {
            scenario.Overwrite(0, (byte)'y');
            File.SetLastWriteTimeUtc(scenario.Path, stamp.AddSeconds(2));
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(changeSize ? 65 : 64, scenario.SourceBytes);
        Assert.Equal(0, scenario.ArtifactSourceBytes);
        Assert.Equal(0, scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(2, await after.SourceRevisions.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Size_date_policy_accepts_same_size_edits_with_preserved_date_even_after_a_watcher_event()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.ScanAsync("size-date-preserved-initial");
        await scenario.DrainAsync();
        scenario.Overwrite(0, (byte)'y', preserveDate: true);
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Single(await after.SourceRevisions.ToArrayAsync());
    }
}
