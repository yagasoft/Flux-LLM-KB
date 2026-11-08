using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(SourceScanRequestState.Held, false)]
    [InlineData(SourceScanRequestState.Released, false)]
    [InlineData(SourceScanRequestState.Running, false)]
    [InlineData(SourceScanRequestState.Failed, false)]
    [InlineData(SourceScanRequestState.Held, true)]
    [InlineData(SourceScanRequestState.Running, true)]
    public async Task Feature_off_retirement_is_terminal_for_all_queued_states_including_paused_roots(SourceScanRequestState state, bool paused)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await SeedLegacyVerificationAsync(scenario);
        Guid requestId, jobId;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var request = await db.SourceScanRequests.SingleAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind);
            var job = await db.SourceScanJobs.SingleAsync(x => x.SourceScanRequestId == request.Id);
            requestId = request.Id; jobId = job.Id;
            request.State = (int)state; request.IsReleased = state != SourceScanRequestState.Held;
            job.State = state switch { SourceScanRequestState.Running => (int)SourceScanJobState.Running,
                SourceScanRequestState.Failed => (int)SourceScanJobState.Failed, _ => (int)SourceScanJobState.Pending };
            if (state == SourceScanRequestState.Running)
            { job.LeaseOwner = "stopped-fixture-owner"; job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10); job.LeaseGeneration = 7; }
            if (paused) (await db.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Paused;
            await db.SaveChangesAsync();
        }
        scenario.Options.Enabled = false;
        var result = await scenario.Verification.RetireForFeatureOffAsync(new WindowsSourceVerificationReaderSlot(scenario.Runtime), _ => ValueTask.CompletedTask, default);
        Assert.Equal(1, result.Runs); Assert.Equal(1, result.Requests); Assert.Equal(1, result.Jobs);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var request = await db.SourceScanRequests.SingleAsync(x => x.Id == requestId);
            var job = await db.SourceScanJobs.SingleAsync(x => x.Id == jobId);
            Assert.Equal((int)SourceScanRequestState.Failed, request.State);
            Assert.Equal((int)SourceScanJobState.Failed, job.State);
            Assert.Equal(SqlSourceVerificationStore.FeatureOffReason, job.Reason);
            Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAtUtc);
            if (state == SourceScanRequestState.Running) Assert.True(job.LeaseGeneration > 7);
            var run = await db.SourceVerificationRuns.SingleAsync();
            Assert.False(run.Active); Assert.Equal("Invalidated", run.State); Assert.Equal(SqlSourceVerificationStore.FeatureOffReason, run.Reason);
            Assert.Null((await db.SourceRootScanStates.SingleAsync()).LastVerifiedAtUtc);
        }
        Assert.False(await new SqlSourceRootStore(scenario.Factory, TimeProvider.System).ReleaseAsync(scenario.RootId, new(requestId), "after-retirement", default));
        var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(scenario.Factory, TimeProvider.System), []),
            new SqlNativeCorpusActionStore(scenario.Factory, new SourceRootPathPolicy(new LocalIngressOptions([scenario.Sources])), new LocalPrivateContentDisclosure()));
        var retry = new NativeCorpusMutation("job_retry", JsonSerializer.SerializeToElement(new { jobId }));
        var preview = await commands.PreviewAsync(retry, "test", default);
        await Assert.ThrowsAsync<NativeOperationException>(() => commands.CommitAsync(retry, preview.ConfirmationId,
            "retired-retry-" + Guid.NewGuid().ToString("N"), "test", default).AsTask());
        var snapshot = await RetirementSnapshotAsync(scenario);
        var again = await scenario.Verification.RetireForFeatureOffAsync(new WindowsSourceVerificationReaderSlot(scenario.Runtime), _ => ValueTask.CompletedTask, default);
        Assert.Equal(0, again.Runs + again.Requests + again.Jobs);
        Assert.Equal(snapshot, await RetirementSnapshotAsync(scenario));
    }


    [NativeSqlServerFact]
    public async Task Feature_off_retirement_refuses_a_busy_native_slot_or_unproved_stopped_binding_without_mutation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await SeedLegacyVerificationAsync(scenario);
        var store = scenario.Verification;
        var slot = new WindowsSourceVerificationReaderSlot(scenario.Runtime);
        var before = await RetirementSnapshotAsync(scenario);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RetireForFeatureOffAsync(slot, _ => ValueTask.CompletedTask, default).AsTask());
        scenario.Options.Enabled = false;
        using (var held = slot.TryAcquire())
        {
            Assert.NotNull(held);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RetireForFeatureOffAsync(slot, _ => ValueTask.CompletedTask, default).AsTask());
        }
        await Assert.ThrowsAsync<IOException>(() => store.RetireForFeatureOffAsync(slot, _ => throw new IOException("stopped-binding-changed"), default).AsTask());
        var guards = 0;
        await Assert.ThrowsAsync<IOException>(() => store.RetireForFeatureOffAsync(slot, _ =>
        { if (++guards > 1) throw new IOException("stopped-binding-changed-before-commit"); return ValueTask.CompletedTask; }, default).AsTask());
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
        using var released = slot.TryAcquire();
        Assert.NotNull(released);
    }

    private static async Task<string> RetirementSnapshotAsync(Scenario scenario)
    {
        await using var db = scenario.Factory.CreateDbContext();
        return JsonSerializer.Serialize(new { Runs = await db.SourceVerificationRuns.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Requests = await db.SourceScanRequests.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Jobs = await db.SourceScanJobs.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(), Preserved = await RetirementPreservedSnapshotAsync(scenario) });
    }

    private static async Task<string> RetirementPreservedSnapshotAsync(Scenario scenario)
    {
        await using var db = scenario.Factory.CreateDbContext();
        return JsonSerializer.Serialize(new { Items = await db.SourceVerificationItems.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            State = await db.SourceRootScanStates.AsNoTracking().OrderBy(x => x.SourceRootId).ToArrayAsync(),
            Roots = await db.SourceRootConfigurations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Pending = await db.SourcePendingChanges.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Targeted = await db.SourceScanRequests.AsNoTracking().Where(x => x.RequestKind == 3).OrderBy(x => x.Id).ToArrayAsync(),
            SourceOutbox = await db.SourceScanOutbox.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            PipelineOutbox = await db.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync() });
    }
}
