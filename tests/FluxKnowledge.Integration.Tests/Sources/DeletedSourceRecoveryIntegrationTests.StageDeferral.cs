using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerFact]
    public async Task A_source_admission_race_requeues_the_exact_pump_delivery_without_waiting_for_lease_expiry()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var jobId = await QueueSourceDispatchAsync(scenario);
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(scenario.Factory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISourceWorkAdmission>(scenario.Admission);
        var worker = new SourceAdmissionDeferringWorker(scenario.Admission, scenario.Claim.SourceRoot.Id.Value,
            scenario.Claim.SourceRoot.ConfigurationRevision);
        services.AddSingleton<IStageWorker>(worker);
        services.AddSingleton<ChannelOutboxWakeSignal>();
        services.AddSingleton<IOutboxWakeSignal>(p => p.GetRequiredService<ChannelOutboxWakeSignal>());
        services.AddSingleton<IStatusEventPublisher, RecoveryStatusPublisher>();
        services.AddScoped<OutboxWorkerRegistration>();
        services.AddScoped<IOutboxStore, SqlOutboxStore>();
        services.AddScoped<IJobClaimStore, SqlJobClaimStore>();
        services.AddScoped<IStageTransitionStore>(p => new SqlStageTransitionStore(scenario.Factory,
            sourceAdmission: scenario.Admission));
        services.AddScoped<StageTransitionService>();
        services.AddSingleton<OutboxPumpService>();
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(1, await provider.GetRequiredService<OutboxPumpService>().PumpOnceAsync(default));
        await using var verify = scenario.Factory.CreateDbContext();
        var job = await verify.Jobs.SingleAsync(x => x.Id == jobId);
        var dispatch = await verify.OutboxMessages.SingleAsync(x => x.JobId == jobId);
        Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
        Assert.Equal("source-work-deferred", job.Reason);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Null(dispatch.LeaseOwner);
        Assert.Null(dispatch.LeaseExpiresAtUtc);
        Assert.Null(dispatch.DispatchedAtUtc);
        Assert.Equal(job.DueAtUtc, dispatch.DueAtUtc);
        Assert.Null((await verify.PipelineRecords.SingleAsync(x => x.Id == job.PipelineRecordId)).RepositoryRecoveryBindingJson);
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
        var work = Assert.IsType<StageWorkItem>(worker.LastWork);
        await provider.GetRequiredService<IStageTransitionStore>().RetryAsync(new(work.DispatchMessage, work.Job,
            job.DueAtUtc, "source-work-deferred", nameof(OutboxPumpService)), default);
        Assert.Single(await verify.AuditEvents.Where(x => x.EventType == "pipeline.stage_deferred").ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("generation")]
    [InlineData("gpu-queued")]
    [InlineData("expired")]
    [InlineData("unlinked")]
    public async Task Source_admission_deferral_refuses_work_it_no_longer_owns(string refusal)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var jobId = await QueueSourceDispatchAsync(scenario, sourceLinked: refusal != "unlinked");
        var now = DateTimeOffset.UtcNow;
        var outbox = new SqlOutboxStore(scenario.Factory, sourceAdmission: scenario.Admission);
        var dispatch = Assert.IsType<ClaimedDispatchMessage>(await outbox.ClaimNextDueAsync("deferral-proof", now,
            TimeSpan.FromMinutes(1), [PipelineOperations.Embed], default));
        var job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(scenario.Factory, sourceAdmission: scenario.Admission)
            .ClaimForDispatchAsync(dispatch, "deferral-proof", now, TimeSpan.FromMinutes(1), default));
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var stored = await db.Jobs.SingleAsync(x => x.Id == jobId);
            if (refusal == "generation") stored.LeaseGeneration++;
            if (refusal == "gpu-queued") stored.PublicState = (int)PublicJobState.GpuQueued;
            if (refusal == "expired") stored.LeaseExpiresAtUtc = now.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlStageTransitionStore(scenario.Factory,
            sourceAdmission: scenario.Admission).RetryAsync(new(dispatch, job, now.AddSeconds(5),
            "source-work-deferred", "test"), default).AsTask());
        await using var verify = scenario.Factory.CreateDbContext();
        Assert.Equal("deferral-proof", (await verify.OutboxMessages.SingleAsync(x => x.JobId == jobId)).LeaseOwner);
        Assert.Equal("deferral-proof", (await verify.Jobs.SingleAsync(x => x.Id == jobId)).LeaseOwner);
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
    }

    private static async Task<Guid> QueueSourceDispatchAsync(Scenario scenario, bool sourceLinked = true)
    {
        await using var db = scenario.Factory.CreateDbContext();
        var inputId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"))
            .Select(x => x.Id).SingleAsync();
        var jobId = await scenario.QueueAsync(inputId, sourceLinked);
        var job = await db.Jobs.SingleAsync(x => x.Id == jobId);
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = Guid.NewGuid(), JobId = jobId, PipelineRecordId = job.PipelineRecordId, SourceRevision = 1,
            Stage = job.Stage, Operation = job.Operation, DispatchGeneration = 1,
            IdempotencyKey = "source-deferral:" + jobId.ToString("N"), DueAtUtc = job.DueAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return jobId;
    }

    private sealed class SourceAdmissionDeferringWorker(SourceWorkAdmission admission, Guid rootId, long revision) : IStageWorker
    {
        public string Operation => PipelineOperations.Embed;
        internal StageWorkItem? LastWork { get; private set; }
        public ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
        {
            LastWork = workItem;
            admission.MarkPrepared(rootId, revision);
            throw new SourceWorkDeferredException();
        }
    }
}
