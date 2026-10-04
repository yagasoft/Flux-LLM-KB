using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class CorpusRebuildCommitTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Empty_worklist_schema_can_be_downgraded_and_reapplied()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync("20260927115909_BindCompletedDeliveryArtifacts");
        Assert.DoesNotContain("20260927121634_AddCorpusRebuildWorklist", await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
        Assert.Empty(await context.CorpusRebuildWorkItems.ToArrayAsync());
        Assert.Null((await context.IndexState.SingleAsync()).CorpusRebuildOperationId);
    }

    [NativeSqlServerFact]
    public async Task Committed_rebuild_refuses_schema_downgrade_without_losing_its_receipt_or_worklist()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Receipt survives downgrade refusal.");
        await AddSlotAsync(environment.Factory);
        var store = new SqlCorpusRebuildStore(environment.Factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        await using var context = environment.Factory.CreateDbContext();
        var error = await Assert.ThrowsAsync<SqlException>(() =>
            context.GetService<IMigrator>().MigrateAsync("20260927115909_BindCompletedDeliveryArtifacts"));
        Assert.Contains("corpus-rebuild-downgrade-requires-empty-receipts", error.Message);
        Assert.Contains("20260927121634_AddCorpusRebuildWorklist", await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(plan.ManifestHash, (await context.CorpusRebuildOperations.SingleAsync()).ManifestHash);
        Assert.Equal(plan.OperationId, (await context.CorpusRebuildWorkItems.SingleAsync()).OperationId);
        Assert.Equal(plan.TargetEpoch, (await context.IndexState.SingleAsync()).CorpusEpoch);
        Assert.True((await store.CommitAsync(plan, "slot-a", CancellationToken.None)).AlreadyCommitted);
    }

    [NativeSqlServerFact]
    public async Task Reset_commits_a_new_epoch_and_worklist_preserving_canonical_and_completion_history_and_replay_is_safe()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Rebuild preserves this canonical input.");
        await AddSlotAsync(environment.Factory);
        var store = new SqlCorpusRebuildStore(environment.Factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await using var before = environment.Factory.CreateDbContext();
        var jobs = await before.Jobs.CountAsync();
        var deliveries = await before.OutboxMessages.CountAsync();
        var canonical = await before.Artifacts.AsNoTracking().SingleAsync(value => value.Stage == (int)PipelineStage.CanonicalIndex);
        var result = await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        Assert.False(result.AlreadyCommitted);
        Assert.True((await store.CommitAsync(plan, "slot-a", CancellationToken.None)).AlreadyCommitted);
        await using var context = environment.Factory.CreateDbContext();
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        Assert.Equal(plan.TargetEpoch, state.CorpusEpoch);
        Assert.Equal(plan.OperationId, state.CorpusRebuildOperationId);
        Assert.Null(state.ActiveIndexGenerationId);
        Assert.Null(state.EmptyCatalogueValidatedAtUtc);
        Assert.Empty(await context.TextChunks.ToArrayAsync());
        Assert.Empty(await context.Vectors.ToArrayAsync());
        Assert.Empty(await context.IndexGenerations.ToArrayAsync());
        Assert.Empty(await context.IndexGenerationVectors.ToArrayAsync());
        Assert.Equal(jobs, await context.Jobs.CountAsync());
        Assert.Equal(deliveries, await context.OutboxMessages.CountAsync());
        Assert.All(await context.Jobs.ToArrayAsync(), job => Assert.Equal((int)PublicJobState.Completed, job.PublicState));
        Assert.Equal(canonical.SearchText, (await context.Artifacts.SingleAsync(value => value.Id == canonical.Id)).SearchText);
        var item = Assert.Single(await context.CorpusRebuildWorkItems.ToArrayAsync());
        Assert.Equal(Assert.Single(plan.Inputs).EmbeddingJobId, item.EmbeddingJobId);
        Assert.Equal(0, item.State);
        Assert.All(plan.Generations.Where(generation => generation.IndexPath.Length > 0), generation => Assert.True(Directory.Exists(generation.IndexPath)));
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var unavailable = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(async () =>
            await reader.GetLexicalReadinessAsync(CancellationToken.None));
        Assert.Equal("rebuilding", unavailable.Status);
        Assert.Equal("rebuilding", (await Assert.ThrowsAsync<PassageRetrievalRefusalException>(async () =>
            await reader.SearchAsync("canonical", new("all", [], null), 10, CancellationToken.None))).Status);
        Assert.Equal("rebuilding", (await Assert.ThrowsAsync<PassageRetrievalRefusalException>(async () =>
            await reader.ReadAsync(new(2, null, null, new string('a', 64), item.PipelineRecordId, item.SourceRevision,
                item.CanonicalArtifactId, canonical.ContentHash, 1, new string('a', 64), 0, 1, plan.PreviousStamp.CorpusEpoch),
                0, CancellationToken.None))).Status);
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System)
            .ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
    }

    [NativeSqlServerFact]
    public async Task Failure_after_projection_clear_rolls_back_receipt_epoch_and_projection_together()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Rollback source.");
        await AddSlotAsync(environment.Factory);
        var store = new SqlCorpusRebuildStore(environment.Factory, afterProjectionReset: _ =>
            ValueTask.FromException(new InvalidOperationException("injected-reset-failure")));
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CommitAsync(plan, "slot-a", CancellationToken.None));
        Assert.Equal("injected-reset-failure", failure.Message);
        await using var context = environment.Factory.CreateDbContext();
        Assert.Equal(plan.PreviousStamp.CorpusEpoch, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch);
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
        Assert.Empty(await context.CorpusRebuildWorkItems.ToArrayAsync());
        Assert.Equal(plan.ChunkCount, await context.TextChunks.LongCountAsync());
        Assert.Equal(plan.VectorCount, await context.Vectors.LongCountAsync());
        Assert.Equal(plan.Generations.Count, await context.IndexGenerations.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Changed_canonical_metadata_after_plan_refuses_reset_without_clearing_any_projection()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Changed identity source.");
        await AddSlotAsync(environment.Factory);
        var store = new SqlCorpusRebuildStore(environment.Factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await using var context = environment.Factory.CreateDbContext();
        await context.Artifacts.Where(value => value.Stage == (int)PipelineStage.CanonicalIndex)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.DocumentMetadataJson, "{}"));
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.CommitAsync(plan, "slot-a", CancellationToken.None));
        Assert.Equal("corpus-rebuild-manifest-changed", error.Message);
        Assert.Equal(plan.ChunkCount, await context.TextChunks.LongCountAsync());
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task A_durable_query_registration_without_a_session_lock_still_blocks_reset()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Native lease retained after session loss.");
        await AddSlotAsync(environment.Factory);
        var store = new SqlCorpusRebuildStore(environment.Factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await using var context = environment.Factory.CreateDbContext();
        context.CorpusQueryLeases.Add(new CorpusQueryLeaseEntity
        {
            Id = Guid.NewGuid(), GenerationId = (await context.IndexState.SingleAsync()).ActiveIndexGenerationId!.Value,
            CorpusEpoch = plan.PreviousStamp.CorpusEpoch, CorpusVersion = plan.PreviousStamp.CorpusVersion,
            ModelFingerprint = "fixture-profile", Dimensions = 256, OwnerInstanceId = Guid.NewGuid(),
            OwnerProcessId = Environment.ProcessId, OwnerStartedAtUtc = DateTimeOffset.UtcNow,
            OwnerMachineFingerprint = new string('a', 64), SqlSessionId = 100, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.CommitAsync(plan, "slot-a", CancellationToken.None));
        Assert.Equal("corpus-rebuild-query-drain-required", error.Message);
        Assert.Single(await context.CorpusQueryLeases.ToArrayAsync());
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
        Assert.Equal(plan.VectorCount, await context.Vectors.LongCountAsync());
    }

    [NativeSqlServerFact]
    public async Task Exited_query_registration_is_drained_at_rebuild_admission_without_a_healthy_exclusive_probe()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Exited query owner permits explicit rebuild admission.");
        await AddSlotAsync(environment.Factory);
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var store = new SqlCorpusRebuildStore(environment.Factory, queryOwnerProbe: new QueryOwnerProbe(owner, GpuInteractiveOwnerObservation.Exited));
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await AddQueryRegistrationAsync(environment.Factory, plan, owner);
        await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await context.CorpusQueryLeases.ToArrayAsync());
        Assert.Equal(plan.OperationId, (await context.IndexState.SingleAsync()).CorpusRebuildOperationId);
    }

    [NativeSqlServerTheory]
    [InlineData(GpuInteractiveOwnerObservation.Alive)]
    [InlineData(GpuInteractiveOwnerObservation.Unknown)]
    public async Task Unproven_query_owner_preserves_its_registration_and_projection_at_rebuild_admission(GpuInteractiveOwnerObservation observation)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unproven query owner preserves projection.");
        await AddSlotAsync(environment.Factory);
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var store = new SqlCorpusRebuildStore(environment.Factory, queryOwnerProbe: new QueryOwnerProbe(owner, observation));
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await AddQueryRegistrationAsync(environment.Factory, plan, owner);
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.CommitAsync(plan, "slot-a", CancellationToken.None));
        Assert.Equal("corpus-rebuild-query-drain-required", error.Message);
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await context.CorpusQueryLeases.ToArrayAsync());
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
        Assert.Equal(plan.VectorCount, await context.Vectors.LongCountAsync());
    }

    [NativeSqlServerFact]
    public async Task Failed_rebuild_admission_rolls_back_exited_query_registration_cleanup()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Failed admission retains the prior receipt.");
        await AddSlotAsync(environment.Factory);
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var store = new SqlCorpusRebuildStore(environment.Factory, queryOwnerProbe: new QueryOwnerProbe(owner, GpuInteractiveOwnerObservation.Exited));
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        await AddQueryRegistrationAsync(environment.Factory, plan, owner);
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.Artifacts.Where(value => value.Stage == (int)PipelineStage.CanonicalIndex)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.DocumentMetadataJson, "{}"));
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.CommitAsync(plan, "slot-a", CancellationToken.None));
        Assert.Equal("corpus-rebuild-manifest-changed", error.Message);
        Assert.Single(await context.CorpusQueryLeases.ToArrayAsync());
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
        Assert.Equal(plan.VectorCount, await context.Vectors.LongCountAsync());
    }

    private static async Task AddQueryRegistrationAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        CorpusRebuildPlan plan, GpuInteractiveOwnerIdentity owner)
    {
        await using var context = await factory.CreateDbContextAsync();
        var generationId = (await context.IndexState.SingleAsync()).ActiveIndexGenerationId;
        var generation = await context.IndexGenerations.SingleAsync(value => value.Id == generationId);
        context.CorpusQueryLeases.Add(new CorpusQueryLeaseEntity
        {
            Id = Guid.NewGuid(), GenerationId = generation.Id, CorpusEpoch = plan.PreviousStamp.CorpusEpoch,
            CorpusVersion = plan.PreviousStamp.CorpusVersion, ModelFingerprint = generation.ModelFingerprint,
            Dimensions = generation.Dimensions, OwnerInstanceId = Guid.NewGuid(), OwnerProcessId = owner.ProcessId,
            OwnerStartedAtUtc = owner.StartedAtUtc, OwnerMachineFingerprint = owner.MachineFingerprint,
            SqlSessionId = 100, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private sealed class QueryOwnerProbe(GpuInteractiveOwnerIdentity current, GpuInteractiveOwnerObservation observation) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => current;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner)
        {
            Assert.Equal(current, owner);
            return observation;
        }
    }

    [NativeSqlServerFact]
    public async Task Uncertain_gpu_capacity_refuses_reset_even_with_a_settled_pipeline()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Uncertain slot source.");
        await AddSlotAsync(environment.Factory, GpuCapacitySlotState.Uncertain);
        var store = new SqlCorpusRebuildStore(environment.Factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("next-profile", 1024), new string('b', 64), CancellationToken.None);
        var error = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.CommitAsync(plan, "slot-a", CancellationToken.None));
        Assert.Equal("corpus-rebuild-gpu-drain-required", error.Message);
        await using var context = environment.Factory.CreateDbContext();
        Assert.Equal(plan.ChunkCount, await context.TextChunks.LongCountAsync());
        Assert.Empty(await context.CorpusRebuildOperations.ToArrayAsync());
    }

    private static async Task AddSlotAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, GpuCapacitySlotState state = GpuCapacitySlotState.Available)
    {
        await using var context = factory.CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", State = (int)state, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
    }
}
