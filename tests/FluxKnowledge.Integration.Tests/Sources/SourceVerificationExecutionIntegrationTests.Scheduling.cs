using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_start_replaces_a_cycle_with_old_configuration_and_drains_its_stale_request(bool obsoleteJournal)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var first = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        if (obsoleteJournal)
        {
            await using var history = scenario.Factory.CreateDbContext();
            var old = await history.SourceVerificationRuns.SingleAsync(x => x.Id == first);
            var frozen = JsonSerializer.Deserialize<SourceVerificationAuthority>(old.AuthorityJson)!;
            old.AuthorityJson = JsonSerializer.Serialize(frozen with { JournalId = "0000000000000000" });
            await history.SaveChangesAsync();
        }
        await CommitNativeAsync(NativeCommands(scenario), new("root_update", JsonSerializer.SerializeToElement(new
        { rootId = scenario.RootId.Value, displayName = "Renamed verification fixture", reconcile = false })));
        var second = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        Assert.NotEqual(first, second);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(1, await db.SourceVerificationRuns.CountAsync(x => x.Active));
        Assert.Equal("Invalidated", await db.SourceVerificationRuns.Where(x => x.Id == first).Select(x => x.State).SingleAsync());
        var stale = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("stale", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        Assert.Null(await scenario.Verification.PrepareAsync(stale.SourceRoot, stale.ScanRequest, default));
        await scenario.Control.CompleteAsync(stale, new(stale.SourceRoot.Id, stale.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, null, default);
        Assert.Equal(1, await scenario.Verification.PumpAsync(default));
    }

    [NativeSqlServerFact]
    public async Task Policy_change_replaces_the_old_window_deadline_but_preserves_the_inter_batch_floor()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var clock = new WatchClock { Now = DateTimeOffset.Parse("2026-11-01T02:00:00Z") };
        await SetScheduleAsync(scenario, clock.Now);
        var store = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental);
        await store.StartAsync(scenario.RootId, false, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("window", clock.Now, TimeSpan.FromMinutes(10), default));
        var batch = (await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default))!;
        await store.FinishBatchAsync(batch, 0, 0, "batch-limit", default);
        await scenario.Control.CompleteAsync(claim, new(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, null, default);
        clock.Now = DateTimeOffset.Parse("2026-11-01T09:00:00Z");
        await SetScheduleAsync(scenario, clock.Now);
        Assert.Equal(0, await store.PumpAsync(default));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T02:00:00Z"), (await store.ReadAsync(scenario.RootId, default))!.Run!.NextBatchAtUtc);
        await CommitNativeAsync(NativeCommands(scenario, clock), new("source_verification_set", JsonSerializer.SerializeToElement(new
        { rootId = scenario.RootId.Value, policy = SourceVerificationPolicy.Default("UTC") with { LocalStart = new(9, 0) } })));
        Assert.Equal(clock.Now.AddSeconds(5), (await store.ReadAsync(scenario.RootId, default))!.Run!.NextBatchAtUtc);
        Assert.Equal(0, await store.PumpAsync(default));
        clock.Now = clock.Now.AddSeconds(5);
        Assert.Equal(1, await store.PumpAsync(default));
    }

    [NativeSqlServerFact]
    public async Task Scheduler_starts_once_per_week_and_continues_only_in_daily_quiet_windows()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var clock = new WatchClock { Now = DateTimeOffset.Parse("2026-11-01T01:59:59Z") };
        await SetScheduleAsync(scenario, clock.Now);
        var store = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental);
        var scheduler = Assert.IsAssignableFrom<ISourceVerificationScheduler>(store);
        Assert.Equal(0, await scheduler.PumpAsync(default));
        clock.Now = clock.Now.AddSeconds(1);
        var started = await Task.WhenAll(scheduler.PumpAsync(default).AsTask(), scheduler.PumpAsync(default).AsTask());
        Assert.Equal(1, started.Sum());
        var runId = (await store.ReadAsync(scenario.RootId, default))!.Run!.Id;
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("scheduled", clock.Now, TimeSpan.FromMinutes(10), default));
        var batch = Assert.IsType<SourceVerificationBatch>(await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T08:00:00Z"), batch.WindowEndUtc);
        await store.FinishBatchAsync(batch, 0, 0, "batch-limit", default);
        await scenario.Control.CompleteAsync(claim, new(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, null, default);
        Assert.Equal(0, await scheduler.PumpAsync(default)); // Five-second inter-batch floor.
        clock.Now = DateTimeOffset.Parse("2026-11-01T08:00:00Z");
        await SetScheduleAsync(scenario, clock.Now);
        Assert.Equal(0, await scheduler.PumpAsync(default)); // Exact end is closed.
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T02:00:00Z"), (await store.ReadAsync(scenario.RootId, default))!.Run!.NextBatchAtUtc);
        clock.Now = DateTimeOffset.Parse("2026-11-02T02:00:00Z");
        await SetScheduleAsync(scenario, clock.Now);
        Assert.Equal(1, await scheduler.PumpAsync(default));
        Assert.Equal(runId, (await store.ReadAsync(scenario.RootId, default))!.Run!.Id);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(1, await db.SourceVerificationRuns.CountAsync());
        Assert.Equal(2, await db.SourceScanRequests.CountAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind));
    }

    [NativeSqlServerFact]
    public async Task Manual_start_overrides_the_window_for_an_existing_cycle_but_respects_pause()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var clock = new WatchClock { Now = DateTimeOffset.Parse("2026-11-01T02:00:00Z") };
        await SetScheduleAsync(scenario, clock.Now, paused: true);
        var store = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental);
        // Explicit start freezes the cycle while paused, without dispatching it.
        var id = await store.StartAsync(scenario.RootId, false, default);
        clock.Now = clock.Now.AddHours(10);
        await SetScheduleAsync(scenario, clock.Now, paused: true);
        Assert.Equal(id, await store.StartAsync(scenario.RootId, true, default));
        await using (var db = scenario.Factory.CreateDbContext())
        {
            Assert.True((await db.SourceVerificationRuns.SingleAsync()).Manual);
            Assert.False(await db.SourceScanRequests.AnyAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind));
        }
        await SetScheduleAsync(scenario, clock.Now);
        Assert.Equal(1, await Assert.IsAssignableFrom<ISourceVerificationScheduler>(store).PumpAsync(default));
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("manual-override", clock.Now, TimeSpan.FromMinutes(10), default));
        Assert.Equal(DateTimeOffset.MaxValue, (await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default))!.WindowEndUtc);
    }

    [NativeSqlServerFact]
    public async Task Disabled_weekly_policy_and_feature_off_do_not_enqueue_automatic_work()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var clock = new WatchClock { Now = DateTimeOffset.Parse("2026-11-01T02:00:00Z") };
        await SetScheduleAsync(scenario, clock.Now, enabled: false);
        var scheduler = Assert.IsAssignableFrom<ISourceVerificationScheduler>(new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, scenario.Incremental));
        Assert.Equal(0, await scheduler.PumpAsync(default));
        await SetScheduleAsync(scenario, clock.Now);
        scenario.Options.Enabled = false;
        Assert.Equal(0, await scheduler.PumpAsync(default));
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.SourceVerificationRuns.AnyAsync());
    }

    private static async Task SetScheduleAsync(Scenario scenario, DateTimeOffset now, bool paused = false, bool enabled = true)
    {
        await using var db = scenario.Factory.CreateDbContext();
        var root = await db.SourceRootConfigurations.SingleAsync();
        root.LastScanCompletedAtUtc = now;
        var state = await db.SourceRootScanStates.SingleAsync();
        state.VerificationPolicyJson = JsonSerializer.Serialize(SourceVerificationPolicy.Default("UTC") with { Enabled = enabled });
        state.VerificationPaused = paused;
        await db.SaveChangesAsync();
    }
}
