using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class OutboxJobBindingMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private const string PreviousMigration = "20260927093601_AddEmbeddingGpuRequests";
    private const string BindingMigration = "20260927115029_BindOutboxMessagesToJobs";
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    [NativeSqlServerFact]
    public async Task Upgrade_binds_a_unique_existing_job_and_preserves_the_delivery()
    {
        await using var database = await fixture.CreateMigrationDatabaseAsync(PreviousMigration);
        await using var context = database.CreateContext();
        var seeded = await SeedHistoricalWorkAsync(context, PublicJobState.WorkerQueued);
        await InsertUnboundDeliveryAsync(context, seeded);

        await context.Database.MigrateAsync();

        var dispatch = await context.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(seeded.DispatchId, dispatch.Id);
        Assert.Equal(seeded.JobId, dispatch.JobId);
        Assert.Null(dispatch.DispatchedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Ambiguous_pending_delivery_rolls_back_upgrade_and_archived_history_can_remain_unbound()
    {
        await using var database = await fixture.CreateMigrationDatabaseAsync(PreviousMigration);
        await using var context = database.CreateContext();
        var seeded = await SeedHistoricalWorkAsync(context, PublicJobState.WorkerQueued);
        await InsertUnboundDeliveryAsync(context, seeded);
        await InsertJobAsync(context, seeded.RecordId, Guid.NewGuid(), PipelineOperations.ExtractUtf8, PublicJobState.WorkerQueued);
        var originalHistory = (await context.Database.GetAppliedMigrationsAsync()).ToArray();

        var failure = await Assert.ThrowsAsync<SqlException>(() => context.Database.MigrateAsync());

        Assert.Contains("outbox-job-binding-migration-requires-unambiguous-pending-deliveries", failure.Message);
        Assert.Equal(originalHistory, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT CASE WHEN COL_LENGTH('dbo.OutboxMessages', 'JobId') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync());
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [OutboxMessages] SET [DispatchedAtUtc] = {CreatedAt} WHERE [Id] = {seeded.DispatchId};
            """));
        await context.Database.MigrateAsync();
        var dispatch = await context.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(seeded.DispatchId, dispatch.Id);
        Assert.Null(dispatch.JobId);
        Assert.Equal(CreatedAt, dispatch.DispatchedAtUtc);
        Assert.Equal(2, await context.Jobs.CountAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(PipelineOperations.ExtractUtf8)]
    [InlineData(PipelineOperations.ExtractDocument)]
    public async Task Output_upgrade_does_not_infer_the_producer_from_a_repeated_stage(string replacementOperation)
    {
        await using var database = await fixture.CreateMigrationDatabaseAsync(BindingMigration);
        await using var context = database.CreateContext();
        var seeded = await SeedHistoricalWorkAsync(context, PublicJobState.Completed);
        var replacementJobId = Guid.NewGuid();
        await InsertJobAsync(context, seeded.RecordId, replacementJobId, replacementOperation, PublicJobState.Completed);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Artifacts] ([Id], [PipelineRecordId], [SourceRevision], [Stage],
                [ContentHash], [ContentType], [SearchText], [CreatedAtUtc])
            VALUES ({Guid.NewGuid()}, {seeded.RecordId}, 1, {(int)PipelineStage.Extract},
                {new string('a', 64)}, 'text/plain; charset=utf-8', 'replacement output', {CreatedAt});
            """);
        var replacementDispatchId = Guid.NewGuid();
        await InsertBoundDeliveryAsync(context, seeded.DispatchId, seeded.JobId, seeded.RecordId, PipelineOperations.ExtractUtf8);
        await InsertBoundDeliveryAsync(context, replacementDispatchId, replacementJobId, seeded.RecordId, replacementOperation);

        await context.Database.MigrateAsync();

        var deliveries = await context.OutboxMessages.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, deliveries.Length);
        Assert.All(deliveries, delivery => Assert.Null(delivery.CompletedArtifactId));
        Assert.Equal(seeded.JobId, deliveries.Single(delivery => delivery.Id == seeded.DispatchId).JobId);
        Assert.Equal(replacementJobId, deliveries.Single(delivery => delivery.Id == replacementDispatchId).JobId);
    }

    // Historical fixtures use only columns present at their recorded migration head.
    private static async Task<(Guid RecordId, Guid JobId, Guid DispatchId)> SeedHistoricalWorkAsync(
        FluxKnowledgeDbContext context, PublicJobState state)
    {
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [SourceIdentities] ([Id], [SourceKind], [StableKey], [CreatedAtUtc])
            VALUES ({sourceId}, 'local file', {$"C:\\migration-fixture\\{recordId:N}.txt"}, {CreatedAt});
            INSERT INTO [PipelineRecords] ([Id], [SourceIdentityId], [Revision], [ContentHash], [RootLineageRecordId],
                [CurrentStage], [RegisteredAtUtc], [CompletionCriteriaMet], [IsDeleted])
            VALUES ({recordId}, {sourceId}, 1, {new string('a', 64)}, {recordId},
                {(int)PipelineStage.Extract}, {CreatedAt}, 0, 0);
            """);
        await InsertJobAsync(context, recordId, jobId, PipelineOperations.ExtractUtf8, state);
        return (recordId, jobId, Guid.NewGuid());
    }

    private static Task<int> InsertJobAsync(FluxKnowledgeDbContext context, Guid recordId, Guid jobId,
        string operation, PublicJobState state) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Jobs] ([Id], [PipelineRecordId], [SourceRevision], [Stage], [Operation], [PublicState],
                [DueAtUtc], [AttemptCount], [LeaseGeneration])
            VALUES ({jobId}, {recordId}, 1, {(int)PipelineStage.Extract}, {operation}, {(int)state}, {CreatedAt}, 0, 0);
            """);

    private static Task<int> InsertUnboundDeliveryAsync(FluxKnowledgeDbContext context,
        (Guid RecordId, Guid JobId, Guid DispatchId) seeded) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [OutboxMessages] ([Id], [PipelineRecordId], [SourceRevision], [Stage], [Operation],
                [DispatchGeneration], [IdempotencyKey], [DueAtUtc], [CreatedAtUtc], [LeaseGeneration])
            VALUES ({seeded.DispatchId}, {seeded.RecordId}, 1, {(int)PipelineStage.Extract},
                {PipelineOperations.ExtractUtf8}, 0, {seeded.DispatchId.ToString("N")}, {CreatedAt}, {CreatedAt}, 0);
            """);

    private static Task<int> InsertBoundDeliveryAsync(FluxKnowledgeDbContext context, Guid dispatchId, Guid jobId,
        Guid recordId, string operation) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [OutboxMessages] ([Id], [JobId], [PipelineRecordId], [SourceRevision], [Stage], [Operation],
                [DispatchGeneration], [IdempotencyKey], [DueAtUtc], [CreatedAtUtc], [DispatchedAtUtc], [LeaseGeneration])
            VALUES ({dispatchId}, {jobId}, {recordId}, 1, {(int)PipelineStage.Extract}, {operation},
                1, {dispatchId.ToString("N")}, {CreatedAt}, {CreatedAt}, {CreatedAt}, 0);
            """);
}
