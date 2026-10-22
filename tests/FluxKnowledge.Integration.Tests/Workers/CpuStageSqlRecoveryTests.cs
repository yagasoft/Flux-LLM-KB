using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class CpuStageSqlRecoveryTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(PipelineStage.Normalise, "before-write")]
    [InlineData(PipelineStage.Normalise, "rolled-back")]
    [InlineData(PipelineStage.CanonicalIndex, "before-write")]
    [InlineData(PipelineStage.CanonicalIndex, "rolled-back")]
    public async Task Transient_cpu_failure_retains_leases_and_reclaims_the_same_delivery(
        PipelineStage stage, string failurePoint)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var clock = new Clock();
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, clock.GetUtcNow(),
            PublicJobState.WorkerQueued, null, stage: stage, operation: Operation(stage));
        var factory = SqlTestData.CreateFactory(fixture);
        var worker = new Worker(factory, clock, stage, failurePoint, SqlFailure(-2));
        await using var services = Services(factory, clock, worker);
        var pump = services.GetRequiredService<OutboxPumpService>();

        await Assert.ThrowsAsync<SqlException>(async () => await pump.PumpOnceAsync(CancellationToken.None));
        await using (var check = factory.CreateDbContext())
        {
            var job = await check.Jobs.SingleAsync(value => value.Id == seeded.JobId.Value);
            var dispatch = await check.OutboxMessages.SingleAsync(value => value.Id == seeded.DispatchMessageId.Value);
            Assert.Equal((int)PublicJobState.WorkerProcessing, job.PublicState);
            Assert.NotNull(job.LeaseOwner);
            Assert.Equal(1, job.LeaseGeneration);
            Assert.Null(dispatch.DispatchedAtUtc);
            Assert.Empty(await check.Artifacts.ToListAsync());
        }
        Assert.Equal(0, await pump.PumpOnceAsync(CancellationToken.None));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Equal(1, await pump.PumpOnceAsync(CancellationToken.None));
        await using var verified = factory.CreateDbContext();
        var completed = await verified.Jobs.SingleAsync(value => value.Id == seeded.JobId.Value);
        var delivered = await verified.OutboxMessages.SingleAsync(value => value.Id == seeded.DispatchMessageId.Value);
        Assert.Equal((int)PublicJobState.Completed, completed.PublicState);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(2, completed.LeaseGeneration);
        Assert.Equal(2, delivered.LeaseGeneration);
        Assert.Equal(0, delivered.DispatchGeneration);
        Assert.NotNull(delivered.CompletedArtifactId);
        Assert.Single(await verified.Artifacts.ToListAsync());
        Assert.False(await verified.AuditEvents.AnyAsync(value => value.EventType == "pipeline.failed"));
        Assert.Equal(2, await verified.Jobs.CountAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(PipelineStage.Normalise)]
    [InlineData(PipelineStage.CanonicalIndex)]
    public async Task Lost_commit_acknowledgement_preserves_the_completed_output(PipelineStage stage)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var clock = new Clock();
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, clock.GetUtcNow(),
            PublicJobState.WorkerQueued, null, stage: stage, operation: Operation(stage));
        var factory = SqlTestData.CreateFactory(fixture);
        var worker = new Worker(factory, clock, stage, "committed", SqlFailure(-2));
        await using var services = Services(factory, clock, worker);
        var pump = services.GetRequiredService<OutboxPumpService>();

        await Assert.ThrowsAsync<SqlException>(async () => await pump.PumpOnceAsync(CancellationToken.None));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Equal(0, await pump.PumpOnceAsync(CancellationToken.None));
        await using var check = factory.CreateDbContext();
        var job = await check.Jobs.SingleAsync(value => value.Id == seeded.JobId.Value);
        var dispatch = await check.OutboxMessages.SingleAsync(value => value.Id == seeded.DispatchMessageId.Value);
        Assert.Equal((int)PublicJobState.Completed, job.PublicState);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(1, job.LeaseGeneration);
        Assert.Equal((await check.Artifacts.SingleAsync()).Id, dispatch.CompletedArtifactId);
        Assert.Equal(2, await check.Jobs.CountAsync());
        Assert.Equal(1, worker.Calls);
    }

    [NativeSqlServerTheory]
    [InlineData(PipelineStage.Normalise, -2, false)]
    [InlineData(PipelineStage.CanonicalIndex, 1205, true)]
    [InlineData(PipelineStage.Extract, -2, false)]
    [InlineData(PipelineStage.Embed, -2, false)]
    public async Task Unsupported_stages_and_unclassified_failures_remain_terminal(
        PipelineStage stage, int number, bool mixed)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var clock = new Clock();
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, clock.GetUtcNow(),
            PublicJobState.WorkerQueued, null, stage: stage, operation: Operation(stage));
        var factory = SqlTestData.CreateFactory(fixture);
        Exception failure = stage == PipelineStage.Normalise
            ? new InvalidOperationException("Execution Timeout Expired.")
            : mixed ? SqlFailure(number, 2601) : SqlFailure(number);
        await using var services = Services(factory, clock, new Worker(factory, clock, stage, "before-write", failure));

        Assert.Equal(1, await services.GetRequiredService<OutboxPumpService>().PumpOnceAsync(CancellationToken.None));
        await using var check = factory.CreateDbContext();
        var job = await check.Jobs.SingleAsync(value => value.Id == seeded.JobId.Value);
        Assert.Equal((int)PublicJobState.Failed, job.PublicState);
        Assert.Null(job.LeaseOwner);
        Assert.NotNull((await check.OutboxMessages.SingleAsync()).DispatchedAtUtc);
        Assert.Empty(await check.Artifacts.ToListAsync());
    }

    private static ServiceProvider Services(IDbContextFactory<FluxKnowledgeDbContext> factory, Clock clock, Worker worker)
    {
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddSingleton<TimeProvider>(clock);
        services.AddFluxKnowledgeOutboxWorkers();
        services.RemoveAll<IStageWorker>();
        services.AddSingleton<IStageWorker>(worker);
        return services.BuildServiceProvider();
    }

    private static string Operation(PipelineStage stage) => stage switch
    {
        PipelineStage.Normalise => PipelineOperations.NormaliseText,
        PipelineStage.CanonicalIndex => PipelineOperations.CanonicalIndex,
        PipelineStage.Embed => PipelineOperations.Embed,
        _ => PipelineOperations.ExtractUtf8
    };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Worker(IDbContextFactory<FluxKnowledgeDbContext> factory, Clock clock,
        PipelineStage stage, string failurePoint, Exception failure) : IStageWorker
    {
        public string Operation => CpuStageSqlRecoveryTests.Operation(stage);
        public int Calls { get; private set; }
        public async ValueTask ExecuteAsync(StageWorkItem work, CancellationToken token)
        {
            Calls++;
            if (Calls == 1 && failurePoint == "before-write") throw failure;
            var store = new SqlStageTransitionStore(factory,
                Calls == 1 && failurePoint == "rolled-back" ? new Fault(failure) : null, clock);
            var text = "retained CPU checkpoint";
            await store.TransitionAsync(new(work.DispatchMessage, work.Job,
                new(Guid.NewGuid(), stage, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                    "text/plain", text, clock.GetUtcNow()),
                stage + 1, stage == PipelineStage.Normalise ? PipelineOperations.CanonicalIndex : PipelineOperations.Embed,
                "test-cpu-worker"), token);
            if (Calls == 1 && failurePoint == "committed") throw failure;
        }
    }

    private sealed class Fault(Exception failure) : IStageTransitionFailureInjector
    {
        public ValueTask AfterArtifactWrittenAsync(CancellationToken token) => ValueTask.FromException(failure);
    }

    private static SqlException SqlFailure(params int[] numbers)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        foreach (var number in numbers)
        {
            var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
                [number, (byte)0, (byte)14, "synthetic", "Synthetic SQL failure.", string.Empty, 1, 0, null], null)!;
            typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        }
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "synthetic"])!;
    }
}
