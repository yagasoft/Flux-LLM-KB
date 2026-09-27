using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlCorpusRebuildStore
{
    public ValueTask<CorpusRebuildReceipt> FinishAsync(Guid operationId, string sharedSlotKey,
        IIndexGenerationVerifier verifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return WithMaintenanceTransactionAsync(sharedSlotKey, async (context, ct) =>
        {
            var plan = await ReadCommittedPlanAsync(context, operationId, ct, allowCompleted: true).ConfigureAwait(false);
            var operation = await context.CorpusRebuildOperations.SingleAsync(value => value.Id == operationId, ct).ConfigureAwait(false);
            if (operation.CompletedAtUtc is not null)
                return new CorpusRebuildReceipt(operationId, plan.TargetEpoch, plan.ManifestHash, true);
            var items = await context.CorpusRebuildWorkItems.Where(value => value.OperationId == operationId).ToArrayAsync(ct).ConfigureAwait(false);
            if (items.Length != plan.Inputs.Count || items.Any(value => value.State != 2 || value.CompletedAtUtc is null))
                throw new CorpusRebuildRefusalException("corpus-rebuild-items-incomplete");
            if (await context.CorpusQueryLeases.AnyAsync(ct).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-query-drain-required");
            if (await context.EmbeddingGpuRequests.AnyAsync(value => value.State != 2 || !value.NativeCleanupConfirmed, ct).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-embedding-request-unsettled");
            foreach (var input in plan.Inputs)
            {
                _ = await ReadUnchangedInputAsync(context, input, ct).ConfigureAwait(false);
                if (!await context.PipelineRecords.AnyAsync(value => value.Id == input.PipelineRecordId &&
                        value.Revision == input.SourceRevision && value.CompletionCriteriaMet, ct).ConfigureAwait(false))
                    throw new CorpusRebuildRefusalException("corpus-rebuild-items-incomplete");
            }
            var state = await context.IndexState.SingleAsync(value => value.Id == 1, ct).ConfigureAwait(false);
            var vectors = await SqlPublishedPassageSelection.ReadVectorsAsync(context, ct).ConfigureAwait(false);
            var chunks = await context.TextChunks.AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false);
            var canonicalIds = plan.Inputs.Select(value => value.CanonicalArtifactId).ToHashSet();
            if (chunks.Any(value => !canonicalIds.Contains(value.ArtifactId) || value.PassagePolicyFingerprint != plan.PassagePolicyFingerprint ||
                    value.ContentHash != Hash(value.Content) || value.SearchInputHash != Hash(value.SearchText)) ||
                chunks.Length != vectors.Count || chunks.Select(value => value.Id).Order().SequenceEqual(vectors.Select(value => value.TextChunkId).Order()) == false ||
                await context.Vectors.LongCountAsync(ct).ConfigureAwait(false) != vectors.Count ||
                vectors.Any(value => value.ModelFingerprint != plan.Profile.ModelFingerprint || value.Dimensions != plan.Profile.Dimensions))
                throw new CorpusRebuildRefusalException("corpus-rebuild-membership-changed");
            if (vectors.Count == 0)
            {
                if (state.ActiveIndexGenerationId is not null || await context.IndexGenerations.AnyAsync(ct).ConfigureAwait(false) ||
                    await context.IndexGenerationVectors.AnyAsync(ct).ConfigureAwait(false))
                    throw new CorpusRebuildRefusalException("corpus-rebuild-membership-changed");
            }
            else
            {
                var generation = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == state.ActiveIndexGenerationId, ct).ConfigureAwait(false)
                    ?? throw new CorpusRebuildRefusalException("corpus-rebuild-generation-required");
                if (generation.ModelFingerprint != plan.Profile.ModelFingerprint || generation.Dimensions != plan.Profile.Dimensions ||
                    generation.CorpusEpoch != plan.TargetEpoch || generation.CorpusVersion != state.CorpusVersion || generation.ValidatedAtUtc is null ||
                    generation.RetiredAtUtc is not null)
                    throw new CorpusRebuildRefusalException("corpus-rebuild-generation-profile-changed");
                var membership = await context.IndexGenerationVectors.Where(value => value.GenerationId == generation.Id)
                    .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync(ct).ConfigureAwait(false);
                if (!membership.SequenceEqual(vectors.Select(value => value.VectorId)) || generation.VectorCount != vectors.Count)
                    throw new CorpusRebuildRefusalException("corpus-rebuild-membership-changed");
                verifier.Validate(generation.IndexPath, new(generation.Id, generation.ModelFingerprint, generation.Dimensions,
                    generation.IndexPath, generation.MetadataChecksum, generation.VectorCount, new(state.CorpusEpoch, state.CorpusVersion)), vectors);
            }
            if (!await FullTextIsCurrentAsync(context, chunks.LongLength, ct).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-full-text-not-ready");
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            operation.CompletedAtUtc = now;
            state.CorpusRebuildOperationId = null;
            state.EmptyCatalogueValidatedAtUtc = vectors.Count == 0 ? now : null;
            state.UpdatedAtUtc = now;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return new CorpusRebuildReceipt(operationId, plan.TargetEpoch, plan.ManifestHash, false);
        }, cancellationToken);
    }

    private static async Task<bool> FullTextIsCurrentAsync(FluxKnowledgeDbContext context, long chunkCount, CancellationToken ct) =>
        await context.Database.SqlQuery<int>($"""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]') AND
                [column_id] = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[TextChunks]'), N'SearchText', 'ColumnId'))
              AND FULLTEXTCATALOGPROPERTY(N'FluxKnowledge', N'PopulateStatus') = 0
              AND TRY_CONVERT(int, OBJECTPROPERTYEX(OBJECT_ID(N'[dbo].[TextChunks]'), N'TableFulltextPopulateStatus')) = 0
              AND TRY_CONVERT(bigint, OBJECTPROPERTYEX(OBJECT_ID(N'[dbo].[TextChunks]'), N'TableFulltextItemCount')) = {chunkCount}
              AND TRY_CONVERT(bigint, OBJECTPROPERTYEX(OBJECT_ID(N'[dbo].[TextChunks]'), N'TableFulltextPendingChanges')) = 0
              THEN 1 ELSE 0 END AS [Value]
            """).SingleAsync(ct).ConfigureAwait(false) == 1;
}
