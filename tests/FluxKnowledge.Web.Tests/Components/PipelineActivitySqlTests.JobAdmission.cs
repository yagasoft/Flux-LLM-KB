using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_job_fact_materialisation_preserves_all_rows_across_rebuild_holds_and_concurrent_transitions(bool active)
    {
        // Each initial rebuild state gets a fresh owned database and SQL plan
        // cache identity, independently of the other case or regression fixture.
        var owned = new NativeSqlServerFixture();
        await owned.InitializeAsync();
        try { await new PipelineActivitySqlTests(owned, output).AssertSnapshotJobFactMaterialisationAsync(active); }
        finally { await owned.DisposeAsync(); }
    }

    private async Task AssertSnapshotJobFactMaterialisationAsync(bool active)
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var operation = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        Guid? previous;
        Guid validJob = default;
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            previous = (await setup.IndexState.SingleAsync()).CorpusRebuildOperationId;
            setup.CorpusRebuildOperations.AddRange(new[] { operation, replacement }.Select(id =>
                new CorpusRebuildOperationEntity { Id = id, TargetEpoch = Guid.NewGuid(), ManifestHash = new string('a', 64),
                    ManifestJson = "{}", CreatedAtUtc = now }));
            await setup.SaveChangesAsync();
        }
        try
        {
            foreach (var kind in new[] { "valid", "paused", "suppressed", "mismatch", "superseded" })
            {
                var root = await AddActivityRootAsync(now);
                var recordId = await SeedActivityJobAsync(root, now, PublicJobState.GpuQueued, null, suppressed: kind == "suppressed");
                await using var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
                if (kind == "paused") (await setup.SourceRootConfigurations.SingleAsync(r => r.Id == root)).State = (int)SourceRootState.Paused;
                var record = await setup.PipelineRecords.SingleAsync(r => r.Id == recordId);
                record.CompletionCriteriaMet = true;
                record.CurrentStage = (int)PipelineStage.Publish;
                var embed = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == recordId);
                embed.Stage = (int)PipelineStage.Embed; embed.Operation = PipelineOperations.Embed;
                if (kind == "valid") validJob = embed.Id;
                var parent = await setup.OutboxMessages.SingleAsync(o => o.JobId == embed.Id);
                parent.Stage = (int)PipelineStage.Embed; parent.Operation = PipelineOperations.Embed;
                parent.DispatchedAtUtc = now; parent.CompletedArtifactId = Guid.NewGuid();
                var publish = new JobEntity { Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = 1,
                    Stage = (int)PipelineStage.Publish, Operation = PipelineOperations.Publish,
                    PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now };
                setup.Jobs.Add(publish);
                setup.OutboxMessages.Add(new() { Id = Guid.NewGuid(), JobId = publish.Id, PipelineRecordId = recordId, SourceRevision = 1,
                    Stage = (int)PipelineStage.Publish, Operation = PipelineOperations.Publish, DispatchGeneration = 1,
                    IdempotencyKey = $"admission-{publish.Id:N}", DueAtUtc = now, CreatedAtUtc = now });
                setup.CorpusRebuildWorkItems.Add(new() { OperationId = operation, PipelineRecordId = recordId,
                    SourceRevision = kind == "mismatch" ? 2 : 1, CanonicalArtifactId = Guid.NewGuid(), EmbeddingJobId = embed.Id,
                    DispatchMessageId = parent.Id, State = 1, PreparedAtUtc = now });
                if (kind == "superseded") setup.CorpusRebuildSupersededJobs.Add(new() { JobId = embed.Id,
                    OperationId = operation, ReplacementOperationId = replacement, SupersededAtUtc = now });
                await setup.SaveChangesAsync();
            }
            foreach (var admission in new DeploymentHoldAdmission[] { new(false, null), new(true, null),
                new(true, operation), new(true, replacement) })
            {
                await SetOperation(active ? operation : null);
                await using (var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
                await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot))
                await using (var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default))
                {
                    var evidence = await SqlGpuActivityReadEvidence.ReadAsync(context, default);
                    var original = await SqlPipelineActivityQuery.Jobs(context, now, admission, scope, evidence).OrderBy(j => j.Id).ToArrayAsync();
                    // Every canonical admission proof must retain this snapshot,
                    // even when the current rebuild changes before population.
                    await SetOperation(active ? null : operation);
                    await scope.MaterializeGpuFactsAsync(now, evidence, default);
                    await scope.MaterializeJobFactsAsync(now, admission, evidence, default);
                    var captured = await SqlPipelineActivityQuery.Jobs(context, now, admission, scope, evidence).OrderBy(j => j.Id).ToArrayAsync();
                    Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(captured));
                    // An active rebuild requires its exact permitted operation,
                    // even when the deployment hold itself is absent.
                    Assert.Equal(active ? admission.PermittedCorpusRebuildOperationId == operation : !admission.IsHeld,
                        Assert.Single(captured, j => j.Id == validJob).SourceAdmitted);
                    await scope.CompleteAsync(default);
                    await transaction.CommitAsync();
                }
                await using var fresh = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
                await using var next = await fresh.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
                await using var nextScope = await SqlSelectedBranchReadScope.CreateAsync(fresh, [], default);
                var nextEvidence = await SqlGpuActivityReadEvidence.ReadAsync(fresh, default);
                var expected = await SqlPipelineActivityQuery.Jobs(fresh, now, admission, nextScope, nextEvidence).OrderBy(j => j.Id).ToArrayAsync();
                await nextScope.MaterializeGpuFactsAsync(now, nextEvidence, default);
                await nextScope.MaterializeJobFactsAsync(now, admission, nextEvidence, default);
                var observed = await SqlPipelineActivityQuery.Jobs(fresh, now, admission, nextScope, nextEvidence).OrderBy(j => j.Id).ToArrayAsync();
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(observed));
                Assert.Equal(!active ? admission.PermittedCorpusRebuildOperationId == operation : !admission.IsHeld,
                    Assert.Single(observed, j => j.Id == validJob).SourceAdmitted);
                await nextScope.CompleteAsync(default);
                await next.CommitAsync();
                // Reuse the same materialisation batch after the reverse
                // transition too, preserving every canonical row.
                await SetOperation(active ? operation : null);
                await using var returned = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
                await using var returnedTransaction = await returned.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
                await using var returnedScope = await SqlSelectedBranchReadScope.CreateAsync(returned, [], default);
                var returnedEvidence = await SqlGpuActivityReadEvidence.ReadAsync(returned, default);
                var returnedExpected = await SqlPipelineActivityQuery.Jobs(returned, now, admission, returnedScope, returnedEvidence).OrderBy(j => j.Id).ToArrayAsync();
                await returnedScope.MaterializeGpuFactsAsync(now, returnedEvidence, default);
                await returnedScope.MaterializeJobFactsAsync(now, admission, returnedEvidence, default);
                var returnedObserved = await SqlPipelineActivityQuery.Jobs(returned, now, admission, returnedScope, returnedEvidence).OrderBy(j => j.Id).ToArrayAsync();
                Assert.Equal(JsonSerializer.Serialize(returnedExpected), JsonSerializer.Serialize(returnedObserved));
                await returnedScope.CompleteAsync(default);
                await returnedTransaction.CommitAsync();
            }
        }
        finally { await SetOperation(previous); }

        async Task SetOperation(Guid? value)
        {
            await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            await writer.IndexState.ExecuteUpdateAsync(update => update.SetProperty(s => s.CorpusRebuildOperationId, value));
        }
    }
}
