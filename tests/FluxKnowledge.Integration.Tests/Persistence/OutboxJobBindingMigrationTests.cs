using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class OutboxJobBindingMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private const string PreviousMigration = "20260927093601_AddEmbeddingGpuRequests";
    private const string BindingMigration = "20260927115029_BindOutboxMessagesToJobs";

    [NativeSqlServerFact]
    public async Task Upgrade_binds_a_unique_existing_job_and_preserves_the_delivery()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, DateTimeOffset.UtcNow, PublicJobState.WorkerQueued, null);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await context.Database.MigrateAsync();
        var dispatch = await context.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(seeded.DispatchMessageId.Value, dispatch.Id);
        Assert.Equal(seeded.JobId.Value, dispatch.JobId);
        Assert.Null(dispatch.DispatchedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Ambiguous_pending_delivery_rolls_back_upgrade_and_archived_history_can_remain_unbound()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.WorkerQueued, null);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await AddDuplicateJobAsync(context, now);
        var failure = await Assert.ThrowsAsync<SqlException>(() => context.Database.MigrateAsync());
        Assert.Contains("outbox-job-binding-migration-requires-unambiguous-pending-deliveries", failure.Message);
        Assert.DoesNotContain(BindingMigration, await context.Database.GetAppliedMigrationsAsync());
        // Read the prior schema directly: the current model must not be used until migration succeeds.
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE [OutboxMessages] SET [DispatchedAtUtc] = {now}");
        await context.Database.MigrateAsync();
        var dispatch = await context.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Null(dispatch.JobId);
        Assert.Equal(now, dispatch.DispatchedAtUtc);
        Assert.Equal(2, await context.Jobs.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Downgrade_refuses_to_restore_stage_matching_after_repeated_jobs_exist()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.Completed, null);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync(BindingMigration);
        await AddDuplicateJobAsync(context, now);
        var failure = await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Contains("outbox-job-binding-downgrade-refuses-repeated-stage-jobs", failure.Message);
        Assert.Contains(BindingMigration, await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
        Assert.Equal(seeded.JobId.Value, (await context.OutboxMessages.AsNoTracking().SingleAsync()).JobId);
    }

    [NativeSqlServerTheory]
    [InlineData(PipelineOperations.ExtractUtf8)]
    [InlineData(PipelineOperations.ExtractDocument)]
    public async Task Output_upgrade_does_not_infer_the_producer_from_a_repeated_stage(string replacementOperation)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var seeded = await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.Completed, null);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        await context.OutboxMessages.ExecuteUpdateAsync(setters => setters.SetProperty(message => message.DispatchedAtUtc, (DateTimeOffset?)now));
        await context.GetService<IMigrator>().MigrateAsync(BindingMigration);
        var replacementJobId = Guid.NewGuid();
        context.Jobs.Add(new JobEntity
        {
            Id = replacementJobId, PipelineRecordId = seeded.PipelineRecordId.Value, SourceRevision = 1,
            Stage = (int)PipelineStage.Extract, Operation = replacementOperation,
            PublicState = (int)PublicJobState.Completed, DueAtUtc = now
        });
        var artifactId = Guid.NewGuid();
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId, PipelineRecordId = seeded.PipelineRecordId.Value, SourceRevision = 1,
            Stage = (int)PipelineStage.Extract, ContentHash = new string('a', 64),
            ContentType = "text/plain; charset=utf-8", SearchText = "replacement output", CreatedAtUtc = now
        });
        await context.SaveChangesAsync();
        var replacementDispatchId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [OutboxMessages] ([Id], [JobId], [PipelineRecordId], [SourceRevision], [Stage], [Operation],
                [DispatchGeneration], [IdempotencyKey], [DueAtUtc], [CreatedAtUtc], [DispatchedAtUtc], [LeaseGeneration])
            VALUES ({replacementDispatchId}, {replacementJobId}, {seeded.PipelineRecordId.Value}, 1,
                {(int)PipelineStage.Extract}, {replacementOperation}, 1, {"replacement-history"}, {now}, {now}, {now}, 0);
            """);
        await context.Database.MigrateAsync();
        var deliveries = await context.OutboxMessages.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, deliveries.Length);
        Assert.All(deliveries, delivery => Assert.Null(delivery.CompletedArtifactId));
        Assert.Equal(seeded.JobId.Value, deliveries.Single(delivery => delivery.Id == seeded.DispatchMessageId.Value).JobId);
        Assert.Equal(replacementJobId, deliveries.Single(delivery => delivery.Id == replacementDispatchId).JobId);
    }

    [NativeSqlServerTheory]
    [InlineData(PipelineOperations.ExtractUtf8)]
    [InlineData(PipelineOperations.ExtractDocument)]
    public async Task Completed_output_binding_cannot_be_downgraded_after_repeated_jobs_exist(string replacementOperation)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(fixture, now, PublicJobState.Completed, null);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        await AddDuplicateJobAsync(context, now, replacementOperation);
        var failure = await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync(BindingMigration));
        Assert.Contains("completed-delivery-artifact-downgrade-refuses-repeated-stage-jobs", failure.Message);
        Assert.Contains("20260927115909_BindCompletedDeliveryArtifacts", await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
    }

    private static async Task AddDuplicateJobAsync(FluxKnowledgeDbContext context, DateTimeOffset now, string? operation = null)
    {
        var original = await context.Jobs.AsNoTracking().SingleAsync();
        context.Jobs.Add(new JobEntity
        {
            Id = Guid.NewGuid(), PipelineRecordId = original.PipelineRecordId, SourceRevision = original.SourceRevision,
            Stage = original.Stage, Operation = operation ?? original.Operation, PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now
        });
        await context.SaveChangesAsync();
    }
}
