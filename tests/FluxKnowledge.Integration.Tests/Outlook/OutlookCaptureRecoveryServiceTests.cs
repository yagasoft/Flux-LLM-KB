using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Outlook;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Outlook;

public sealed class OutlookCaptureRecoveryServiceTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Restart_releases_only_stale_catch_up_leases_and_replays_pending_hints()
    {
        var stale = new OutlookCatchUpLeaseRecoveryCandidate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new OutlookCaptureProfileId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            7,
            Now.AddMinutes(-11));
        var nonStale = new OutlookCatchUpLeaseRecoveryCandidate(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            new OutlookCaptureProfileId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            9,
            Now.AddMinutes(-5));
        var pendingHint = new OutlookHintRecoveryCandidate(
            new OutlookHintRequest(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                new string('a', 64),
                stale.ProfileId,
                "durable-hint",
                "event-hint"),
            Now.AddSeconds(-6));
        var debouncingHint = new OutlookHintRecoveryCandidate(
            new OutlookHintRequest(
                Guid.Parse("66666666-6666-6666-6666-666666666666"),
                new string('b', 64),
                nonStale.ProfileId,
                "fresh-hint",
                "event-hint"),
            Now.AddSeconds(-1));
        var store = new RecordingRecoveryStore(
            new OutlookCaptureRecoverySnapshot([stale, nonStale], [pendingHint, debouncingHint]));
        var services = new ServiceCollection();
        services.AddSingleton<IOutlookCaptureRecoveryStore>(store);
        using var provider = services.BuildServiceProvider();
        var options = new OutlookCaptureRecoveryOptions
        {
            Enabled = true,
            HintDebounce = TimeSpan.FromSeconds(5),
            RecoveryCadence = TimeSpan.FromMinutes(1),
            StaleLeaseAge = TimeSpan.FromMinutes(10)
        };
        var recovery = new OutlookCaptureRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(Now),
            options);

        var result = await recovery.RunOnceAsync(CancellationToken.None);

        Assert.Equal(Now.AddMinutes(-10), store.StaleBeforeUtc);
        Assert.Equal(Now.AddSeconds(-5), store.PendingHintBeforeUtc);
        var release = Assert.Single(store.Releases);
        Assert.Equal(stale.CatchUpId, release.CatchUpId);
        Assert.Equal(stale.ProfileId, release.ProfileId);
        Assert.Equal(stale.FencingToken, release.FencingToken);
        Assert.Equal(stale.LeaseExpiresAtUtc, release.LeaseExpiresAtUtc);
        Assert.Equal(pendingHint.Hint, Assert.Single(store.ReplayedHints));
        Assert.Equal(new OutlookCaptureRecoveryResult(1, 1), result);
    }

    [NativeSqlServerFact]
    public async Task Durable_recovery_filters_history_and_fences_a_renewed_stale_lease()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var sourceRootId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var staleCatchUpId = Guid.NewGuid();
        var currentCatchUpId = Guid.NewGuid();
        var sameTokenSiblingId = Guid.NewGuid();
        var pendingHintId = Guid.NewGuid();
        var freshHintId = Guid.NewGuid();
        var pendingHintOperation = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = sourceRootId,
                CanonicalPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}",
                DisplayName = "Private Outlook capture",
                State = (int)SourceRootState.Paused,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]",
                MaximumFileBytes = 64L * 1024 * 1024,
                ReconciliationCadenceSeconds = 86400,
                ConfigurationRevision = 1,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            });
            context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
            {
                Id = profileId,
                SourceRootId = sourceRootId,
                DisplayName = "Recovery profile",
                SpoolRoot = "C:\\private\\outlook",
                IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
                State = (int)OutlookCaptureState.CatchingUp,
                IsEnabled = true,
                ConfigurationRevision = 1,
                CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
                MaximumOverlapTicks = TimeSpan.FromMinutes(5).Ticks,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            });
            context.OutlookCatchUps.AddRange(
                Lease(staleCatchUpId, profileId, Now.AddMinutes(-11), 3),
                Lease(currentCatchUpId, profileId, Now.AddMinutes(-5), 4),
                Lease(sameTokenSiblingId, profileId, Now.AddMinutes(-5), 3),
                Hint(pendingHintId, profileId, "pending-hint"),
                Hint(freshHintId, profileId, "fresh-hint"));
            context.OutlookCaptureOperations.AddRange(
                HintOperation(pendingHintOperation, pendingHintId, profileId, Now.AddSeconds(-6), new string('c', 64)),
                HintOperation(Guid.NewGuid(), freshHintId, profileId, Now.AddSeconds(-1), new string('d', 64)));
            for (var index = 0; index < 101; index++)
            {
                var manualCatchUpId = Guid.NewGuid();
                context.OutlookCatchUps.Add(new OutlookCatchUpEntity
                {
                    Id = manualCatchUpId,
                    ProfileId = profileId,
                    CoalescingKey = $"manual-history-{index}",
                    Provenance = (int)OutlookCatchUpProvenance.Manual,
                    State = 0
                });
                context.OutlookCaptureOperations.Add(HintOperation(
                    Guid.NewGuid(),
                    manualCatchUpId,
                    profileId,
                    Now.AddMinutes(-30).AddSeconds(index),
                    index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)));
            }
            await context.SaveChangesAsync();
        }
        var store = new SqlOutlookCaptureStore(factory, new FixedTimeProvider(Now));

        var snapshot = await store.ReadRecoverySnapshotAsync(
            Now.AddMinutes(-10),
            Now.AddSeconds(-5),
            CancellationToken.None);

        Assert.Equal(staleCatchUpId, Assert.Single(snapshot.CatchUpLeases).CatchUpId);
        var hint = Assert.Single(snapshot.PendingHints);
        Assert.Equal(pendingHintOperation, hint.Hint.OperationId);
        Assert.Equal("pending-hint", hint.Hint.CoalescingKey);
        var observed = Assert.Single(snapshot.CatchUpLeases);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var renewed = await EntityFrameworkQueryableExtensions.SingleAsync(
                context.OutlookCatchUps,
                candidate => candidate.Id == staleCatchUpId,
                CancellationToken.None);
            renewed.LeaseExpiresAtUtc = Now.AddMinutes(5);
            renewed.LastHeartbeatAtUtc = Now;
            await context.SaveChangesAsync();
        }

        var receipt = await store.ReleaseStaleCatchUpLeaseAsync(
            new OutlookStaleCatchUpLeaseReleaseRequest(
                Guid.NewGuid(),
                new string('e', 64),
                observed.CatchUpId,
                observed.ProfileId,
                observed.FencingToken,
                observed.LeaseExpiresAtUtc),
            CancellationToken.None);

        Assert.False(receipt.Accepted);
        await using var verification = await factory.CreateDbContextAsync();
        var rows = await EntityFrameworkQueryableExtensions.ToDictionaryAsync(
            verification.OutlookCatchUps.Where(
                candidate => candidate.Id == staleCatchUpId || candidate.Id == sameTokenSiblingId),
            candidate => candidate.Id,
            CancellationToken.None);
        Assert.Equal(1, rows[staleCatchUpId].State);
        Assert.Equal(Now.AddMinutes(5), rows[staleCatchUpId].LeaseExpiresAtUtc);
        Assert.Equal(1, rows[sameTokenSiblingId].State);
    }

    [Fact]
    public async Task Disabled_recovery_never_reads_or_mutates_durable_state()
    {
        var store = new RecordingRecoveryStore(
            new OutlookCaptureRecoverySnapshot([], []));
        var services = new ServiceCollection();
        services.AddSingleton<IOutlookCaptureRecoveryStore>(store);
        using var provider = services.BuildServiceProvider();
        var recovery = new OutlookCaptureRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(Now),
            new OutlookCaptureRecoveryOptions { Enabled = false });

        var result = await recovery.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookCaptureRecoveryResult.Empty, result);
        Assert.Equal(0, store.ReadCount);
        Assert.Empty(store.Releases);
        Assert.Empty(store.ReplayedHints);
    }

    private sealed class RecordingRecoveryStore(OutlookCaptureRecoverySnapshot snapshot)
        : IOutlookCaptureRecoveryStore
    {
        public int ReadCount { get; private set; }
        public DateTimeOffset? StaleBeforeUtc { get; private set; }
        public DateTimeOffset? PendingHintBeforeUtc { get; private set; }
        public List<OutlookStaleCatchUpLeaseReleaseRequest> Releases { get; } = [];
        public List<OutlookHintRequest> ReplayedHints { get; } = [];

        public ValueTask<OutlookCaptureRecoverySnapshot> ReadRecoverySnapshotAsync(
            DateTimeOffset staleBeforeUtc,
            DateTimeOffset pendingHintBeforeUtc,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            StaleBeforeUtc = staleBeforeUtc;
            PendingHintBeforeUtc = pendingHintBeforeUtc;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<OutlookOperationReceipt> ReleaseStaleCatchUpLeaseAsync(
            OutlookStaleCatchUpLeaseReleaseRequest request,
            CancellationToken cancellationToken)
        {
            Releases.Add(request);
            return ValueTask.FromResult(new OutlookOperationReceipt(request.OperationId, true, true, false));
        }

        public ValueTask<OutlookOperationReceipt> ReplayHintAsync(
            OutlookHintRequest request,
            CancellationToken cancellationToken)
        {
            ReplayedHints.Add(request);
            return ValueTask.FromResult(new OutlookOperationReceipt(request.OperationId, true, true, true));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static OutlookCatchUpEntity Lease(
        Guid id,
        Guid profileId,
        DateTimeOffset expiresAtUtc,
        long fencingToken) => new()
    {
        Id = id,
        ProfileId = profileId,
        CoalescingKey = $"lease-{id:N}",
        Provenance = (int)OutlookCatchUpProvenance.Manual,
        State = 1,
        LeaseOwner = "S-1-5-21-100|4|host-a",
        LeaseExpiresAtUtc = expiresAtUtc,
        LastHeartbeatAtUtc = expiresAtUtc.AddMinutes(-1),
        FencingToken = fencingToken
    };

    private static OutlookCatchUpEntity Hint(Guid id, Guid profileId, string key) => new()
    {
        Id = id,
        ProfileId = profileId,
        CoalescingKey = key,
        Provenance = (int)OutlookCatchUpProvenance.Hint,
        State = 0
    };

    private static OutlookCaptureOperationEntity HintOperation(
        Guid operationId,
        Guid catchUpId,
        Guid profileId,
        DateTimeOffset recordedAtUtc,
        string fingerprint) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = profileId,
        Kind = "request-catchup",
        OperationId = operationId,
        RequestFingerprint = fingerprint,
        ResourceId = catchUpId,
        Accepted = true,
        CompletedAtUtc = recordedAtUtc
    };
}
