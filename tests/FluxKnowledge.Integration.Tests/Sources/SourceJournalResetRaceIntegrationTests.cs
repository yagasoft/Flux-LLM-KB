using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceJournalResetRaceIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Registration_racing_a_reset_releases_the_old_claim_then_invalidates_every_registered_root()
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalResetRace-" + Guid.NewGuid().ToString("N"));
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(Root(ids[0]));
            setup.SourceRootScanStates.Add(new() { SourceRootId = ids[0], VolumeIdentity = volume, BaselineEpoch = 1, RecoveryGeneration = 1, Mode = "Journal", NamespaceComplete = true });
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            await setup.SaveChangesAsync();
        }
        try
        {
            var reader = new ResetReader(volume, async () =>
            {
                await using var enrol = factory.CreateDbContext();
                enrol.SourceRootConfigurations.Add(Root(ids[1]));
                enrol.SourceRootScanStates.Add(new() { SourceRootId = ids[1], VolumeIdentity = volume, BaselineEpoch = 1,
                    RecoveryGeneration = 1, RecoveryRequired = true, Mode = "BaselineRequired" });
                (await enrol.SourceVolumeJournalCheckpoints.SingleAsync()).RegistrationGeneration++;
                await enrol.SaveChangesAsync();
            });
            using var artifacts = new ContentAddressedSourceArtifactStore(area);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, reader,
                new WindowsSourceObservationReader(), artifacts, new() { Enabled = true, ApprovedVolumes = [volume] });
            await coordinator.PumpAsync(default);
            await using (var first = factory.CreateDbContext())
            {
                var point = await first.SourceVolumeJournalCheckpoints.SingleAsync();
                Assert.Equal(100, point.NextUsn);
                Assert.Equal("0000000000000001", point.JournalId);
                Assert.Null(point.LeaseOwner);
                Assert.Equal(1, (await first.SourceRootScanStates.SingleAsync(x => x.SourceRootId == ids[0])).BaselineEpoch);
            }
            await coordinator.PumpAsync(default);
            await using var final = factory.CreateDbContext();
            var reset = await final.SourceVolumeJournalCheckpoints.SingleAsync();
            Assert.Equal(1000, reset.NextUsn);
            Assert.Equal("0000000000000002", reset.JournalId);
            var states = await final.SourceRootScanStates.OrderBy(x => x.SourceRootId).ToArrayAsync();
            Assert.Equal(2, states.Length);
            Assert.All(states, state => { Assert.True(state.RecoveryRequired); Assert.Equal(2, state.RecoveryGeneration); });
            Assert.Equal(2, states.Single(x => x.SourceRootId == ids[0]).BaselineEpoch);
            Assert.Equal(1, states.Single(x => x.SourceRootId == ids[1]).BaselineEpoch); // Unstarted recovery coalesces.
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceScanOutbox.Where(x => ids.Contains(x.SourceScanRequest.SourceRootId)).ExecuteDeleteAsync();
            await cleanup.SourceScanJobs.Where(x => ids.Contains(x.SourceScanRequest.SourceRootId)).ExecuteDeleteAsync();
            await cleanup.SourceScanRequests.Where(x => ids.Contains(x.SourceRootId)).ExecuteDeleteAsync();
            await cleanup.SourceRootConfigurations.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
            if (Directory.Exists(area)) Directory.Delete(area, true);
        }

        SourceRootConfigurationEntity Root(Guid id) => new() { Id = id, CanonicalPath = Path.Combine(area, id.ToString("N")), DisplayName = "Reset race fixture",
            Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
            ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    private sealed class ResetReader(string volume, Func<Task> register) : ISourceChangeJournalReader
    {
        private bool _registered;
        public async ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken ct)
        {
            if (!_registered) { _registered = true; await register(); }
            return new(volume, 2, 0, 1000, 0, 1024, 128);
        }
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct)
        {
            if (cursor.JournalId == 1) throw new SourceJournalGapException("Fixture journal replaced");
            return ValueTask.FromResult(new JournalPage(cursor, cursor, []));
        }
    }
}
