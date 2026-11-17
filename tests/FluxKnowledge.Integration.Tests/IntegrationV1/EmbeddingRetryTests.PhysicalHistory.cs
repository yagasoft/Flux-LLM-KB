using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed partial class EmbeddingRetryTests
{
    [NativeSqlServerTheory]
    [InlineData("edit")]
    [InlineData("rename")]
    [InlineData("different-root")]
    [InlineData("different-identity")]
    [InlineData("derived")]
    public async Task Physical_revision_history_uses_its_exact_predecessor_without_treating_edits_as_derived(string boundary)
    {
        await using var environment = await SeedAsync();
        var failed = await FailAsync(environment);
        await using (var db = environment.Factory.CreateDbContext())
        {
            var source = await db.SourceRevisions.SingleAsync(x => x.Id == db.PipelineRecords.Where(p => p.Id == failed.Work.Job.PipelineRecordId.Value).Select(p => p.SourceRevisionId).Single());
            var predecessor = (SourceRevisionEntity)db.Entry(source).CurrentValues.ToObject();
            predecessor.Id = Guid.NewGuid(); predecessor.RowVersion = []; predecessor.SuppressedAtUtc = DateTimeOffset.UtcNow;
            predecessor.ContentSha256 = new string('a', 64); predecessor.Revision = source.Revision;
            if (boundary == "rename") predecessor.CanonicalPath += ".old";
            if (boundary == "different-identity") predecessor.StableSourceIdentity += ":different";
            if (boundary == "different-root")
            {
                var root = (SourceRootConfigurationEntity)db.Entry(await db.SourceRootConfigurations.SingleAsync(x => x.Id == source.SourceRootId)).CurrentValues.ToObject();
                root.Id = Guid.NewGuid(); root.CanonicalPath += "-different"; root.RowVersion = []; db.SourceRootConfigurations.Add(root); predecessor.SourceRootId = root.Id;
            }
            await db.SourceRevisions.Where(x => x.Id == source.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Revision, source.Revision + 1));
            db.SourceRevisions.Add(predecessor);
            await db.SaveChangesAsync();
            // Set up the immutable historical capture as the scanner would insert it.
            await db.SourceRevisions.Where(x => x.Id == source.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ParentSourceRevisionId, predecessor.Id).SetProperty(x => x.Revision, source.Revision + 1)
                .SetProperty(x => x.OriginKind, boundary == "derived" ? 1 : 0));
        }
        var command = Mutation(failed.Work.Job.JobId.Value); var service = Service(environment);
        if (boundary is not ("edit" or "rename"))
        { await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", default).AsTask()); return; }
        var preview = await service.PreviewAsync(command, "test", default);
        await service.CommitAsync(command, preview.ConfirmationId, "physical-history-" + boundary, "test", default);
        var work = await ClaimAsync(environment);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(environment.Factory))
            .ReadNextAsync(work, Profile, default);
        Assert.Equal(failed.Generation, batch.GenerationId);
    }

    [NativeSqlServerFact]
    public async Task First_embedding_draft_can_be_created_by_the_original_worker_after_confirmed_pre_draft_failure()
    {
        await using var environment = await SeedAsync(); var work = await ClaimAsync(environment);
        await new SqlStageTransitionStore(environment.Factory, sourceAdmission: new FixtureSourceAdmission(environment.Factory))
            .FailAsync(new(work.DispatchMessage, work.Job, "stage worker failed non-retryably", "SQL timeout before first draft", "test"), default);
        var command = Mutation(work.Job.JobId.Value); var service = Service(environment);
        var preview = await service.PreviewAsync(command, "test", default);
        await service.CommitAsync(command, preview.ConfirmationId, "pre-draft-recovery", "test", default);
        await using (var db = environment.Factory.CreateDbContext()) Assert.Empty(await db.IndexGenerations.Where(x => x.EmbeddingJobId == work.Job.JobId.Value).ToArrayAsync());
        var resumed = await ClaimAsync(environment);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(environment.Factory))
            .ReadNextAsync(resumed, Profile, default);
        Assert.NotEmpty(batch.Chunks);
        await using var check = environment.Factory.CreateDbContext();
        Assert.Equal(batch.GenerationId, (await check.IndexGenerations.SingleAsync(x => x.EmbeddingJobId == work.Job.JobId.Value)).Id);
        Assert.Empty(await check.EmbeddingGpuRequests.ToArrayAsync());
        var worker = new EmbedStageWorker(environment.Store, new SyntheticBatchedProvider(environment.Embeddings),
            new StageTransitionService(new SqlStageTransitionStore(environment.Factory, sourceAdmission: new FixtureSourceAdmission(environment.Factory)),
                new NoEvents(), new ChannelOutboxWakeSignal(), TimeProvider.System), TimeProvider.System,
            new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(environment.Factory)));
        while (true)
        {
            await worker.ExecuteAsync(resumed, default);
            if (await check.Jobs.AsNoTracking().AnyAsync(x => x.Id == work.Job.JobId.Value && x.PublicState == (int)PublicJobState.Completed)) break;
            resumed = await ClaimAsync(environment);
        }
        await environment.PumpAsync();
        Assert.True((await check.PipelineRecords.AsNoTracking().SingleAsync(x => x.Id == work.Job.PipelineRecordId.Value)).CompletionCriteriaMet);
    }

    [NativeSqlServerTheory]
    [InlineData("canonical-delivery")]
    [InlineData("chunks")]
    [InlineData("foreign-generation-vector")]
    [InlineData("draft-after-preview")]
    [InlineData("task-after-preview")]
    [InlineData("epoch-after-preview")]
    public async Task Pre_draft_recovery_refuses_changed_lineage_execution_evidence_and_stale_absence_confirmation(string boundary)
    {
        await using var environment = await SeedAsync(); var work = await ClaimAsync(environment);
        await new SqlStageTransitionStore(environment.Factory, sourceAdmission: new FixtureSourceAdmission(environment.Factory))
            .FailAsync(new(work.DispatchMessage, work.Job, "stage worker failed non-retryably", "before first draft", "test"), default);
        var service = Service(environment); var command = Mutation(work.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", default);
        await using (var db = environment.Factory.CreateDbContext())
        {
            var canonical = await db.Artifacts.SingleAsync(x => x.PipelineRecordId == work.Job.PipelineRecordId.Value && x.Stage == 3);
            switch (boundary)
            {
                case "canonical-delivery":
                    (await db.OutboxMessages.SingleAsync(x => x.CompletedArtifactId == canonical.Id)).CompletedArtifactId = null; break;
                case "chunks":
                    await db.TextChunks.Where(x => x.ArtifactId == canonical.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContentHash, new string('c', 64))); break;
                case "foreign-generation-vector":
                case "draft-after-preview":
                    var state = await db.IndexState.SingleAsync();
                    var generation = new IndexGenerationEntity { Id = Guid.NewGuid(), EmbeddingJobId = boundary == "draft-after-preview" ? work.Job.JobId.Value : null,
                        CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ModelFingerprint = Profile.ModelFingerprint, Dimensions = Profile.Dimensions,
                        MetadataChecksum = EmbedDraftDefaults.MetadataChecksum, CreatedAtUtc = DateTimeOffset.UtcNow };
                    db.IndexGenerations.Add(generation);
                    if (boundary == "foreign-generation-vector")
                    {
                        var chunk = await db.TextChunks.FirstAsync(x => x.ArtifactId == canonical.Id);
                        var values = new byte[Profile.Dimensions * 4];
                        db.Vectors.Add(new() { IndexGenerationId = generation.Id, TextChunkId = chunk.Id, SourceRevision = work.Job.SourceRevision,
                            ModelFingerprint = Profile.ModelFingerprint, Dimensions = Profile.Dimensions, Values = values, TextChunkContentHash = chunk.ContentHash,
                            SearchInputHash = chunk.SearchInputHash, PayloadChecksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(values)), CreatedAtUtc = DateTimeOffset.UtcNow });
                    }
                    break;
                case "task-after-preview":
                    db.GpuMiniTasks.Add(new() { Id = Guid.NewGuid(), ParentJobId = work.Job.JobId.Value, SourceRevision = work.Job.SourceRevision,
                        ModelRuntimeKey = Runtime.RuntimeKey, SettingsFingerprint = Runtime.SettingsFingerprint, EstimatedBytes = 1024,
                        IdempotencyKey = "pre-draft-injected-task", CreatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "epoch-after-preview": (await db.IndexState.SingleAsync()).CorpusEpoch = Guid.NewGuid(); break;
            }
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId, "pre-draft-refusal-" + boundary, "test", default).AsTask());
        await using var verify = environment.Factory.CreateDbContext();
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(x => x.Id == work.Job.JobId.Value)).PublicState);
        Assert.Empty(await verify.NativeOperationReceipts.Where(x => x.IdempotencyKey == "pre-draft-refusal-" + boundary).ToArrayAsync());
    }
}
