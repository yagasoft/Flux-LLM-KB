using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Integration.Tests.Support;

internal static class SqlTestData
{
    public static IDbContextFactory<FluxKnowledgeDbContext> CreateFactory(
        NativeSqlServerFixture fixture) =>
        new TestDbContextFactory(fixture.ConnectionString);

    public static async Task ClearPipelineAsync(NativeSqlServerFixture fixture)
    {
        await using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        await context.SourceActivities.ExecuteDeleteAsync();
        await context.AuditEvents.ExecuteDeleteAsync();
        await context.GpuSchedulerOperationReceipts.ExecuteDeleteAsync();
        await context.GpuExecutorEvidence.ExecuteDeleteAsync();
        await context.GpuExecutorResultReceipts.ExecuteDeleteAsync();
        await context.GpuExecutorDispatches.ExecuteDeleteAsync();
        await context.GpuMiniTasks.ExecuteDeleteAsync();
        await context.GpuCapacitySlots
            .Where(slot => slot.ActiveBatchId != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(slot => slot.ActiveBatchId, (Guid?)null));
        await context.GpuBatches.ExecuteDeleteAsync();
        await context.GpuCapacitySlots.ExecuteDeleteAsync();
        var scheduler = await context.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
        scheduler.WakeGeneration = 0;
        scheduler.PendingWakeReasons = 0;
        scheduler.NextDeferredAtUtc = null;
        scheduler.InFlightWakeOperationId = null;
        scheduler.InFlightWakeGeneration = null;
        scheduler.InFlightWakeReasons = 0;
        scheduler.InFlightNextDeferredAtUtc = null;
        scheduler.InFlightEffectiveAdmissionReasons = null;
        scheduler.UpdatedAtUtc = DateTimeOffset.UnixEpoch;
        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
        state.ActiveIndexGenerationId = null;
        await context.SaveChangesAsync();
        await context.IndexGenerationVectors.ExecuteDeleteAsync();
        await context.Vectors.ExecuteDeleteAsync();
        await context.IndexGenerations.ExecuteDeleteAsync();
        await context.Artifacts.ExecuteDeleteAsync();
        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.JobAttempts.ExecuteDeleteAsync();
        await context.Jobs.ExecuteDeleteAsync();
        await context.PipelineRecords.ExecuteDeleteAsync();
        await context.SourceIdentities.ExecuteDeleteAsync();
        await context.SaveChangesAsync();
    }

    public static async Task ClearPhase3SourceDataAsync(NativeSqlServerFixture fixture)
    {
        await using (var context = new FluxKnowledgeDbContext(
                         new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                             .UseSqlServer(fixture.ConnectionString)
                             .Options))
        {
            await context.SourceScanOutbox.ExecuteDeleteAsync();
            await context.SourceScanJobs.ExecuteDeleteAsync();
            await context.SourceScanRequests.ExecuteDeleteAsync();
        }

        await ClearPipelineAsync(fixture);

        await using var sourceContext = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        await sourceContext.SourceArtifacts.ExecuteDeleteAsync();
        while (await sourceContext.SourceRevisions
                   .Where(candidate => !sourceContext.SourceRevisions
                       .Any(child => child.ParentSourceRevisionId == candidate.Id))
                   .ExecuteDeleteAsync() > 0)
        {
        }

        await sourceContext.SourceRootConfigurations.ExecuteDeleteAsync();
    }

    public static async Task<SeededWorkItem> SeedWorkItemAsync(
        NativeSqlServerFixture fixture,
        DateTimeOffset now,
        PublicJobState state,
        DateTimeOffset? leaseExpiresAtUtc,
        long leaseGeneration = 0,
        int attemptCount = 0,
        PipelineStage stage = PipelineStage.Extract,
        string operation = PipelineOperations.ExtractUtf8,
        string? stablePath = null,
        string? contentHash = null)
    {
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        var hash = contentHash ?? new string('a', 64);
        await using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        context.SourceIdentities.Add(
            new SourceIdentityEntity
            {
                Id = sourceId,
                SourceKind = "local file",
                StableKey = stablePath ?? $"C:\\ingress\\{recordId:N}.txt",
                CreatedAtUtc = now
            });
        context.PipelineRecords.Add(
            new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = sourceId,
                Revision = 1,
                ContentHash = hash,
                RootLineageRecordId = recordId,
                CurrentStage = (int)stage,
                RegisteredAtUtc = now
            });
        context.Jobs.Add(
            new JobEntity
            {
                Id = jobId,
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = (int)stage,
                Operation = operation,
                PublicState = (int)state,
                DueAtUtc = now,
                AttemptCount = attemptCount,
                LeaseOwner = state == PublicJobState.WorkerProcessing ? "expired-worker" : null,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                LeaseGeneration = leaseGeneration
            });
        context.OutboxMessages.Add(
            new OutboxMessageEntity
            {
                Id = dispatchId,
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = (int)stage,
                Operation = operation,
                DispatchGeneration = 0,
                IdempotencyKey = $"{recordId:N}:1:{stage}:0",
                DueAtUtc = now,
                CreatedAtUtc = now
            });
        await context.SaveChangesAsync();
        return new SeededWorkItem(
            new PipelineRecordId(recordId),
            new JobId(jobId),
            new DispatchMessageId(dispatchId));
    }

    private sealed class TestDbContextFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }
}

internal sealed record SeededWorkItem(
    PipelineRecordId PipelineRecordId,
    JobId JobId,
    DispatchMessageId DispatchMessageId);
