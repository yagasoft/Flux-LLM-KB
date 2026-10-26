using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceJournalSchedulingIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    [SupportedOSPlatform("windows")]
    public async Task Unauthenticated_helper_cannot_advance_durable_cursor_or_root_coverage()
    {
        var rootId = Guid.NewGuid();
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalAuthentication-" + rootId.ToString("N"));
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [volume],
            PipeName = "FluxKnowledge.SourceJournal.test-" + Guid.NewGuid().ToString("N") };
        var security = new PipeSecurity();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 65536, security, HandleInheritability.None, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<int> ObserveRequestBytesAsync()
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            return await pipe.ReadAsync(new byte[1], deadline.Token);
        }
        var server = ObserveRequestBytesAsync();
        await using (var setup = factory.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            setup.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = Path.Combine(area, "Source"), DisplayName = "Authentication fixture",
                Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volume, BaselineEpoch = 3,
                Mode = "Journal", PolicyFingerprint = "test", CoveredUsn = 100, NamespaceComplete = true });
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000007", NextUsn = 100 });
            await setup.SaveChangesAsync(deadline.Token);
        }
        try
        {
            using var artifacts = new ContentAddressedSourceArtifactStore(area);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, new SourceJournalPipeClient(options),
                new WindowsSourceObservationReader(), artifacts, options);
            _ = await coordinator.PumpAsync(deadline.Token);
            Assert.Equal(0, await server);
            await using var verify = factory.CreateDbContext();
            var point = await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume);
            Assert.Equal("0000000000000007", point.JournalId);
            Assert.Equal(100, point.NextUsn);
            Assert.Equal("journal-unavailable", point.LastFailureCode);
            Assert.NotNull(point.UnavailableSinceUtc);
            Assert.Null(point.LeaseOwner);
            var state = await verify.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId);
            Assert.Equal(100, state.CoveredUsn);
            Assert.Equal(3, state.BaselineEpoch);
            Assert.True(state.NamespaceComplete);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceScanOutbox.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
            await cleanup.SourceScanJobs.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
            await cleanup.SourceScanRequests.Where(x => x.SourceRootId == rootId).ExecuteDeleteAsync();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
            if (Directory.Exists(area)) Directory.Delete(area, true);
        }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Helper_deadline_yields_to_other_volumes_and_dispatch_but_caller_cancellation_propagates(bool cancelCaller)
    {
        var rootId = Guid.NewGuid();
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalDeadline-" + rootId.ToString("N"));
        var volumes = new[] { $@"\\?\Volume{{{Guid.NewGuid()}}}\", $@"\\?\Volume{{{Guid.NewGuid()}}}\" };
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        using var caller = new CancellationTokenSource();
        await using (var setup = factory.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            setup.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = Path.Combine(area, "Source"), DisplayName = "Deadline fixture",
                Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volumes[1], BaselineEpoch = 1, Mode = "Journal", PolicyFingerprint = "test" });
            setup.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = "pending.txt", PathFingerprint = "pending", Generation = 1, ReasonFlags = 1 });
            foreach (var volume in volumes) setup.SourceVolumeJournalCheckpoints.Add(new()
                { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            await setup.SaveChangesAsync();
        }
        try
        {
            using var artifacts = new ContentAddressedSourceArtifactStore(area);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System,
                new DeadlineReader(volumes[0], cancelCaller ? caller : null), new WindowsSourceObservationReader(), artifacts,
                new SourceJournalOptions { Enabled = true, ApprovedVolumes = volumes });
            if (cancelCaller)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PumpAsync(caller.Token).AsTask());
            else Assert.Equal(1, await coordinator.PumpAsync(caller.Token));
            await using var verify = factory.CreateDbContext();
            var first = await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volumes[0]);
            var second = await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volumes[1]);
            Assert.Equal(100, first.NextUsn);
            if (cancelCaller)
            {
                Assert.Equal(100, second.NextUsn);
                Assert.False(await verify.SourceScanRequests.AnyAsync(x => x.SourceRootId == rootId));
            }
            else
            {
                Assert.True(second.NextUsn > 100);
                Assert.NotNull(first.UnavailableSinceUtc);
                Assert.Null(first.LeaseOwner);
                var request = Assert.Single(await verify.SourceScanRequests.Where(x => x.SourceRootId == rootId).ToArrayAsync());
                Assert.Equal((int)SourceScanRequestState.Released, request.State);
                Assert.True(await verify.SourcePendingChanges.AnyAsync(x => x.SourceRootId == rootId));
            }
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceScanOutbox.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
            await cleanup.SourceScanJobs.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
            await cleanup.SourceScanRequests.Where(x => x.SourceRootId == rootId).ExecuteDeleteAsync();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => volumes.Contains(x.VolumeIdentity)).ExecuteDeleteAsync();
            if (Directory.Exists(area)) Directory.Delete(area, true);
        }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Busy_or_unavailable_first_volume_does_not_starve_another_volume(bool firstUnavailable)
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalScheduling-" + Guid.NewGuid().ToString("N"));
        var volumes = new[] { @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", @"\\?\Volume{00000000-0000-0000-0000-000000000002}\" };
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            foreach (var volume in volumes) setup.SourceVolumeJournalCheckpoints.Add(new()
                { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            await setup.SaveChangesAsync();
        }
        try
        {
            using var artifacts = new ContentAddressedSourceArtifactStore(area);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System,
                new BusyReader(firstUnavailable ? volumes[0] : null), new WindowsSourceObservationReader(), artifacts,
                new SourceJournalOptions { Enabled = true, ApprovedVolumes = volumes });
            await coordinator.PumpAsync(default);
            await using var verify = factory.CreateDbContext();
            Assert.True((await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volumes[1])).NextUsn > 100);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => volumes.Contains(x.VolumeIdentity)).ExecuteDeleteAsync();
            if (Directory.Exists(area)) Directory.Delete(area, true);
        }
    }

    [NativeSqlServerFact]
    public async Task Barrier_claim_bypasses_idle_delay_but_never_an_existing_reader_owner()
    {
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var now = DateTimeOffset.UtcNow;
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume,
                JournalId = "0000000000000001", NextUsn = 100, DueAtUtc = now.AddSeconds(2) });
            await setup.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, TimeProvider.System);
            Assert.Null(await store.ClaimVolumeAsync(volume, now, false, default));
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, now, true, default));
            Assert.Null(await new SqlSourceChangeJournalStore(factory, TimeProvider.System).ClaimVolumeAsync(volume, now, true, default));
            await store.AcceptAsync(claim, new(claim.Cursor, claim.Cursor with { NextUsn = 200 }, []), default);
            await using var verify = factory.CreateDbContext();
            Assert.Equal(200, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    private sealed class DeadlineReader(string firstVolume, CancellationTokenSource? caller) : ISourceChangeJournalReader
    {
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken)
        {
            if (cursor.VolumeIdentity == firstVolume)
            {
                caller?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Fixture helper deadline; the caller remains active.");
            }
            return ValueTask.FromResult(new JournalPage(cursor, cursor with { NextUsn = cursor.NextUsn + 100 }, []));
        }
    }

    private sealed class BusyReader(string? unavailableVolume) : ISourceChangeJournalReader
    {
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JournalCapability(volumeIdentity, 1, 0, long.MaxValue, 0, 1024, 128));

        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken)
        {
            if (cursor.VolumeIdentity == unavailableVolume) throw new IOException("Fixture journal unavailable");
            return ValueTask.FromResult(new JournalPage(cursor, cursor with { NextUsn = cursor.NextUsn + 100 },
                [new(cursor.NextUsn + 1, "000000000000002A", "0000000000000007", "outside.txt", 1, false)]));
        }
    }
}
