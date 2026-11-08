using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalRecoveryIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_checkpoint_retry_does_not_query_or_replace_an_existing_due_or_owned_checkpoint(bool leased)
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        var volume = RegistrationVolume();
        string saved;
        await using (var db = factory.CreateDbContext())
        {
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000002", NextUsn = 500,
                DueAtUtc = leased ? null : clock.Now.AddMinutes(5), LeaseOwner = leased ? "other-owner" : null,
                LeaseExpiresAtUtc = leased ? clock.Now.AddMinutes(5) : null });
            await db.SaveChangesAsync();
            saved = JsonSerializer.Serialize(await db.SourceVolumeJournalCheckpoints.SingleAsync());
        }
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        var reader = new RegistrationReader();
        var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
            new() { Enabled = true, ApprovedVolumes = [volume] });
        Assert.Equal(0, await coordinator.PumpAsync(default));
        Assert.Equal(0, reader.QueryCalls);
        Assert.Equal(0, reader.ReadCalls);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(saved, JsonSerializer.Serialize(await verify.SourceVolumeJournalCheckpoints.SingleAsync()));
        Assert.False(await verify.AuditEvents.AnyAsync(x => x.EventType == "source.journal.first_registration"));
    }

    [NativeSqlServerFact]
    public async Task Missing_checkpoint_retry_preserves_a_concurrent_registration_and_all_root_evidence()
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        var volume = RegistrationVolume();
        var rootId = await SeedRegistrationRootAsync(factory, clock.Now, volume);
        string savedState, savedPending;
        await using (var db = factory.CreateDbContext())
        {
            savedState = JsonSerializer.Serialize(await db.SourceRootScanStates.SingleAsync());
            savedPending = JsonSerializer.Serialize(await db.SourcePendingChanges.SingleAsync());
        }
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        var reader = new RegistrationReader { BeforeQuery = async (_, ct) =>
        {
            await using var concurrent = factory.CreateDbContext();
            concurrent.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000002", NextUsn = 500,
                LeaseOwner = "concurrent-enrol", LeaseGeneration = 7, LeaseExpiresAtUtc = clock.Now.AddMinutes(1) });
            await concurrent.SaveChangesAsync(ct);
        } };
        Assert.Equal(0, await new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
            new() { Enabled = true, ApprovedVolumes = [volume] }).PumpAsync(default));
        await using var verify = factory.CreateDbContext();
        var checkpoint = await verify.SourceVolumeJournalCheckpoints.SingleAsync();
        Assert.Equal("0000000000000002", checkpoint.JournalId);
        Assert.Equal(500, checkpoint.NextUsn);
        Assert.Equal("concurrent-enrol", checkpoint.LeaseOwner);
        Assert.Equal(7, checkpoint.LeaseGeneration);
        Assert.Equal(savedState, JsonSerializer.Serialize(await verify.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId)));
        Assert.Equal(savedPending, JsonSerializer.Serialize(await verify.SourcePendingChanges.SingleAsync()));
        Assert.False(await verify.SourceScanRequests.AnyAsync());
        Assert.False(await verify.AuditEvents.AnyAsync(x => x.EventType == "source.journal.first_registration"));
        Assert.Equal(1, reader.QueryCalls);
        Assert.Equal(0, reader.ReadCalls);
    }

    [NativeSqlServerTheory]
    [InlineData("paused")]
    [InlineData("foreign-volume")]
    [InlineData("cancellation")]
    public async Task Missing_checkpoint_retry_requires_current_root_volume_and_cancellation_authority(string fault)
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        var volume = RegistrationVolume();
        await SeedRegistrationRootAsync(factory, clock.Now, volume, fault == "paused" ? SourceRootState.Paused : SourceRootState.Enabled);
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        using var cancellation = new CancellationTokenSource();
        var reader = new RegistrationReader { ReturnedVolume = fault == "foreign-volume" ? RegistrationVolume() : null,
            BeforeQuery = (_, ct) => { if (fault == "cancellation") cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
            new() { Enabled = true, ApprovedVolumes = [volume] });
        if (fault == "cancellation") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PumpAsync(cancellation.Token).AsTask());
        else if (fault == "foreign-volume") await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PumpAsync(default).AsTask());
        else Assert.Equal(0, await coordinator.PumpAsync(default));
        await using var verify = factory.CreateDbContext();
        Assert.False(await verify.SourceVolumeJournalCheckpoints.AnyAsync());
        Assert.False(await verify.SourceScanRequests.AnyAsync());
        Assert.False(await verify.AuditEvents.AnyAsync(x => x.EventType == "source.journal.first_registration"));
        Assert.Equal(fault == "paused" ? 0 : 1, reader.QueryCalls);
    }

    [NativeSqlServerFact]
    public async Task Missing_checkpoint_query_failure_is_attempted_once_per_pump_while_another_volume_advances()
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        var missing = RegistrationVolume();
        var advancing = RegistrationVolume();
        await SeedRegistrationRootAsync(factory, clock.Now, missing);
        await using (var db = factory.CreateDbContext())
        {
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = advancing, JournalId = "0000000000000001", NextUsn = 100 });
            await db.SaveChangesAsync();
        }
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        var reader = new RegistrationReader { BeforeQuery = (_, _) => throw new IOException("Temporarily unavailable"), AdvanceReads = true };
        var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
            new() { Enabled = true, ApprovedVolumes = [missing, advancing] });
        Assert.Equal(0, await coordinator.PumpAsync(default));
        Assert.True(reader.ReadCalls > 1); // Exercise the burst's second and later rounds.
        Assert.Equal(1, reader.QueryCalls);
        Assert.Equal(0, await coordinator.PumpAsync(default));
        Assert.Equal(2, reader.QueryCalls); // The next service poll can retry again.
        await using var verify = factory.CreateDbContext();
        Assert.False(await verify.SourceVolumeJournalCheckpoints.AnyAsync(x => x.VolumeIdentity == missing));
    }

    private static string RegistrationVolume() => $@"\\?\Volume{{{Guid.NewGuid()}}}\";

    private static async Task<Guid> SeedRegistrationRootAsync(SourceJournalStoreIntegrationTests.ContextFactory factory,
        DateTimeOffset now, string volume, SourceRootState rootState = SourceRootState.Enabled)
    {
        await using var db = factory.CreateDbContext();
        var id = Guid.NewGuid();
        db.SourceRootConfigurations.Add(new() { Id = id, CanonicalPath = $@"I:\registration-{id:N}", DisplayName = "First registration fixture",
            State = (int)rootState, ConfigurationRevision = 4, ReconciliationCadenceSeconds = SourcePolicyDefaults.MetadataReconciliationSeconds,
            LastScanCompletedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.SourceRootScanStates.Add(new() { SourceRootId = id, VolumeIdentity = volume, Mode = "Fallback", BaselineEpoch = 3,
            RecoveryGeneration = 7, Generation = 11, CoveredUsn = 0, NamespaceGeneration = 9, NamespaceComplete = true, LastReconciliationAtUtc = now });
        db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = id, RelativePath = "later.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey("later.txt"),
            ReasonFlags = SourcePendingReasons.FileEvent, Generation = 11, LastContentUsn = 0, LastUnjournaledGeneration = 11,
            NotBeforeUtc = now.AddDays(7), FirstObservedAtUtc = now });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class RegistrationReader : ISourceChangeJournalReader
    {
        public int QueryCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public string? ReturnedVolume { get; init; }
        public bool AdvanceReads { get; init; }
        public Func<string, CancellationToken, Task>? BeforeQuery { get; init; }
        public async ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken ct)
        {
            QueryCalls++;
            if (BeforeQuery is not null) await BeforeQuery(volumeIdentity, ct);
            return new(ReturnedVolume ?? volumeIdentity, 1, 0, 100, 0, 1024, 128);
        }
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct)
        {
            ReadCalls++;
            return ValueTask.FromResult(new JournalPage(cursor, AdvanceReads ? cursor with { NextUsn = cursor.NextUsn + 1 } : cursor,
                AdvanceReads ? [new(cursor.NextUsn, "0000000000000001", "0000000000000002", "unrelated.txt", 1, false)] : []));
        }
    }
}
