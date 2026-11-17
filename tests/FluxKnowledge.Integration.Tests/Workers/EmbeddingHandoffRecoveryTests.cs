using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class EmbeddingHandoffRecoveryTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Real_pre_handoff_lock_timeout_preserves_delivery_and_reclaims_the_original_unstarted_request()
    {
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A durable request must survive scheduler contention.", embed: false, clock: clock);
        var runtime = new EmbeddingGpuRuntime(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint,
            new(BgeOfflineModels.EmbeddingFingerprint, 1024), 1024);
        var admission = new FixtureSourceAdmission(environment.Factory);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, timeProvider: clock, sourceAdmission: admission);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), runtime, clock, sourceAdmission: admission);
        var worker = new HandoffWorker(new SqlEmbeddingCheckpointStore(environment.Factory, clock, runtime, admission), requests);
        var registrations = new ServiceCollection();
        registrations.AddSingleton(environment.Factory); registrations.AddSingleton<TimeProvider>(clock);
        registrations.AddSingleton<ISourceWorkAdmission>(admission); registrations.AddSingleton(runtime);
        registrations.AddFluxKnowledgeOutboxWorkers(); registrations.RemoveAll<IStageWorker>(); registrations.AddSingleton<IStageWorker>(worker);
        await using var services = registrations.BuildServiceProvider();
        var pump = services.GetRequiredService<OutboxPumpService>();
        await using var blocker = new SqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using (var hold = blocker.CreateCommand())
        {
            hold.CommandText = "DECLARE @r int; EXEC @r=sp_getapplock @Resource=N'FluxKnowledge.GpuScheduler.Mutation',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0; SELECT @r;";
            Assert.InRange(Convert.ToInt32(await hold.ExecuteScalarAsync()), 0, 1);
        }
        var failure = await Record.ExceptionAsync(() => pump.PumpOnceAsync(default).AsTask());
        await using var before = environment.Factory.CreateDbContext();
        var errors = await before.Jobs.Where(x => x.PublicState == (int)PublicJobState.Failed).Select(x => x.ErrorDetails).ToArrayAsync();
        Assert.True(failure is SqlEmbeddingHandoffDeferredException,
            $"Expected the typed pre-mutation handoff timeout, got {failure?.GetType().Name ?? "no exception"}; terminal details: {string.Join(" | ", errors)}");
        var pending = await before.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        var job = await before.Jobs.AsNoTracking().SingleAsync(x => x.Id == pending.ParentJobId);
        var outbox = await before.OutboxMessages.AsNoTracking().SingleAsync(x => x.JobId == job.Id);
        Assert.Equal((int)PublicJobState.WorkerProcessing, job.PublicState);
        Assert.Equal(0, pending.State); Assert.Null(pending.ExecutorInstanceId); Assert.Null(pending.DispatchId);
        Assert.Null(outbox.DispatchedAtUtc); Assert.NotNull(outbox.LeaseOwner);
        Assert.Empty(await before.GpuMiniTasks.ToArrayAsync());
        await using (var release = blocker.CreateCommand())
        {
            release.CommandText = "EXEC sp_releaseapplock @Resource=N'FluxKnowledge.GpuScheduler.Mutation',@LockOwner=N'Session';";
            await release.ExecuteNonQueryAsync();
        }
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal(1, await pump.PumpOnceAsync(default));
        await requests.QueueAsync(worker.LastWork!, worker.LastBatch!, default);
        await using var verify = environment.Factory.CreateDbContext();
        Assert.Single(await verify.EmbeddingGpuRequests.ToArrayAsync());
        Assert.Equal(pending.MiniTaskId, (await verify.GpuMiniTasks.SingleAsync()).Id);
        var resumed = await verify.Jobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal((int)PublicJobState.GpuQueued, resumed.PublicState);
        Assert.Equal(2, resumed.AttemptCount);
        Assert.Equal(outbox.Id, (await verify.OutboxMessages.SingleAsync(x => x.JobId == job.Id)).Id);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class HandoffWorker(SqlEmbeddingCheckpointStore checkpoint, SqlEmbeddingGpuRequestStore requests) : IStageWorker
    {
        public string Operation => PipelineOperations.Embed;
        public StageWorkItem? LastWork { get; private set; }
        public EmbeddingWorkBatch? LastBatch { get; private set; }
        public async ValueTask ExecuteAsync(StageWorkItem work, CancellationToken ct)
        {
            LastWork = work;
            LastBatch = await checkpoint.ReadNextAsync(work, requests.Profile, ct);
            await requests.QueueAsync(work, LastBatch, ct);
        }
    }
}
