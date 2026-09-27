using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;
using System.Runtime.CompilerServices;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlCorpusRebuildStore
{
    public async ValueTask<CorpusRebuildPreparation> PrepareAsync(Guid operationId, Guid pipelineRecordId,
        PassageBuilder passageBuilder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passageBuilder);
        await using var readContext = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var plan = await ReadCommittedPlanAsync(readContext, operationId, cancellationToken).ConfigureAwait(false);
        if (plan.PassagePolicyFingerprint != passageBuilder.PolicyFingerprint)
            throw new CorpusRebuildRefusalException("corpus-rebuild-policy-changed");
        var input = plan.Inputs.SingleOrDefault(value => value.PipelineRecordId == pipelineRecordId)
            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-item-not-captured");
        var source = await ReadUnchangedInputAsync(readContext, input, cancellationToken).ConfigureAwait(false);
        // Tokenisation runs outside the serializable publication transaction. Commit rechecks its exact input.
        var passages = passageBuilder.BuildDocument(source.SearchText, source.DocumentMetadataJson);
        return await readContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            _ = await ReadCommittedPlanAsync(context, operationId, cancellationToken).ConfigureAwait(false);
            _ = await ReadUnchangedInputAsync(context, input, cancellationToken).ConfigureAwait(false);
            var item = await context.CorpusRebuildWorkItems.SingleAsync(value => value.OperationId == operationId &&
                value.PipelineRecordId == pipelineRecordId, cancellationToken).ConfigureAwait(false);
            if (item.State != 0)
            {
                var count = await context.TextChunks.CountAsync(value => value.ArtifactId == input.CanonicalArtifactId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CorpusRebuildPreparation(pipelineRecordId, item.State == 2 && count == 0 ? null : item.EmbeddingJobId,
                    item.State == 2 && count == 0 ? null : item.DispatchMessageId, count, true);
            }
            if (await context.TextChunks.AnyAsync(value => value.ArtifactId == input.CanonicalArtifactId, cancellationToken).ConfigureAwait(false) ||
                await context.Jobs.AnyAsync(value => value.Id == input.EmbeddingJobId, cancellationToken).ConfigureAwait(false) ||
                await context.OutboxMessages.AnyAsync(value => value.Id == input.DispatchMessageId, cancellationToken).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-preparation-conflict");
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            item.PreparedAtUtc = now;
            if (passages.Count == 0)
            {
                item.State = 2;
                item.CompletedAtUtc = now;
            }
            else
            {
                foreach (var passage in passages)
                    context.TextChunks.Add(new TextChunkEntity
                    {
                        ArtifactId = input.CanonicalArtifactId, SourceRevision = input.SourceRevision,
                        Ordinal = passage.Ordinal, StartOffset = passage.StartOffset, Length = passage.Length,
                        Content = passage.Content, ContentHash = passage.ContentHash,
                        PassagePolicyFingerprint = passage.PassagePolicyFingerprint, ContextHeader = passage.ContextHeader,
                        SearchInputHash = passage.SearchInputHash
                    });
                var generation = checked(1 + (await context.OutboxMessages.Where(value => value.PipelineRecordId == pipelineRecordId &&
                    value.SourceRevision == input.SourceRevision).MaxAsync(value => (long?)value.DispatchGeneration, cancellationToken).ConfigureAwait(false) ?? 0));
                context.Jobs.Add(new JobEntity
                {
                    Id = input.EmbeddingJobId, PipelineRecordId = pipelineRecordId, SourceRevision = input.SourceRevision,
                    Stage = (int)PipelineStage.Embed, Operation = PipelineOperations.Embed,
                    PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now
                });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                context.OutboxMessages.Add(new OutboxMessageEntity
                {
                    Id = input.DispatchMessageId, JobId = input.EmbeddingJobId, PipelineRecordId = pipelineRecordId,
                    SourceRevision = input.SourceRevision, Stage = (int)PipelineStage.Embed, Operation = PipelineOperations.Embed,
                    DispatchGeneration = generation, IdempotencyKey = SqlPipelineStore.CreateIdempotencyKey(pipelineRecordId, input.SourceRevision, PipelineStage.Embed, generation),
                    DueAtUtc = now, CreatedAtUtc = now
                });
                var record = await context.PipelineRecords.SingleAsync(value => value.Id == pipelineRecordId, cancellationToken).ConfigureAwait(false);
                record.CompletionCriteriaMet = false;
                record.CurrentStage = (int)PipelineStage.Embed;
                item.State = 1;
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CorpusRebuildPreparation(pipelineRecordId, passages.Count == 0 ? null : input.EmbeddingJobId,
                passages.Count == 0 ? null : input.DispatchMessageId, passages.Count, false);
        }).ConfigureAwait(false);
    }

    private static async Task<CorpusRebuildPlan> ReadCommittedPlanAsync(FluxKnowledgeDbContext context, Guid operationId, CancellationToken ct,
        bool allowCompleted = false)
    {
        var operation = await context.CorpusRebuildOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == operationId, ct).ConfigureAwait(false)
            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-operation-not-found");
        var plan = JsonSerializer.Deserialize<CorpusRebuildPlan>(operation.ManifestJson)
            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-receipt-invalid");
        if (plan.OperationId != operation.Id || plan.TargetEpoch != operation.TargetEpoch || plan.ManifestHash != operation.ManifestHash ||
            plan.ManifestHash != Hash(JsonSerializer.Serialize(plan with { ManifestHash = "" })))
            throw new CorpusRebuildRefusalException("corpus-rebuild-receipt-invalid");
        if (allowCompleted && operation.CompletedAtUtc is not null) return plan;
        if (!await context.IndexState.AnyAsync(value => value.Id == 1 && value.CorpusRebuildOperationId == operationId &&
                value.CorpusEpoch == plan.TargetEpoch, ct).ConfigureAwait(false) || operation.CompletedAtUtc is not null)
            throw new CorpusRebuildRefusalException("corpus-rebuild-operation-not-active");
        return plan;
    }

    private static async Task<RebuildInputRow> ReadUnchangedInputAsync(FluxKnowledgeDbContext context, CorpusRebuildInput input, CancellationToken ct)
    {
        var row = await context.Database.SqlQuery<RebuildInputRow>(SqlPublishedPassageSelection.BindSources($"""
            SELECT [record].[Id] AS [PipelineRecordId], [record].[Revision] AS [SourceRevision],
                [artifact].[Id] AS [CanonicalArtifactId], [artifact].[ContentHash], [artifact].[SearchText], [artifact].[DocumentMetadataJson],
                [record].[SourceRevisionId] AS [RetainedInputId], [retained].[ContentSha256] AS [RetainedHash],
                [root].[Id] AS [RootId], [root].[State] AS [RootState], [root].[ConfigurationRevision] AS [RootRevision],
                [publication].[OwnerSourceRevisionId] AS [OwnerId], [owner].[ContentSha256] AS [OwnerHash],
                [publication].[DocumentInputSourceRevisionId] AS [PublishedInputId], [publication].[SourceProcessorBranchId] AS [BranchId],
                [publication].[ProcessorFingerprint], [publication].[PublishedAtUtc]
            FROM [Artifacts] AS [artifact]
            INNER JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            /*publication-bindings*/
            WHERE [record].[Id] = {input.PipelineRecordId} AND [record].[Revision] = {input.SourceRevision}
              AND [record].[IsDeleted] = 0 AND [artifact].[Id] = {input.CanonicalArtifactId}
              AND [artifact].[SourceRevision] = [record].[Revision] AND [artifact].[Stage] = {(int)PipelineStage.CanonicalIndex}
              AND [retained].[SuppressedAtUtc] IS NULL AND [owner].[SuppressedAtUtc] IS NULL
            """)).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null || row.ContentHash != input.ContentHash || Hash(row.SearchText) != input.ContentHash ||
            Hash(JsonSerializer.Serialize(row.DocumentMetadataJson)) != input.MetadataHash || BindingHash(row) != input.SourceBindingHash)
            throw new CorpusRebuildRefusalException("corpus-rebuild-input-changed");
        return row;
    }

    internal static async Task<bool> ValidateMaintenanceJobAsync(FluxKnowledgeDbContext context, Guid jobId,
        CancellationToken ct, EmbeddingProfile? profile = null)
    {
        var operationId = await context.IndexState.Where(value => value.Id == 1)
            .Select(value => value.CorpusRebuildOperationId).SingleAsync(ct).ConfigureAwait(false);
        if (operationId is null) return false;
        var query = FormattableStringFactory.Create($"SELECT COUNT(*) AS [Value] FROM [Jobs] WHERE [Id] = {{0}} AND {SqlCorpusRebuildEligibility.ActiveJob("[Jobs].[Id]")}", jobId);
        if (await context.Database.SqlQuery<int>(query).SingleAsync(ct).ConfigureAwait(false) != 1)
            throw new CorpusRebuildRefusalException("corpus-rebuild-job-not-authorised");
        var plan = await ReadCommittedPlanAsync(context, operationId.Value, ct).ConfigureAwait(false);
        if (profile is not null && profile != plan.Profile)
            throw new CorpusRebuildRefusalException("corpus-rebuild-profile-changed");
        var recordId = await context.Jobs.Where(value => value.Id == jobId).Select(value => value.PipelineRecordId).SingleAsync(ct).ConfigureAwait(false);
        _ = await ReadUnchangedInputAsync(context, plan.Inputs.Single(value => value.PipelineRecordId == recordId), ct).ConfigureAwait(false);
        return true;
    }

    internal static async Task<bool> PreserveCapturedPublicationAsync(FluxKnowledgeDbContext context, PipelineRecordEntity record, CancellationToken ct)
    {
        var operationId = await context.IndexState.Where(value => value.Id == 1)
            .Select(value => value.CorpusRebuildOperationId).SingleAsync(ct).ConfigureAwait(false);
        if (operationId is null) return false;
        var plan = await ReadCommittedPlanAsync(context, operationId.Value, ct).ConfigureAwait(false);
        var input = plan.Inputs.SingleOrDefault(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision)
            ?? throw new CorpusRebuildRefusalException("corpus-rebuild-publication-not-captured");
        if (!await context.CorpusRebuildWorkItems.AnyAsync(value => value.OperationId == operationId &&
                value.PipelineRecordId == record.Id && value.State == 1, ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-item-not-prepared");
        _ = await ReadUnchangedInputAsync(context, input, ct).ConfigureAwait(false);
        // This publication was selected before reset. Keep its exact winner, branch and timestamp.
        return true;
    }

    internal static async Task CompletePublishedItemAsync(FluxKnowledgeDbContext context, Guid jobId,
        IndexGenerationDescriptor? generation, DateTimeOffset now, CancellationToken ct)
    {
        if (!await ValidateMaintenanceJobAsync(context, jobId, ct,
                generation is null ? null : new(generation.ModelFingerprint, generation.Dimensions)).ConfigureAwait(false)) return;
        if (generation is null) throw new CorpusRebuildRefusalException("corpus-rebuild-generation-required");
        var operationId = (await context.IndexState.SingleAsync(value => value.Id == 1, ct).ConfigureAwait(false)).CorpusRebuildOperationId!.Value;
        var recordId = await context.Jobs.Where(value => value.Id == jobId).Select(value => value.PipelineRecordId).SingleAsync(ct).ConfigureAwait(false);
        var item = await context.CorpusRebuildWorkItems.SingleAsync(value => value.OperationId == operationId &&
            value.PipelineRecordId == recordId, ct).ConfigureAwait(false);
        item.State = 2;
        item.CompletedAtUtc = now;
    }
}
