using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlPipelineStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : IRegistrationStore, IPipelineStageReader, IIndexGenerationStore
{
    public SqlPipelineStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory)
        : this(contextFactory, TimeProvider.System)
    {
    }

    public async ValueTask<RegistrationReceipt> RegisterAsync(
        Utf8FileRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.CanonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.RequestedBy);
        ValidateHash(registration.ContentHash);
        if (registration.CanonicalPath.Length > 768)
        {
            throw new ArgumentException(
                "The canonical local source path exceeds the SQL stable-key limit.",
                nameof(registration));
        }

        await using var executionContext = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await contextFactory
                        .CreateDbContextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return await RegisterWithinTransactionAsync(
                            context,
                            registration,
                            cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    private async Task<RegistrationReceipt> RegisterWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        Utf8FileRegistration registration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var source = await context.SourceIdentities
            .FromSqlInterpolated(
                $"""
                 SELECT [Id], [SourceKind], [StableKey], [CreatedAtUtc]
                 FROM [SourceIdentities] WITH (UPDLOCK, HOLDLOCK)
                 WHERE [SourceKind] = N'local file'
                   AND [StableKey] = {registration.CanonicalPath}
                 """)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (source is null)
        {
            source = new SourceIdentityEntity
            {
                Id = Guid.NewGuid(),
                SourceKind = "local file",
                StableKey = registration.CanonicalPath,
                CreatedAtUtc = now
            };
            context.SourceIdentities.Add(source);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var latest = await context.PipelineRecords
            .Where(record => record.SourceIdentityId == source.Id)
            .OrderByDescending(record => record.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is not null &&
            string.Equals(latest.ContentHash, registration.ContentHash, StringComparison.Ordinal))
        {
            var initialJob = await context.Jobs.SingleAsync(
                    job =>
                        job.PipelineRecordId == latest.Id &&
                        job.SourceRevision == latest.Revision &&
                        job.Stage == (int)PipelineStage.Extract &&
                        job.Operation == PipelineOperations.ExtractUtf8,
                    cancellationToken)
                .ConfigureAwait(false);
            var initialDispatch = await context.OutboxMessages.SingleAsync(
                    message =>
                        message.PipelineRecordId == latest.Id &&
                        message.SourceRevision == latest.Revision &&
                        message.Stage == (int)PipelineStage.Extract &&
                        message.Operation == PipelineOperations.ExtractUtf8 &&
                        message.DispatchGeneration == 0,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CreateReceipt(latest, initialJob.Id, initialDispatch.Id, existing: true);
        }

        var recordId = Guid.NewGuid();
        var revision = latest?.Revision + 1 ?? 1;
        var record = new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = source.Id,
            Revision = revision,
            ContentHash = registration.ContentHash,
            RootLineageRecordId = latest?.RootLineageRecordId ?? recordId,
            ParentRevisionRecordId = latest?.Id,
            CurrentStage = (int)PipelineStage.Extract,
            RegisteredAtUtc = now
        };
        context.PipelineRecords.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var jobId = Guid.NewGuid();
        context.Jobs.Add(
            new JobEntity
            {
                Id = jobId,
                PipelineRecordId = recordId,
                SourceRevision = revision,
                Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractUtf8,
                PublicState = (int)PublicJobState.WorkerQueued,
                DueAtUtc = now
            });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var dispatchId = Guid.NewGuid();
        context.OutboxMessages.Add(
            new OutboxMessageEntity
            {
                Id = dispatchId,
                PipelineRecordId = recordId,
                SourceRevision = revision,
                Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractUtf8,
                DispatchGeneration = 0,
                IdempotencyKey = CreateIdempotencyKey(
                    recordId,
                    revision,
                    PipelineStage.Extract,
                    0),
                DueAtUtc = now,
                CreatedAtUtc = now
            });
        context.AuditEvents.Add(
            new AuditEventEntity
            {
                PipelineRecordId = recordId,
                EventType = "pipeline record registered",
                Actor = registration.RequestedBy,
                DetailsJson = JsonSerializer.Serialize(
                    new
                    {
                        registration.CanonicalPath,
                        registration.SourceLabel,
                        registration.ContentHash,
                        Revision = revision
                    }),
                OccurredAtUtc = now
            });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CreateReceipt(record, jobId, dispatchId, existing: false);
    }

    public async ValueTask<PipelineStageSource> ReadStageSourceAsync(
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipelineRecordId);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var source = await (
                from record in context.PipelineRecords.AsNoTracking()
                join identity in context.SourceIdentities.AsNoTracking()
                    on record.SourceIdentityId equals identity.Id
                where record.Id == pipelineRecordId.Value &&
                      record.Revision == sourceRevision &&
                      !record.IsDeleted
                select new
                {
                    identity.StableKey,
                    record.ContentHash,
                    record.SourceRevisionId
                })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException(
                "The claimed pipeline record revision no longer exists.");
        }

        string? inputText = null;
        if (stage > PipelineStage.Extract)
        {
            var inputStage = (PipelineStage)((int)stage - 1);
            inputText = await context.Artifacts.AsNoTracking()
                .Where(
                    artifact =>
                        artifact.PipelineRecordId == pipelineRecordId.Value &&
                        artifact.SourceRevision == sourceRevision &&
                        artifact.Stage == (int)inputStage)
                .Select(artifact => artifact.SearchText)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return new PipelineStageSource(
            pipelineRecordId,
            sourceRevision,
            source.StableKey,
            source.ContentHash,
            inputText,
            source.SourceRevisionId is null ? null : new FluxKnowledge.Domain.Sources.SourceRevisionId(source.SourceRevisionId.Value));
    }

    public async ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await (
            from chunk in context.TextChunks.AsNoTracking()
            join artifact in context.Artifacts.AsNoTracking() on chunk.ArtifactId equals artifact.Id
            where artifact.PipelineRecordId == pipelineRecordId.Value &&
                  artifact.SourceRevision == sourceRevision &&
                  artifact.Stage == (int)PipelineStage.CanonicalIndex
            orderby chunk.Ordinal
            select new CanonicalTextChunk(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length,
                chunk.Content, chunk.ContentHash))
            .ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await (
            from membership in context.IndexGenerationVectors.AsNoTracking()
            join vector in context.Vectors.AsNoTracking() on membership.VectorId equals vector.VectorId
            where membership.GenerationId == indexGenerationId
            orderby vector.VectorId
            select new CanonicalVector(vector.VectorId, vector.TextChunkId,
                vector.ModelFingerprint, vector.Dimensions, vector.Values,
                vector.TextChunkContentHash, vector.PayloadChecksum,
                vector.SourceRevision))
            .ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await (
            from vector in context.Vectors.AsNoTracking()
            join chunk in context.TextChunks.AsNoTracking() on vector.TextChunkId equals chunk.Id
            join artifact in context.Artifacts.AsNoTracking() on chunk.ArtifactId equals artifact.Id
            join record in context.PipelineRecords.AsNoTracking() on artifact.PipelineRecordId equals record.Id
            where !vector.IsDeleted && !record.IsDeleted &&
                  (record.SourceRevisionId.HasValue
                      ? context.SourceRevisions.Any(sourceRevision =>
                          sourceRevision.Id == record.SourceRevisionId.Value && sourceRevision.SuppressedAtUtc == null)
                      : record.Revision == context.PipelineRecords
                          .Where(candidate => candidate.SourceIdentityId == record.SourceIdentityId)
                          .Max(candidate => candidate.Revision))
            orderby vector.VectorId
            select new CanonicalVector(vector.VectorId, vector.TextChunkId,
                vector.ModelFingerprint, vector.Dimensions, vector.Values,
                vector.TextChunkContentHash, vector.PayloadChecksum,
                vector.SourceRevision))
            .ToListAsync(cancellationToken);
    }

    public async ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.IndexGenerations.AsNoTracking()
            .Where(generation => generation.Id == indexGenerationId)
            .Select(generation => new IndexGenerationDescriptor(generation.Id,
                generation.ModelFingerprint, generation.Dimensions, generation.IndexPath,
                generation.MetadataChecksum, generation.VectorCount))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.IndexState.AsNoTracking()
            .Where(state => state.Id == 1)
            .Select(state => state.ActiveIndexGenerationId)
            .SingleAsync(cancellationToken);
    }

    public async ValueTask UpdateGenerationMetadataAsync(
        IndexGenerationDescriptor generation,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.IndexGenerations.SingleOrDefaultAsync(
            candidate => candidate.Id == generation.Id, cancellationToken);
        if (entity is null)
        {
            entity = new IndexGenerationEntity { Id = generation.Id, CreatedAtUtc = timeProvider.GetUtcNow() };
            context.IndexGenerations.Add(entity);
        }
        entity.ModelFingerprint = generation.ModelFingerprint;
        entity.Dimensions = generation.Dimensions;
        entity.IndexPath = generation.IndexPath;
        entity.MetadataChecksum = generation.MetadataChecksum;
        entity.VectorCount = generation.VectorCount;
        entity.ValidatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }

    internal static string CreateIdempotencyKey(
        Guid pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        long dispatchGeneration) =>
        $"{pipelineRecordId:N}:{sourceRevision}:{stage}:{dispatchGeneration}";

    private static RegistrationReceipt CreateReceipt(
        PipelineRecordEntity record,
        Guid jobId,
        Guid dispatchId,
        bool existing) =>
        new(
            new PipelineRecordId(record.Id),
            new JobId(jobId),
            new DispatchMessageId(dispatchId),
            record.Revision,
            record.ContentHash,
            new PipelineRecordId(record.RootLineageRecordId),
            record.ParentRevisionRecordId is { } parentId
                ? new PipelineRecordId(parentId)
                : null,
            existing);

    private static void ValidateHash(string contentHash)
    {
        if (contentHash.Length != 64 ||
            contentHash.Any(
                character =>
                    character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Content hash must be a lower-case SHA-256 value.",
                nameof(contentHash));
        }
    }
}
