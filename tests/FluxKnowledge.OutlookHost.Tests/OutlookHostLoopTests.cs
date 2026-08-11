using System.Data.Common;
using System.Reflection;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class OutlookHostLoopTests
{
    private static readonly DateTimeOffset CursorUtc = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Program_composes_and_runs_default_disabled_host_behavior()
    {
        var application = new FakeHostApplication(new OutlookHostRunResult(OutlookHostExitReason.Disabled));
        var factory = new FakeHostApplicationFactory(application);

        var exitCode = await Program.RunAsync([], factory, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, factory.CreateCount);
        Assert.False(factory.Options!.Enabled);
        Assert.Equal(1, application.RunCount);
    }

    [Fact]
    public async Task Disabled_startup_never_activates_COM()
    {
        var fixture = Fixture.Create(enabled: false);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Disabled, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Equal(0, fixture.ControlPlane.ClaimCount);
    }

    [Theory]
    [InlineData(false, true, true, OutlookHostExitReason.NotWindows)]
    [InlineData(true, false, true, OutlookHostExitReason.NonInteractiveSession)]
    [InlineData(true, true, false, OutlookHostExitReason.SingletonUnavailable)]
    public async Task Environment_and_singleton_gates_run_before_COM(
        bool isWindows,
        bool isInteractive,
        bool singletonAvailable,
        OutlookHostExitReason expected)
    {
        var fixture = Fixture.Create(
            isWindows: isWindows,
            isInteractive: isInteractive,
            singletonAvailable: singletonAvailable);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(expected, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Equal(singletonAvailable && isWindows && isInteractive ? 1 : 0, fixture.ControlPlane.ClaimCount);
    }

    [Fact]
    public async Task No_durable_work_never_activates_COM()
    {
        var fixture = Fixture.Create(hasWork: false);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.NoDurableWork, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
    }

    [Fact]
    public async Task Disabled_durable_claim_never_activates_COM()
    {
        var work = CreateWork() with { IsDurablyEnabled = false };
        var fixture = Fixture.Create(work: work);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.DurableClaimDisabled, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Single(fixture.ControlPlane.Failures);
    }

    [Fact]
    public async Task Claim_bound_to_another_Windows_session_never_activates_COM()
    {
        var work = CreateWork();
        work = work with
        {
            Claim = work.Claim with
            {
                LeaseOwner = new OutlookHostIdentity("S-1-5-21-other", 99, "host-other")
            }
        };
        var fixture = Fixture.Create(work: work);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.DurableClaimDisabled, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Single(fixture.ControlPlane.Failures);
    }

    [Fact]
    public async Task Catch_up_without_durable_folder_configuration_never_activates_COM()
    {
        var work = CreateWork() with { Folders = [] };
        var fixture = Fixture.Create(work: work);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.DurableClaimDisabled, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Single(fixture.ControlPlane.Failures);
    }

    [Fact]
    public async Task Stale_catch_up_lease_is_rejected_before_COM_activation()
    {
        var work = CreateWork();
        work = work with
        {
            Claim = work.Claim with { LeaseExpiresAtUtc = CursorUtc.AddSeconds(-1) }
        };
        var fixture = Fixture.Create(work: work);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.LeaseStale, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Equal([OutlookCatchUpFailureReason.LeaseLost], fixture.ControlPlane.Failures);
    }

    [Fact]
    public async Task Lease_loss_during_catch_up_stops_before_message_read_or_ingestion()
    {
        var item = Item("lease-lost-entry", CursorUtc.AddMinutes(1), CursorUtc);
        var adapter = new FakeClassicOutlookAdapter([item]);
        var fixture = Fixture.Create(adapter: adapter, leaseRenewalAccepted: false);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.LeaseStale, result.Reason);
        Assert.Equal(0, fixture.Factory.ActivationCount);
        Assert.Equal(0, adapter.ReadCount);
        Assert.Empty(fixture.Ingestion.IngestedEntryIds);
        Assert.Equal([OutlookCatchUpFailureReason.LeaseLost], fixture.ControlPlane.Failures);
    }

    [Fact]
    public async Task Item_notification_records_one_hint_and_never_exports_inside_the_callback()
    {
        var item = Item("entry-1", CursorUtc.AddMinutes(1), CursorUtc.AddMinutes(-10));
        var adapter = new FakeClassicOutlookAdapter([item], new OutlookHint("folder-change"));
        var fixture = Fixture.Create(adapter: adapter);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal(["hint", "read", "ingest", "complete"], fixture.Events);
        Assert.Single(fixture.ControlPlane.Hints);
        Assert.Single(fixture.Ingestion.IngestedEntryIds);
    }

    [Fact]
    public void Item_event_releases_transient_COM_argument_after_recording_the_hint()
    {
        var transientItem = new object();
        var events = new List<string>();

        ClassicOutlookComAdapter.ObserveHint(
            transientItem,
            hint =>
            {
                Assert.Equal("folder-change", hint.CoalescingKey);
                events.Add("hint");
                return ValueTask.CompletedTask;
            },
            released =>
            {
                Assert.Same(transientItem, released);
                events.Add("release");
            });

        Assert.Equal(["hint", "release"], events);
    }

    [Fact]
    public async Task Last_modification_overlap_captures_an_older_item_moved_into_the_folder()
    {
        var moved = Item("entry-moved", CursorUtc.AddMinutes(-4), CursorUtc.AddDays(-2));
        var adapter = new FakeClassicOutlookAdapter([moved]);
        var fixture = Fixture.Create(adapter: adapter);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal(CursorUtc.AddMinutes(-5), adapter.LastCursor!.FromUtc);
        Assert.Equal(OutlookIncrementalBasis.LastModificationTime, adapter.LastCursor.Basis);
        Assert.Equal(["entry-moved"], fixture.Ingestion.IngestedEntryIds);
    }

    [Fact]
    public async Task Non_default_store_identity_is_carried_to_message_read()
    {
        var item = Item("entry-store", CursorUtc.AddMinutes(1), CursorUtc, "archive-store");
        var adapter = new FakeClassicOutlookAdapter([item]);
        var fixture = Fixture.Create(adapter: adapter);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal("archive-store", adapter.LastReadStoreId);
    }

    [Fact]
    public async Task Empty_scan_renews_before_activation_and_again_before_completion()
    {
        var fixture = Fixture.Create(adapter: new FakeClassicOutlookAdapter());

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.True(fixture.ControlPlane.RenewCount >= 2);
        Assert.Equal(1, fixture.ControlPlane.CompletionCount);
    }

    [Fact]
    public async Task Rejected_completion_is_reported_as_stale_instead_of_completed()
    {
        var fixture = Fixture.Create(completionAccepted: false);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.LeaseStale, result.Reason);
        Assert.Equal(1, fixture.ControlPlane.CompletionCount);
        Assert.Equal([OutlookCatchUpFailureReason.LeaseLost], fixture.ControlPlane.Failures);
    }

    [Fact]
    public async Task Renewed_lease_expiry_is_used_by_the_COM_activation_gate()
    {
        var work = CreateWork() with
        {
            Claim = CreateWork().Claim with { LeaseExpiresAtUtc = CursorUtc.AddSeconds(1) }
        };
        await using var dispatcher = new OutlookStaDispatcher();
        var adapter = new FakeClassicOutlookAdapter();
        var activator = new CountingComActivator(adapter);
        var gatedFactory = new GatedClassicOutlookAdapterFactory(activator, dispatcher);
        var fixture = Fixture.Create(
            work: work,
            adapter: adapter,
            adapterFactory: gatedFactory,
            renewedLeaseExpiry: CursorUtc.AddMinutes(10),
            timeProvider: new SequenceTimeProvider(CursorUtc, CursorUtc.AddSeconds(2)));

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal(1, activator.ActivationCount);
    }

    [Fact]
    public async Task Periodic_heartbeat_loss_during_long_scan_prevents_read_and_completion()
    {
        var item = Item("long-entry", CursorUtc.AddMinutes(1), CursorUtc);
        var adapter = new FakeClassicOutlookAdapter([item], enumerationDelay: TimeSpan.FromMilliseconds(100));
        var fixture = Fixture.Create(
            adapter: adapter,
            leaseRenewalResults: [true, false],
            heartbeatCadence: TimeSpan.FromMilliseconds(10));

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.LeaseStale, result.Reason);
        Assert.Equal(0, adapter.ReadCount);
        Assert.Equal(0, fixture.ControlPlane.CompletionCount);
        Assert.True(fixture.ControlPlane.RenewCount >= 2);
    }

    [Fact]
    public async Task Catch_up_deduplicates_repeated_entry_ids_before_export()
    {
        var first = Item("same-entry", CursorUtc.AddMinutes(1), CursorUtc);
        var duplicate = Item("same-entry", CursorUtc.AddMinutes(2), CursorUtc);
        var adapter = new FakeClassicOutlookAdapter([first, duplicate]);
        var fixture = Fixture.Create(adapter: adapter);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal(1, adapter.ReadCount);
        Assert.Equal(["same-entry"], fixture.Ingestion.IngestedEntryIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_spool_or_SQL_ingestion_failure_is_sanitised_without_completing_or_advancing_cursor(
        bool sqlFailure)
    {
        var work = CreateWork();
        var item = Item("failed-entry", CursorUtc.AddMinutes(1), CursorUtc);
        Exception failure = sqlFailure
            ? new FakeDbException("private SQL diagnostics")
            : new IOException("private spool path");
        var ingestion = new FakeIngestionBridge([], failure);
        var fixture = Fixture.Create(
            work: work,
            adapter: new FakeClassicOutlookAdapter([item]),
            ingestion: ingestion);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.IngestionFailed, result.Reason);
        Assert.Equal([OutlookCatchUpFailureReason.RetryableHostFailure], fixture.ControlPlane.Failures);
        Assert.Equal(0, fixture.ControlPlane.CompletionCount);
        Assert.Empty(ingestion.IngestedEntryIds);
        Assert.Equal(CursorUtc, Assert.Single(work.Folders).CursorUtc);
        Assert.Equal(new string('b', 64), Assert.Single(work.Folders).CursorFingerprint);
    }

    [Fact]
    public async Task Ready_export_fencing_loss_records_lease_lost_without_completing_or_advancing_cursor()
    {
        var work = CreateWork();
        var item = Item("stale-ready-export", CursorUtc.AddMinutes(1), CursorUtc);
        var ingestion = new FakeIngestionBridge([], new OutlookReadyExportLeaseException());
        var fixture = Fixture.Create(
            work: work,
            adapter: new FakeClassicOutlookAdapter([item]),
            ingestion: ingestion);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.LeaseStale, result.Reason);
        Assert.Equal([OutlookCatchUpFailureReason.LeaseLost], fixture.ControlPlane.Failures);
        Assert.Equal(0, fixture.ControlPlane.CompletionCount);
        Assert.Empty(ingestion.IngestedEntryIds);
        Assert.Equal(CursorUtc, Assert.Single(work.Folders).CursorUtc);
        Assert.Equal(new string('b', 64), Assert.Single(work.Folders).CursorFingerprint);
    }

    [Fact]
    public async Task Restart_after_hint_loss_replays_catch_up_without_duplicate_export()
    {
        var item = Item("restart-entry", CursorUtc.AddMinutes(1), CursorUtc);
        var ingestion = new FakeIngestionBridge([]);
        var first = Fixture.Create(adapter: new FakeClassicOutlookAdapter([item]), ingestion: ingestion);
        var second = Fixture.Create(adapter: new FakeClassicOutlookAdapter([item]), ingestion: ingestion);

        await first.Loop.RunOnceAsync(CancellationToken.None);
        var result = await second.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal(["restart-entry"], ingestion.IngestedEntryIds);
        Assert.Equal(2, ingestion.AttemptCount);
    }

    [Fact]
    public void Adapter_contract_exposes_no_mailbox_mutation_member()
    {
        var methodNames = typeof(IClassicOutlookAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(methodNames, name =>
            new[] { "Move", "Delete", "Categories", "UnRead", "Flag", "Reply" }
                .Any(forbidden => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task Gated_factory_rejects_every_missing_prerequisite_before_COM_activation(
        bool isWindows,
        bool isInteractive,
        bool hasSingleton,
        bool hasDurableWork)
    {
        var adapter = new FakeClassicOutlookAdapter();
        var activator = new CountingComActivator(adapter);
        await using var dispatcher = new OutlookStaDispatcher();
        var factory = new GatedClassicOutlookAdapterFactory(activator, dispatcher);
        var identity = new OutlookHostIdentity("S-1-5-21-test", 7, "host-test");
        var context = new OutlookComActivationContext(
            isWindows,
            isInteractive,
            hasSingleton,
            identity,
            hasDurableWork ? CreateWork() : null,
            BrowseClaim: null,
            CursorUtc);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.CreateAsync(context, CancellationToken.None));

        Assert.Equal(0, activator.ActivationCount);
    }

    [Fact]
    public async Task Durable_browse_claim_is_completed_through_the_gated_COM_factory()
    {
        var identity = new OutlookHostIdentity("S-1-5-21-test", 7, "host-test");
        var claim = new OutlookBrowseClaim(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            1,
            identity,
            1,
            CursorUtc.AddMinutes(10));
        var control = new FakeBrowseControlPlane(claim);
        var adapter = new FakeClassicOutlookAdapter();
        var factory = new CountingAdapterFactory(adapter);
        var browser = new OutlookFolderBrowser(
            new OutlookHostOptions { Enabled = true },
            new FakeEnvironment(isWindows: true, isInteractive: true),
            new FakeSingletonFactory(available: true),
            control,
            factory,
            new FixedTimeProvider(CursorUtc));

        var result = await browser.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Equal(1, factory.ActivationCount);
        Assert.Single(control.CompletedFolders);
    }

    [Fact]
    public async Task COM_activation_use_release_and_singleton_ownership_stay_on_one_STA_thread()
    {
        var calls = new List<(string Operation, int ThreadId, ApartmentState Apartment)>();
        await using var dispatcher = new OutlookStaDispatcher();
        var adapter = new ThreadRecordingAdapter(calls);
        var factory = new GatedClassicOutlookAdapterFactory(
            new ThreadRecordingActivator(adapter, calls),
            dispatcher);
        var singleton = new StaOutlookSessionSingletonFactory(
            new ThreadRecordingSingletonFactory(calls),
            dispatcher);
        var fixture = Fixture.Create(
            adapterFactory: factory,
            singletonFactory: singleton);

        var result = await fixture.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(OutlookHostExitReason.Completed, result.Reason);
        Assert.Contains(calls, call => call.Operation == "activate");
        Assert.Contains(calls, call => call.Operation == "dispose-adapter");
        Assert.Contains(calls, call => call.Operation == "acquire-singleton");
        Assert.Contains(calls, call => call.Operation == "release-singleton");
        Assert.Single(calls.Select(call => call.ThreadId).Distinct());
        Assert.All(calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
    }

    private static OutlookItemEnvelope Item(
        string entryId,
        DateTimeOffset modified,
        DateTimeOffset received,
        string storeId = "store") =>
        new(storeId, entryId, modified, received, new string('a', 64));

    private static OutlookHostCatchUpWork CreateWork() =>
        new(
            new OutlookCatchUpClaim(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                new OutlookCaptureProfileId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                "scheduled",
                OutlookCatchUpProvenance.Schedule,
                0,
                null,
                new OutlookHostIdentity("S-1-5-21-test", 7, "host-test"),
                CursorUtc.AddMinutes(10),
                CursorUtc,
                1),
            IsDurablyEnabled: true,
            [new OutlookHostFolderConfiguration(
                new OutlookCaptureFolderId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new OutlookFolderIdentity("store", "folder", "Inbox"),
                OutlookIncrementalBasis.LastModificationTime,
                CursorUtc,
                new string('b', 64),
                TimeSpan.FromMinutes(5),
                "C:\\private\\outlook")]);

    private sealed record Fixture(
        OutlookHostLoop Loop,
        CountingAdapterFactory Factory,
        FakeControlPlane ControlPlane,
        FakeIngestionBridge Ingestion,
        List<string> Events)
    {
        public static Fixture Create(
            bool enabled = true,
            bool isWindows = true,
            bool isInteractive = true,
            bool singletonAvailable = true,
            bool hasWork = true,
            bool leaseRenewalAccepted = true,
            bool completionAccepted = true,
            IReadOnlyList<bool>? leaseRenewalResults = null,
            TimeSpan? heartbeatCadence = null,
            DateTimeOffset? renewedLeaseExpiry = null,
            TimeProvider? timeProvider = null,
            OutlookHostCatchUpWork? work = default,
            FakeClassicOutlookAdapter? adapter = null,
            FakeIngestionBridge? ingestion = null,
            IClassicOutlookAdapterFactory? adapterFactory = null,
            IOutlookSessionSingletonFactory? singletonFactory = null)
        {
            var events = new List<string>();
            adapter ??= new FakeClassicOutlookAdapter();
            var factory = adapterFactory as CountingAdapterFactory ?? new CountingAdapterFactory(adapter);
            var control = new FakeControlPlane(hasWork ? work ?? CreateWork() : null, events, leaseRenewalResults)
            {
                LeaseRenewalAccepted = leaseRenewalAccepted,
                CompletionAccepted = completionAccepted,
                RenewedLeaseExpiry = renewedLeaseExpiry
            };
            ingestion ??= new FakeIngestionBridge(events);
            var loop = new OutlookHostLoop(
                new OutlookHostOptions
                {
                    Enabled = enabled,
                    HeartbeatCadence = heartbeatCadence ?? TimeSpan.FromMinutes(1)
                },
                new FakeEnvironment(isWindows, isInteractive),
                singletonFactory ?? new FakeSingletonFactory(singletonAvailable),
                control,
                adapterFactory ?? factory,
                ingestion,
                timeProvider ?? new FixedTimeProvider(CursorUtc));
            return new Fixture(loop, factory, control, ingestion, events);
        }
    }

    private sealed class FakeEnvironment(bool isWindows, bool isInteractive) : IOutlookHostEnvironment
    {
        public bool IsWindows => isWindows;
        public bool IsInteractiveSession => isInteractive;
        public OutlookHostIdentity Identity { get; } = new("S-1-5-21-test", 7, "host-test");
    }

    private sealed class FakeSingletonFactory(bool available) : IOutlookSessionSingletonFactory
    {
        public ValueTask<IAsyncDisposable?> TryAcquireAsync(OutlookHostIdentity identity, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(available ? new Lease() : null);

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeControlPlane(
        OutlookHostCatchUpWork? work,
        List<string> events,
        IReadOnlyList<bool>? leaseRenewalResults = null) : IOutlookHostControlPlane
    {
        private int _renewIndex;
        public int ClaimCount { get; private set; }
        public List<OutlookHint> Hints { get; } = [];
        public List<OutlookCatchUpFailureReason> Failures { get; } = [];
        public bool LeaseRenewalAccepted { get; init; } = true;
        public bool CompletionAccepted { get; init; } = true;
        public DateTimeOffset? RenewedLeaseExpiry { get; init; }
        public int RenewCount { get; private set; }
        public int CompletionCount { get; private set; }

        public ValueTask<OutlookHostCatchUpWork?> TryClaimCatchUpAsync(
            OutlookHostIdentity host,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimCount++;
            return ValueTask.FromResult(work);
        }

        public ValueTask RecordHintAsync(
            OutlookCaptureProfileId profileId,
            OutlookHint hint,
            CancellationToken cancellationToken)
        {
            Hints.Add(hint);
            events.Add("hint");
            return ValueTask.CompletedTask;
        }

        public ValueTask<OutlookCatchUpClaim?> RenewCatchUpAsync(
            OutlookCatchUpClaim claim,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            RenewCount++;
            var accepted = leaseRenewalResults is not null && _renewIndex < leaseRenewalResults.Count
                ? leaseRenewalResults[_renewIndex++]
                : LeaseRenewalAccepted;
            return ValueTask.FromResult<OutlookCatchUpClaim?>(accepted
                ? claim with
                {
                    LeaseExpiresAtUtc = RenewedLeaseExpiry ?? claim.LeaseExpiresAtUtc.Add(leaseDuration),
                    LastHeartbeatAtUtc = CursorUtc
                }
                : null);
        }

        public ValueTask<bool> CompleteCatchUpAsync(OutlookCatchUpClaim claim, int exportedCount, CancellationToken cancellationToken)
        {
            CompletionCount++;
            if (CompletionAccepted)
            {
                events.Add("complete");
            }

            return ValueTask.FromResult(CompletionAccepted);
        }

        public ValueTask FailCatchUpAsync(OutlookCatchUpClaim claim, OutlookCatchUpFailureReason reason, CancellationToken cancellationToken)
        {
            Failures.Add(reason);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeIngestionBridge(List<string> events, Exception? failure = null) : IOutlookExportIngestionBridge
    {
        private readonly HashSet<string> _committed = new(StringComparer.Ordinal);

        public List<string> IngestedEntryIds { get; } = [];
        public int AttemptCount { get; private set; }

        public ValueTask<bool> ExportAndIngestAsync(
            OutlookHostCatchUpWork work,
            OutlookHostFolderConfiguration folder,
            OutlookItemEnvelope item,
            OutlookMessagePayload payload,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            events.Add("read");
            if (failure is not null)
            {
                throw failure;
            }
            if (_committed.Add(item.EntryId))
            {
                IngestedEntryIds.Add(item.EntryId);
                events.Add("ingest");
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class CountingComActivator(FakeClassicOutlookAdapter adapter) : IClassicOutlookComActivator
    {
        public int ActivationCount { get; private set; }

        public IClassicOutlookAdapter Activate()
        {
            ActivationCount++;
            return adapter;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1);
            return values[index];
        }
    }

    private sealed class FakeBrowseControlPlane(OutlookBrowseClaim? claim) : IOutlookFolderBrowseControlPlane
    {
        public List<IReadOnlyList<OutlookFolderDescriptor>> CompletedFolders { get; } = [];

        public ValueTask<OutlookBrowseClaim?> TryClaimBrowseAsync(
            OutlookHostIdentity host,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => ValueTask.FromResult(claim);

        public ValueTask CompleteBrowseAsync(
            OutlookBrowseClaim completedClaim,
            IReadOnlyList<OutlookFolderDescriptor> folders,
            CancellationToken cancellationToken)
        {
            CompletedFolders.Add(folders);
            return ValueTask.CompletedTask;
        }

        public ValueTask FailBrowseAsync(
            OutlookBrowseClaim failedClaim,
            OutlookBrowseFailureCode failureCode,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThreadRecordingActivator(
        IClassicOutlookAdapter adapter,
        List<(string Operation, int ThreadId, ApartmentState Apartment)> calls) : IClassicOutlookComActivator
    {
        public IClassicOutlookAdapter Activate()
        {
            Record("activate", calls);
            return adapter;
        }
    }

    private sealed class ThreadRecordingAdapter(
        List<(string Operation, int ThreadId, ApartmentState Apartment)> calls) : IClassicOutlookAdapter
    {
        public ValueTask<IReadOnlyList<OutlookFolderDescriptor>> BrowseFoldersAsync(CancellationToken cancellationToken)
        {
            Record("browse", calls);
            return ValueTask.FromResult<IReadOnlyList<OutlookFolderDescriptor>>([]);
        }

        public ValueTask<IAsyncDisposable> SubscribeHintsAsync(
            OutlookFolderIdentity folder,
            Func<OutlookHint, ValueTask> onHint,
            CancellationToken cancellationToken)
        {
            Record("subscribe", calls);
            return ValueTask.FromResult<IAsyncDisposable>(new ThreadRecordingSubscription(calls));
        }

        public async IAsyncEnumerable<OutlookItemEnvelope> EnumerateAsync(
            OutlookFolderIdentity folder,
            OutlookCursor cursor,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Record("enumerate", calls);
            await Task.Yield();
            Record("enumerate-continuation", calls);
            yield break;
        }

        public ValueTask<OutlookMessagePayload> ReadForExportAsync(
            OutlookItemEnvelope item,
            CancellationToken cancellationToken)
        {
            Record("read", calls);
            return ValueTask.FromResult(new OutlookMessagePayload("body"u8.ToArray(), "text/plain", []));
        }

        public ValueTask DisposeAsync()
        {
            Record("dispose-adapter", calls);
            return ValueTask.CompletedTask;
        }

        private sealed class ThreadRecordingSubscription(
            List<(string Operation, int ThreadId, ApartmentState Apartment)> calls) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Record("dispose-subscription", calls);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ThreadRecordingSingletonFactory(
        List<(string Operation, int ThreadId, ApartmentState Apartment)> calls) : IOutlookSessionSingletonFactory
    {
        public ValueTask<IAsyncDisposable?> TryAcquireAsync(
            OutlookHostIdentity identity,
            CancellationToken cancellationToken)
        {
            Record("acquire-singleton", calls);
            return ValueTask.FromResult<IAsyncDisposable?>(new ThreadRecordingLease(calls));
        }

        private sealed class ThreadRecordingLease(
            List<(string Operation, int ThreadId, ApartmentState Apartment)> calls) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Record("release-singleton", calls);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static void Record(
        string operation,
        ICollection<(string Operation, int ThreadId, ApartmentState Apartment)> calls) =>
        calls.Add((operation, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

    private sealed class FakeHostApplication(OutlookHostRunResult result) : IOutlookHostApplication
    {
        public int RunCount { get; private set; }

        public ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken)
        {
            RunCount++;
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHostApplicationFactory(IOutlookHostApplication application)
        : IOutlookHostApplicationFactory
    {
        public int CreateCount { get; private set; }
        public OutlookHostOptions? Options { get; private set; }

        public IOutlookHostApplication Create(OutlookHostOptions options)
        {
            CreateCount++;
            Options = options;
            return application;
        }
    }
}
