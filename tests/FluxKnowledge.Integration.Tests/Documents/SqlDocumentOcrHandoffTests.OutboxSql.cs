using System.Data.Common;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integration.Tests.Workers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData("before-request")]
    [InlineData("request-committed")]
    [InlineData("handoff-committed")]
    public async Task Document_extract_SQL_failure_preserves_exact_request_and_handoff_ownership(string failurePoint)
    {
        var (baseFactory, initialClaim, revision, hash) = await CreateClaimedPdfAsync();
        var clock = new OcrOutboxClock();
        var failure = CpuStageSqlRecoveryTests.SqlFailure(-2);
        var fault = new OcrRequestCommitSqlFailure(failure);
        IDbContextFactory<FluxKnowledgeDbContext> factory = failurePoint == "request-committed"
            ? new OcrOutboxFaultFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(_fixture.ConnectionString).AddInterceptors(fault).Options)
            : baseFactory;
        var worker = new OcrHandoffSqlWorker(CreateStore(factory), revision, hash, failurePoint, failure);
        var registrations = new ServiceCollection();
        registrations.AddSingleton(factory);
        registrations.AddSingleton<TimeProvider>(clock);
        registrations.AddSingleton<ISourceWorkAdmission>(new FixtureSourceAdmission(factory));
        registrations.AddFluxKnowledgeOutboxWorkers();
        registrations.RemoveAll<IStageWorker>();
        registrations.AddSingleton<IStageWorker>(worker);
        await using var services = registrations.BuildServiceProvider();
        var pump = services.GetRequiredService<OutboxPumpService>();

        var observed = await Record.ExceptionAsync(async () => await pump.PumpOnceAsync(default));
        Assert.Equal(1, worker.Calls);
        Assert.IsType<SqlException>(observed);
        var handedOff = failurePoint == "handoff-committed";
        await using (var check = factory.CreateDbContext())
        {
            var parent = await check.Jobs.SingleAsync(value => value.Id == initialClaim.JobId.Value);
            Assert.Equal((int)(handedOff ? PublicJobState.GpuQueued : PublicJobState.WorkerProcessing), parent.PublicState);
            Assert.Equal(handedOff ? 1 : 0, await check.GpuMiniTasks.CountAsync());
            Assert.Equal(failurePoint == "before-request" ? 0 : 1, await check.DocumentOcrRequests.CountAsync());
            Assert.Empty(await check.Artifacts.ToArrayAsync());
        }
        Assert.Equal(0, await pump.PumpOnceAsync(default));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Equal(handedOff ? 0 : 1, await pump.PumpOnceAsync(default));

        await using var verify = factory.CreateDbContext();
        var request = await verify.DocumentOcrRequests.SingleAsync();
        var task = await verify.GpuMiniTasks.SingleAsync();
        Assert.Equal(request.MiniTaskId, task.Id);
        Assert.Equal(initialClaim.JobId.Value, request.ParentJobId);
        Assert.Equal(revision.Value, request.RetainedSourceRevisionId);
        Assert.Equal(hash, request.ContentSha256);
        Assert.Equal((int)PublicJobState.GpuQueued, (await verify.Jobs.SingleAsync()).PublicState);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, task.ExecutionState);
        Assert.Empty(await verify.Artifacts.ToArrayAsync());
        Assert.Empty(await verify.GpuExecutorDispatches.ToArrayAsync());
        Assert.False(await verify.AuditEvents.AnyAsync(value => value.EventType == "pipeline.failed"));
        Assert.Equal(handedOff ? 1 : 2, worker.Calls);
        if (failurePoint == "request-committed") Assert.True(fault.Fired);

        var stale = worker.FirstWork!;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlStageTransitionStore(factory, failureInjector: null, timeProvider: clock, sourceAdmission: new FixtureSourceAdmission(factory))
                .TransitionAsync(new(stale.DispatchMessage, stale.Job,
                    new(Guid.NewGuid(), PipelineStage.Extract, hash, "text/plain", "stale", clock.Now),
                    PipelineStage.Normalise, PipelineOperations.NormaliseText, "stale-ocr-test"), default).AsTask());
        Assert.Contains("lease", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await verify.Artifacts.AsNoTracking().ToArrayAsync());
    }

    private sealed class OcrOutboxClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class OcrHandoffSqlWorker(SqlDocumentOcrStore store, SourceRevisionId revision, string hash,
        string failurePoint, SqlException failure) : IStageWorker
    {
        public string Operation => PipelineOperations.ExtractDocument;
        public int Calls { get; private set; }
        public StageWorkItem? FirstWork { get; private set; }
        public async ValueTask ExecuteAsync(StageWorkItem work, CancellationToken token)
        {
            Calls++;
            FirstWork ??= work;
            if (Calls == 1 && failurePoint == "before-request") throw failure;
            Assert.True((await store.HandoffAsync(new(work.Job, revision, hash, [0]), token)).Scheduled);
            if (Calls == 1 && failurePoint == "handoff-committed") throw failure;
        }
    }

    private sealed class OcrOutboxFaultFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class OcrRequestCommitSqlFailure(SqlException failure) : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context is FluxKnowledgeDbContext context &&
                context.ChangeTracker.Entries<DocumentOcrRequestEntity>().Any())
            { Fired = true; throw failure; }
            return Task.CompletedTask;
        }
    }
}
