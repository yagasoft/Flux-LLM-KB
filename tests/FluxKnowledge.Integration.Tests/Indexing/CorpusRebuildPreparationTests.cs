using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class CorpusRebuildPreparationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Preparation_preserves_canonical_identity_and_queues_one_exact_job_on_replay_and_concurrency()
    {
        const string text = "The policy applies to invoice INV-42.\n\nApproval requires two signatures.";
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, text);
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        var input = Assert.Single(plan.Inputs);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None).AsTask()));
        Assert.Single(results, result => !result.AlreadyPrepared);
        Assert.All(results, result => Assert.Equal(input.EmbeddingJobId, result.EmbeddingJobId));
        var replay = await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        Assert.True(replay.AlreadyPrepared);
        await using var context = environment.Factory.CreateDbContext();
        var canonical = await context.Artifacts.SingleAsync(artifact => artifact.Id == input.CanonicalArtifactId);
        Assert.Equal(text, canonical.SearchText);
        var expected = builder.BuildDocument(text, canonical.DocumentMetadataJson);
        var chunks = await context.TextChunks.OrderBy(chunk => chunk.Ordinal).ToArrayAsync();
        Assert.Equal(expected.Select(chunk => (chunk.Content, chunk.StartOffset, chunk.Length, chunk.SearchInputHash)),
            chunks.Select(chunk => (chunk.Content, chunk.StartOffset, chunk.Length, chunk.SearchInputHash)));
        Assert.All(chunks, chunk => Assert.Equal(input.CanonicalArtifactId, chunk.ArtifactId));
        var job = await context.Jobs.SingleAsync(value => value.Id == input.EmbeddingJobId);
        Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
        Assert.Equal(PipelineOperations.Embed, job.Operation);
        var delivery = await context.OutboxMessages.SingleAsync(value => value.Id == input.DispatchMessageId);
        Assert.Equal(input.EmbeddingJobId, delivery.JobId);
        Assert.True(delivery.DispatchGeneration > await context.OutboxMessages.Where(value => value.Id != delivery.Id).MaxAsync(value => value.DispatchGeneration));
        var record = await context.PipelineRecords.SingleAsync(value => value.Id == input.PipelineRecordId);
        Assert.False(record.CompletionCriteriaMet);
        Assert.Equal((int)PipelineStage.Embed, record.CurrentStage);
        Assert.Equal(1, (await context.CorpusRebuildWorkItems.SingleAsync()).State);
        Assert.Equal(input.EmbeddingJobId, (await new SqlJobClaimStore(environment.Factory, environment.DeploymentHold).ClaimForDispatchAsync(
            (await new SqlOutboxStore(environment.Factory, environment.DeploymentHold).ClaimNextDueAsync("rebuild-dispatch", DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(2), [PipelineOperations.Embed], CancellationToken.None))!,
            "rebuild-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None))!.JobId.Value);
    }

    [NativeSqlServerFact]
    public async Task Changed_metadata_or_policy_refuses_preparation_without_partial_chunks_or_jobs()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Preserved input.");
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        var input = Assert.Single(plan.Inputs);
        Assert.Equal("corpus-rebuild-policy-changed", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.PrepareAsync(plan.OperationId, input.PipelineRecordId,
                new PassageBuilder(new Tokenizer(), new PassagePolicy(TargetTokens: 100)), CancellationToken.None))).Message);
        await using var context = environment.Factory.CreateDbContext();
        await context.Artifacts.Where(artifact => artifact.Id == input.CanonicalArtifactId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.DocumentMetadataJson, "{}"));
        Assert.Equal("corpus-rebuild-input-changed", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None))).Message);
        Assert.Empty(await context.TextChunks.ToArrayAsync());
        Assert.False(await context.Jobs.AnyAsync(value => value.Id == input.EmbeddingJobId));
        Assert.Equal(0, (await context.CorpusRebuildWorkItems.SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Prepared_work_reaches_USearch_publication_without_enabling_a_paused_root()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted INV-42 baseline.");
        var recordId = await environment.AddRetainedAndPumpAsync("Paused policy requires two signatures.");
        await using var context = environment.Factory.CreateDbContext();
        await context.SourceRootConfigurations.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, (int)SourceRootState.Paused));
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        foreach (var input in plan.Inputs)
            await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        await environment.PumpAsync();
        Assert.All(await context.CorpusRebuildWorkItems.AsNoTracking().ToArrayAsync(), item => Assert.Equal(2, item.State));
        Assert.True((await context.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == recordId)).CompletionCriteriaMet);
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        var generation = await environment.Store.GetGenerationAsync(state.ActiveIndexGenerationId!.Value, CancellationToken.None);
        Assert.Equal(plan.TargetEpoch, generation!.CorpusStamp!.CorpusEpoch);
        Assert.Equal(plan.Inputs.Count, (await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None)).Count);
        Assert.Equal((int)SourceRootState.Paused, (await context.SourceRootConfigurations.AsNoTracking().SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Changed_source_after_claim_refuses_embedding_checkpoint_before_creating_a_draft()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Captured content.");
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        var input = Assert.Single(plan.Inputs);
        await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var dispatch = (await new SqlOutboxStore(environment.Factory, environment.DeploymentHold).ClaimNextDueAsync("maintenance", now,
            TimeSpan.FromMinutes(2), [PipelineOperations.Embed], CancellationToken.None))!;
        var job = (await new SqlJobClaimStore(environment.Factory, environment.DeploymentHold).ClaimForDispatchAsync(dispatch, "maintenance", now,
            TimeSpan.FromMinutes(2), CancellationToken.None))!;
        await using var context = environment.Factory.CreateDbContext();
        await context.Artifacts.Where(value => value.Id == input.CanonicalArtifactId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.DocumentMetadataJson, "{}"));
        Assert.Equal("corpus-rebuild-input-changed", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(new(dispatch, job),
                plan.Profile, CancellationToken.None))).Message);
        Assert.Empty(await context.IndexGenerations.ToArrayAsync());
        Assert.Empty(await context.Vectors.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Held_claims_require_a_current_exact_operation_and_recheck_revocation_between_dispatch_and_job()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Held rebuild input.");
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        var input = Assert.Single(plan.Inputs);
        await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        await environment.AddAndPumpAtPathAsync("Ordinary intake waits.", "waiting.txt", pump: false);
        var hold = new MutableHold();
        var outbox = new SqlOutboxStore(environment.Factory, hold);
        var jobs = new SqlJobClaimStore(environment.Factory, hold);
        async Task AssertBlockedAsync()
        {
            Assert.Null(await outbox.ClaimNextDueAsync("held-dispatcher", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
                [PipelineOperations.Embed, PipelineOperations.ExtractUtf8], CancellationToken.None));
            Assert.Null(await jobs.ClaimNextDueAsync("held-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None));
        }
        await AssertBlockedAsync();
        hold.Current = new(false, null); // Missing hold file during an active rebuild is also a missing permit.
        await AssertBlockedAsync();
        hold.Current = new(true, Guid.NewGuid());
        await AssertBlockedAsync();
        hold.Current = new(true, plan.OperationId);
        var dispatch = (await outbox.ClaimNextDueAsync("held-dispatcher", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2),
            [PipelineOperations.Embed, PipelineOperations.ExtractUtf8], CancellationToken.None))!;
        Assert.Equal(input.DispatchMessageId, dispatch.DispatchMessageId.Value);
        hold.Current = new(true, null);
        Assert.Null(await jobs.ClaimForDispatchAsync(dispatch, "held-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None));
        hold.Current = new(true, plan.OperationId);
        var job = (await jobs.ClaimForDispatchAsync(dispatch, "held-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None))!;
        Assert.Equal(input.EmbeddingJobId, job.JobId.Value);
        await using (var context = environment.Factory.CreateDbContext())
            await context.Jobs.Where(value => value.Id == input.EmbeddingJobId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await outbox.ReleaseAsync(dispatch, DateTimeOffset.UtcNow, CancellationToken.None);
        await environment.PumpAsync();
        await FinishWhenPopulatedAsync(store, plan.OperationId);
        await AssertBlockedAsync(); // A remaining hold cannot reopen ordinary intake after finalisation.
        hold.Current = new(false, null);
        Assert.Equal(PipelineOperations.ExtractUtf8, (await outbox.ClaimNextDueAsync("ordinary-dispatcher", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(2), [PipelineOperations.ExtractUtf8], CancellationToken.None))!.Operation);
    }

    private sealed class MutableHold : IDeploymentValidationHold
    {
        public DeploymentHoldAdmission Current { get; set; } = new(true, null);
        public bool IsHeld => Current.IsHeld;
        public DeploymentHoldAdmission ReadAdmissionState() => Current;
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [NativeSqlServerFact]
    public async Task Finish_reopens_search_only_after_complete_work_and_validated_current_native_membership()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Invoice INV-42 needs two signatures.");
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        Assert.Equal("corpus-rebuild-items-incomplete", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.FinishAsync(plan.OperationId, "slot-a", new UsearchGenerationValidator(), CancellationToken.None))).Message);
        foreach (var input in plan.Inputs)
            await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        await environment.PumpAsync();
        Assert.Equal("native-validation-failure", (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.FinishAsync(plan.OperationId, "slot-a", new ThrowingVerifier(), CancellationToken.None))).Message);
        await using var context = environment.Factory.CreateDbContext();
        Assert.Equal(plan.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        Assert.Null((await context.CorpusRebuildOperations.AsNoTracking().SingleAsync()).CompletedAtUtc);
        var receipt = await FinishWhenPopulatedAsync(store, plan.OperationId);
        Assert.False(receipt.AlreadyCommitted);
        Assert.True((await store.FinishAsync(plan.OperationId, "slot-a", new UsearchGenerationValidator(), CancellationToken.None)).AlreadyCommitted);
        Assert.Null((await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        var hit = Assert.Single(await new SqlCorpusRetrievalReader(environment.Factory).SearchAsync("INV-42", new("all", [], null), 10, CancellationToken.None));
        Assert.Contains("two signatures", hit.Content);
    }

    [NativeSqlServerTheory]
    [InlineData("profile")]
    [InlineData("membership")]
    [InlineData("source")]
    public async Task Finish_refuses_changed_model_membership_or_source_and_keeps_search_closed(string fault)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Captured invoice INV-42.");
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        var input = Assert.Single(plan.Inputs);
        await store.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        await environment.PumpAsync();
        await using var context = environment.Factory.CreateDbContext();
        var active = (await context.IndexState.AsNoTracking().SingleAsync()).ActiveIndexGenerationId;
        if (fault == "profile")
            await context.IndexGenerations.Where(value => value.Id == active).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ModelFingerprint, "wrong-profile"));
        else if (fault == "membership") await context.IndexGenerationVectors.ExecuteDeleteAsync();
        else await context.Artifacts.Where(value => value.Id == input.CanonicalArtifactId).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.DocumentMetadataJson, "{}"));
        var failure = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.FinishAsync(plan.OperationId, "slot-a", new UsearchGenerationValidator(), CancellationToken.None));
        Assert.Equal(fault switch { "profile" => "corpus-rebuild-generation-profile-changed", "membership" => "corpus-rebuild-membership-changed", _ => "corpus-rebuild-input-changed" }, failure.Message);
        Assert.Equal(plan.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        Assert.Null((await context.CorpusRebuildOperations.AsNoTracking().SingleAsync()).CompletedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task An_empty_catalogue_finishes_with_verified_empty_state_and_no_native_index()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        await using var context = factory.CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var store = new SqlCorpusRebuildStore(factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("empty-profile", 1024), new PassageBuilder(new Tokenizer()).PolicyFingerprint, CancellationToken.None);
        Assert.Empty(plan.Inputs);
        await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        await FinishWhenPopulatedAsync(store, plan.OperationId, new ThrowingVerifier());
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        Assert.Null(state.CorpusRebuildOperationId);
        Assert.Null(state.ActiveIndexGenerationId);
        Assert.NotNull(state.EmptyCatalogueValidatedAtUtc);
    }

    private static async Task<CorpusRebuildReceipt> FinishWhenPopulatedAsync(SqlCorpusRebuildStore store, Guid operationId,
        IIndexGenerationVerifier? verifier = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { return await store.FinishAsync(operationId, "slot-a", verifier ?? new UsearchGenerationValidator(), timeout.Token); }
            catch (CorpusRebuildRefusalException exception) when (exception.Message == "corpus-rebuild-full-text-not-ready")
            { await Task.Delay(200, timeout.Token); }
        }
    }

    [NativeSqlServerFact]
    public async Task Empty_finish_refuses_a_remaining_zero_vector_generation_without_marking_the_catalogue_validated()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        await using var context = factory.CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var store = new SqlCorpusRebuildStore(factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("empty-profile", 1024), new PassageBuilder(new Tokenizer()).PolicyFingerprint, CancellationToken.None);
        await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        context.IndexGenerations.Add(new IndexGenerationEntity { Id = Guid.NewGuid(), ModelFingerprint = "empty-profile",
            Dimensions = 1024, MetadataChecksum = new string('a', 64), CreatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        Assert.Equal("corpus-rebuild-membership-changed", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.FinishAsync(plan.OperationId, "slot-a", new ThrowingVerifier(), CancellationToken.None))).Message);
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        Assert.Equal(plan.OperationId, state.CorpusRebuildOperationId);
        Assert.Null(state.EmptyCatalogueValidatedAtUtc);
        Assert.Null((await context.CorpusRebuildOperations.AsNoTracking().SingleAsync()).CompletedAtUtc);
    }

    private sealed class ThrowingVerifier : IIndexGenerationVerifier
    {
        public void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors)
            => throw new InvalidOperationException("native-validation-failure");
    }

    [NativeSqlServerFact]
    public async Task Paused_published_input_can_claim_its_exact_rebuild_job_while_ordinary_ingress_waits()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted baseline.");
        var recordId = await environment.AddRetainedAndPumpAsync("Retained paused input.");
        await using var context = environment.Factory.CreateDbContext();
        await context.SourceRootConfigurations.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, (int)SourceRootState.Paused));
        var builder = new PassageBuilder(new Tokenizer());
        var (store, plan) = await ResetAsync(environment, builder);
        var input = plan.Inputs.Single(value => value.PipelineRecordId == recordId);
        await store.PrepareAsync(plan.OperationId, recordId, builder, CancellationToken.None);
        await environment.AddAndPumpAtPathAsync("New ingress must wait during rebuild.", "waiting.txt", pump: false);
        var outbox = new SqlOutboxStore(environment.Factory, environment.DeploymentHold);
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await outbox.ClaimNextDueAsync("ordinary", now, TimeSpan.FromMinutes(2), [PipelineOperations.ExtractUtf8], CancellationToken.None));
        var dispatch = await outbox.ClaimNextDueAsync("maintenance", now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], CancellationToken.None);
        Assert.NotNull(dispatch);
        Assert.Equal(input.DispatchMessageId, dispatch.DispatchMessageId.Value);
        var job = await new SqlJobClaimStore(environment.Factory, environment.DeploymentHold).ClaimForDispatchAsync(dispatch,
            "maintenance", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(input.EmbeddingJobId, job.JobId.Value);
        Assert.Equal((int)SourceRootState.Paused, (await context.SourceRootConfigurations.AsNoTracking().SingleAsync()).State);
        Assert.Equal((int)PublicJobState.WorkerQueued, (await context.Jobs.SingleAsync(value => value.Operation == PipelineOperations.ExtractUtf8 && value.PublicState != (int)PublicJobState.Completed)).PublicState);
    }

    private static async Task<(SqlCorpusRebuildStore, CorpusRebuildPlan)> ResetAsync(
        SqlToUsearchRebuildTests.PipelineEnvironment environment, PassageBuilder builder)
    {
        await using var context = environment.Factory.CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", State = (int)GpuCapacitySlotState.Available, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var store = new SqlCorpusRebuildStore(environment.Factory);
        var profile = await context.IndexGenerations.Where(value => value.IndexPath != "")
            .Select(value => new EmbeddingProfile(value.ModelFingerprint, value.Dimensions)).FirstAsync();
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), profile, builder.PolicyFingerprint, CancellationToken.None);
        await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        environment.PermitRebuild(plan.OperationId);
        return (store, plan);
    }

    private sealed class Tokenizer : IPassageTokenizer
    {
        public string Fingerprint => "rebuild-synthetic-tokenizer-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
