using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Confirmed_removal_retires_queued_work_but_keeps_an_unconfirmed_worker_claim()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var path = Path.Combine(scenario.Files, "removed", "one.txt");
        await using var db = scenario.Factory.CreateDbContext();
        var sourceId = await db.SourceRevisions.Where(x => x.CanonicalPath == path).Select(x => x.Id).SingleAsync();
        var processingId = await scenario.QueueAsync(sourceId);
        var processing = await db.Jobs.SingleAsync(job => job.Id == processingId);
        var queuedId = Guid.NewGuid();
        db.Jobs.Add(new JobEntity
        {
            Id = queuedId, PipelineRecordId = processing.PipelineRecordId, SourceRevision = processing.SourceRevision,
            Stage = processing.Stage, Operation = processing.Operation, PublicState = (int)PublicJobState.WorkerQueued,
            DueAtUtc = processing.DueAtUtc
        });
        var delivery = new OutboxMessageEntity
        {
            Id = Guid.NewGuid(), JobId = queuedId, PipelineRecordId = processing.PipelineRecordId,
            SourceRevision = processing.SourceRevision, Stage = processing.Stage, Operation = processing.Operation,
            IdempotencyKey = $"removed-source-delivery:{queuedId:N}", DueAtUtc = processing.DueAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.OutboxMessages.Add(delivery);
        await db.SaveChangesAsync();
        await db.Jobs.Where(job => job.Id == processingId).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.PublicState, (int)PublicJobState.WorkerProcessing)
            .SetProperty(job => job.LeaseOwner, "expired-worker")
            .SetProperty(job => job.LeaseGeneration, 1)
            .SetProperty(job => job.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var reconciliation = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader());
        var events = new RetirementEvents(async cancellationToken =>
        {
            await using var committed = scenario.Factory.CreateDbContext();
            return await committed.Jobs.AnyAsync(job => job.Id == queuedId && job.Reason == "source-removed", cancellationToken);
        });
        using var services = new ServiceCollection().AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(scenario.Factory)
            .AddSingleton(TimeProvider.System).AddSingleton<IStatusEventPublisher>(events)
            .AddScoped<SqlSourceWorkRetirement>().BuildServiceProvider();
        using var scope = services.CreateScope();
        var retirement = scope.ServiceProvider.GetRequiredService<SqlSourceWorkRetirement>();
        File.Delete(path);
        await scenario.PendingAsync(Path.Combine("removed", "one.txt"), false);

        Assert.Equal(0, await retirement.RetireAsync(default));
        Assert.Equal(1, await reconciliation.ReconcileAsync(default));
        Assert.Equal(1, await retirement.RetireAsync(default));
        Assert.Equal(0, await retirement.RetireAsync(default));
        var changed = Assert.Single(events.Events);
        Assert.Equal("sources", changed.Event.Projection);
        Assert.True(changed.SawCommittedRetirement);

        var jobs = await db.Jobs.AsNoTracking().Where(job => job.Id == processingId || job.Id == queuedId)
            .ToDictionaryAsync(job => job.Id);
        Assert.Equal((int)PublicJobState.WorkerProcessing, jobs[processingId].PublicState);
        Assert.Equal("expired-worker", jobs[processingId].LeaseOwner);
        Assert.Equal((int)PublicJobState.Failed, jobs[queuedId].PublicState);
        Assert.Equal("source-removed", jobs[queuedId].Reason);
        var retainedDelivery = await db.OutboxMessages.AsNoTracking().SingleAsync(row => row.Id == delivery.Id);
        Assert.Null(retainedDelivery.DispatchedAtUtc);
        Assert.Null(retainedDelivery.LeaseOwner);
        var outbox = new SqlOutboxStore(scenario.Factory, sourceAdmission: scenario.Admission);
        Assert.Null(await outbox.ClaimNextDueAsync("after-retirement", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), [PipelineOperations.Embed], default));
    }

    [NativeSqlServerFact]
    public async Task Hosted_recovery_retires_an_existing_removed_backlog_across_multiple_polls()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var path = Path.Combine(scenario.Files, "removed", "one.txt");
        var now = DateTimeOffset.UtcNow;
        var beforeRestart = now.AddHours(-2);
        await using var db = scenario.Factory.CreateDbContext();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == path);
        var excluded = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        var queuedId = await scenario.QueueAsync(source.Id);
        var excludedId = await scenario.QueueAsync(excluded.Id);
        var queued = await db.Jobs.SingleAsync(job => job.Id == queuedId);
        queued.PublicState = (int)PublicJobState.GpuQueued;
        excluded.SuppressedAtUtc = beforeRestart;
        excluded.RetentionEvidenceJson = JsonSerializer.Serialize(new
        {
            Version = 1, SourceRootId = excluded.SourceRootId, SourceRevisionId = excluded.Id,
            Category = "OutOfScope", SuppressedAtUtc = beforeRestart
        });
        (await db.Jobs.SingleAsync(job => job.Id == excludedId)).DueAtUtc = beforeRestart.AddMinutes(-1);
        var deferredGpu = new GpuMiniTaskEntity
        {
            Id = Guid.NewGuid(), ParentJobId = queuedId, SourceRevision = queued.SourceRevision,
            ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic",
            IdempotencyKey = $"removed-source-deferred:{queuedId:N}", EstimatedBytes = 1,
            ReservationAttemptCount = 1, ExecutionState = (int)GpuMiniTaskExecutionState.Ready, CreatedAtUtc = beforeRestart
        };
        db.GpuMiniTasks.Add(deferredGpu);
        db.Jobs.AddRange(Enumerable.Range(0, 128).Select(_ => new JobEntity
        {
            Id = Guid.NewGuid(), PipelineRecordId = queued.PipelineRecordId, SourceRevision = queued.SourceRevision,
            Stage = queued.Stage, Operation = queued.Operation, PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = beforeRestart
        }));
        await db.SaveChangesAsync();
        File.Delete(path);
        await scenario.PendingAsync(Path.Combine("removed", "one.txt"), false);
        var observations = new WindowsSourceObservationReader();
        Assert.Equal(1, await new SqlSourceRemovalReconciler(scenario.Factory, new RemovalTime(beforeRestart), observations)
            .ReconcileAsync(default));
        var existingAudit = await db.AuditEvents.CountAsync(x => x.SourceRevisionId == source.Id && x.EventType == "source.removed");

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(scenario.Factory);
        services.AddSingleton(scenario.Admission);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISourceObservationReader>(observations);
        services.AddSingleton<ISourceRemovalReconciler, SqlSourceRemovalReconciler>();
        services.AddScoped<ISourceWorkRetirement, SqlSourceWorkRetirement>();
        services.AddScoped<ISourceWorkRecovery, SqlSourceWorkRecovery>();
        await using var provider = services.BuildServiceProvider();
        using var recovery = new SourceWorkRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SourceWorkRecoveryService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await recovery.StartAsync(deadline.Token);
        try
        {
            while (await db.Jobs.CountAsync(job => job.PipelineRecordId == queued.PipelineRecordId &&
                       job.PublicState == (int)PublicJobState.Failed && job.Reason == "source-removed", deadline.Token) < 129)
                await Task.Delay(50, deadline.Token);
        }
        finally
        {
            await recovery.StopAsync(default);
        }

        var cancelled = await db.GpuMiniTasks.AsNoTracking().SingleAsync(x => x.Id == deferredGpu.Id);
        Assert.Equal((int)GpuMiniTaskExecutionState.Cancelled, cancelled.ExecutionState);
        Assert.Equal(1, cancelled.ReservationAttemptCount);
        Assert.Null(cancelled.BatchId);
        Assert.Equal(existingAudit, await db.AuditEvents.CountAsync(x => x.SourceRevisionId == source.Id && x.EventType == "source.removed"));
        Assert.Equal(beforeRestart, await db.SourceRevisions.Where(x => x.Id == source.Id).Select(x => x.SuppressedAtUtc).SingleAsync());
    }

    private sealed class RetirementEvents(Func<CancellationToken, Task<bool>> hasCommitted) : IStatusEventPublisher
    {
        public List<(StatusChanged Event, bool SawCommittedRetirement)> Events { get; } = [];

        public async ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) =>
            Events.Add((statusChanged, await hasCommitted(cancellationToken)));
    }

    private sealed class RemovalTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
