using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlStageTransitionStore : IStageTransitionStore
{
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _contextFactory;
    private readonly IStageTransitionFailureInjector? _failureInjector;
    private readonly TimeProvider _timeProvider;

    public SqlStageTransitionStore(
        IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
        IStageTransitionFailureInjector? failureInjector,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _failureInjector = failureInjector;
        _timeProvider = timeProvider;
    }

    public SqlStageTransitionStore(
        IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
        IStageTransitionFailureInjector? failureInjector = null)
        : this(contextFactory, failureInjector, TimeProvider.System)
    {
    }

    public async ValueTask<StageTransitionResult> TransitionAsync(
        StageTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await using var executionContext = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await _contextFactory
                        .CreateDbContextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return await TransitionWithinTransactionAsync(
                            context,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    private async Task<StageTransitionResult> TransitionWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        StageTransitionRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(
                request.IndexingOutput?.ActivateGeneration is null
                    ? IsolationLevel.ReadCommitted
                    : IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

        var validated = await ValidateClaimAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        if (validated.DispatchMessage.DispatchedAtUtc is not null)
        {
            var existing = await ReadExistingTransitionAsync(
                    context,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        context.Artifacts.Add(
            new ArtifactEntity
            {
                Id = request.Artifact.Id,
                PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                SourceRevision = request.CurrentJob.SourceRevision,
                Stage = (int)request.Artifact.Stage,
                ContentHash = request.Artifact.ContentHash,
                ContentType = request.Artifact.ContentType,
                SearchText = request.Artifact.SearchText,
                CreatedAtUtc = request.Artifact.CreatedAtUtc
            });
        WriteIndexingOutput(context, request);
        context.AuditEvents.Add(
            new AuditEventEntity
            {
                PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                EventType = "pipeline stage completed",
                Actor = request.Actor,
                DetailsJson = JsonSerializer.Serialize(
                    new
                    {
                        Stage = request.Artifact.Stage.ToString(),
                        request.DispatchMessage.IdempotencyKey,
                        ArtifactId = request.Artifact.Id
                    }),
                OccurredAtUtc = _timeProvider.GetUtcNow()
            });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (_failureInjector is not null)
        {
            await _failureInjector.AfterArtifactWrittenAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var completed = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [Jobs]
                 SET [PublicState] = {(int)PublicJobState.Completed},
                     [LeaseOwner] = NULL,
                     [LeaseExpiresAtUtc] = NULL,
                     [Reason] = NULL,
                     [ErrorDetails] = NULL
                 WHERE [Id] = {request.CurrentJob.JobId.Value}
                   AND [PipelineRecordId] = {request.CurrentJob.PipelineRecordId.Value}
                   AND [SourceRevision] = {request.CurrentJob.SourceRevision}
                   AND [PublicState] = {(int)PublicJobState.WorkerProcessing}
                   AND [LeaseOwner] = {request.CurrentJob.LeaseOwner}
                   AND [LeaseGeneration] = {request.CurrentJob.LeaseGeneration};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (completed != 1)
        {
            throw new InvalidOperationException(
                "The current Job lease was lost before the stage transition completed.");
        }

        JobId? nextJobId = null;
        DispatchMessageId? nextDispatchMessageId = null;
        if (request.NextStage is { } nextStage)
        {
            var nextJobGuid = Guid.NewGuid();
            nextJobId = new JobId(nextJobGuid);
            context.Jobs.Add(
                new JobEntity
                {
                    Id = nextJobGuid,
                    PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                    SourceRevision = request.CurrentJob.SourceRevision,
                    Stage = (int)nextStage,
                    Operation = request.NextOperation!,
                    PublicState = (int)PublicJobState.WorkerQueued,
                    DueAtUtc = _timeProvider.GetUtcNow()
                });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var nextDispatchGuid = Guid.NewGuid();
            nextDispatchMessageId = new DispatchMessageId(nextDispatchGuid);
            var nextGeneration = request.DispatchMessage.DispatchGeneration + 1;
            context.OutboxMessages.Add(
                new OutboxMessageEntity
                {
                    Id = nextDispatchGuid,
                    PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                    SourceRevision = request.CurrentJob.SourceRevision,
                    Stage = (int)nextStage,
                    Operation = request.NextOperation!,
                    DispatchGeneration = nextGeneration,
                    IdempotencyKey = SqlPipelineStore.CreateIdempotencyKey(
                        request.CurrentJob.PipelineRecordId.Value,
                        request.CurrentJob.SourceRevision,
                        nextStage,
                        nextGeneration),
                    DueAtUtc = _timeProvider.GetUtcNow(),
                    CreatedAtUtc = _timeProvider.GetUtcNow()
                });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            validated.PipelineRecord.CurrentStage = (int)nextStage;
        }
        else
        {
            validated.PipelineRecord.CurrentStage = (int)request.Artifact.Stage;
        }
        if (PipelineCompletionCriteria.IsMet(request.Artifact.Stage, request.NextStage))
        {
            validated.PipelineRecord.CompletionCriteriaMet = true;
        }

        if (request.IndexingOutput?.ActivateGeneration is { } activeGeneration)
        {
            var expectedMembership = request.IndexingOutput.ActivateMembership
                ?? throw new ArgumentException("An active generation requires an immutable vector membership snapshot.", nameof(request));
            var currentMembership = await ReadEligibleVectorsAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (!SameSnapshot(expectedMembership, currentMembership) ||
                !string.Equals(activeGeneration.MetadataChecksum,
                    ComputeSnapshotChecksum(activeGeneration.ModelFingerprint, activeGeneration.Dimensions, expectedMembership),
                    StringComparison.Ordinal))
            {
                // Superseded candidates complete their durable Publish work without moving
                // the pointer backwards; a later current-corpus Publish owns activation.
                goto SkipActivation;
            }

            await EnsureGenerationExistsAsync(context, activeGeneration, cancellationToken)
                .ConfigureAwait(false);
            var existingGeneration = await context.IndexGenerations.AsNoTracking()
                .SingleAsync(generation => generation.Id == activeGeneration.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!SameGeneration(existingGeneration, activeGeneration))
            {
                throw new IndexGenerationStaleException(
                    "The immutable generation ID already has incompatible SQL metadata.");
            }

            var existingMembership = await context.IndexGenerationVectors.AsNoTracking()
                .Where(membership => membership.GenerationId == activeGeneration.Id)
                .Select(membership => membership.VectorId).OrderBy(vectorId => vectorId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existingMembership.Count > 0 && !existingMembership.SequenceEqual(currentMembership.Select(vector => vector.VectorId)))
            {
                throw new IndexGenerationStaleException(
                    "The immutable generation ID already has incompatible SQL membership.");
            }

            await EnsureMembershipExistsAsync(context, activeGeneration.Id, currentMembership, cancellationToken)
                .ConfigureAwait(false);
            var state = await context.IndexState.SingleAsync(state => state.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            state.ActiveIndexGenerationId = activeGeneration.Id;
            state.UpdatedAtUtc = _timeProvider.GetUtcNow();
        SkipActivation: ;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await MarkDispatchCompleteAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StageTransitionResult(
            request.Artifact.Id,
            nextJobId,
            nextDispatchMessageId,
            ExistingTransition: false);
    }

    public async ValueTask FailAsync(
        StageFailureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        await using var executionContext = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await _contextFactory
                        .CreateDbContextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await FailWithinTransactionAsync(context, request, cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    private async Task FailWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        StageFailureRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var dispatch = await context.OutboxMessages.SingleOrDefaultAsync(
                message =>
                    message.Id == request.DispatchMessage.DispatchMessageId.Value &&
                    message.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                    message.SourceRevision == request.CurrentJob.SourceRevision &&
                    message.IdempotencyKey == request.DispatchMessage.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (dispatch is null)
        {
            throw new InvalidOperationException(
                "The claimed DispatchMessage does not match a durable delivery.");
        }

        if (dispatch.DispatchedAtUtc is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        ValidateDispatchLease(dispatch, request.DispatchMessage);
        var failed = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [Jobs]
                 SET [PublicState] = {(int)PublicJobState.Failed},
                     [LeaseOwner] = NULL,
                     [LeaseExpiresAtUtc] = NULL,
                     [Reason] = {request.Reason},
                     [ErrorDetails] = {request.ErrorDetails}
                 WHERE [Id] = {request.CurrentJob.JobId.Value}
                   AND [PipelineRecordId] = {request.CurrentJob.PipelineRecordId.Value}
                   AND [SourceRevision] = {request.CurrentJob.SourceRevision}
                   AND [PublicState] = {(int)PublicJobState.WorkerProcessing}
                   AND [LeaseOwner] = {request.CurrentJob.LeaseOwner}
                   AND [LeaseGeneration] = {request.CurrentJob.LeaseGeneration};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (failed != 1)
        {
            throw new InvalidOperationException(
                "The current Job lease was lost before failure could be persisted.");
        }

        context.AuditEvents.Add(
            new AuditEventEntity
            {
                PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                EventType = "pipeline stage failed",
                Actor = request.Actor,
                DetailsJson = JsonSerializer.Serialize(
                    new
                    {
                        request.Reason,
                        request.ErrorDetails,
                        request.DispatchMessage.IdempotencyKey
                    }),
                OccurredAtUtc = _timeProvider.GetUtcNow()
            });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await MarkDispatchCompleteAsync(
                context,
                request.DispatchMessage,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ValidatedClaim> ValidateClaimAsync(
        FluxKnowledgeDbContext context,
        StageTransitionRequest request,
        CancellationToken cancellationToken)
    {
        var record = await context.PipelineRecords.SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == request.CurrentJob.PipelineRecordId.Value &&
                    candidate.Revision == request.CurrentJob.SourceRevision &&
                    !candidate.IsDeleted,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            throw new InvalidOperationException(
                "The PipelineRecord revision does not match the claimed stage work.");
        }

        var dispatch = await context.OutboxMessages.SingleOrDefaultAsync(
                message =>
                    message.Id == request.DispatchMessage.DispatchMessageId.Value &&
                    message.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                    message.SourceRevision == request.CurrentJob.SourceRevision &&
                    message.IdempotencyKey == request.DispatchMessage.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (dispatch is null ||
            dispatch.Stage != (int)request.CurrentJob.Stage ||
            !string.Equals(
                dispatch.Operation,
                request.CurrentJob.Operation,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The DispatchMessage idempotency key does not match the claimed stage work.");
        }

        if (dispatch.DispatchedAtUtc is null)
        {
            ValidateDispatchLease(dispatch, request.DispatchMessage);
            var jobMatches = await context.Jobs.AsNoTracking().AnyAsync(
                    job =>
                        job.Id == request.CurrentJob.JobId.Value &&
                        job.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                        job.SourceRevision == request.CurrentJob.SourceRevision &&
                        job.Stage == (int)request.CurrentJob.Stage &&
                        job.Operation == request.CurrentJob.Operation &&
                        job.PublicState == (int)PublicJobState.WorkerProcessing &&
                        job.LeaseOwner == request.CurrentJob.LeaseOwner &&
                        job.LeaseGeneration == request.CurrentJob.LeaseGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!jobMatches)
            {
                throw new InvalidOperationException(
                    "The Job lease does not match the claimed stage work.");
            }
        }

        return new ValidatedClaim(record, dispatch);
    }

    private static void ValidateDispatchLease(
        OutboxMessageEntity durable,
        Application.Workers.ClaimedDispatchMessage claim)
    {
        if (!string.Equals(durable.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal) ||
            durable.LeaseGeneration != claim.LeaseGeneration)
        {
            throw new InvalidOperationException(
                "The DispatchMessage lease does not match the claimed delivery.");
        }
    }

    private static async Task<StageTransitionResult> ReadExistingTransitionAsync(
        FluxKnowledgeDbContext context,
        StageTransitionRequest request,
        CancellationToken cancellationToken)
    {
        var artifact = await context.Artifacts.AsNoTracking().SingleOrDefaultAsync(
                candidate =>
                    candidate.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                    candidate.SourceRevision == request.CurrentJob.SourceRevision &&
                    candidate.Stage == (int)request.Artifact.Stage,
                cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
        {
            throw new InvalidOperationException(
                "The durable delivery completed without the expected stage artefact.");
        }

        JobId? nextJobId = null;
        DispatchMessageId? nextDispatchMessageId = null;
        if (request.NextStage is { } nextStage)
        {
            var nextJob = await context.Jobs.AsNoTracking().SingleAsync(
                    job =>
                        job.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                        job.SourceRevision == request.CurrentJob.SourceRevision &&
                        job.Stage == (int)nextStage &&
                        job.Operation == request.NextOperation,
                    cancellationToken)
                .ConfigureAwait(false);
            var nextDispatch = await context.OutboxMessages.AsNoTracking().SingleAsync(
                    message =>
                        message.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                        message.SourceRevision == request.CurrentJob.SourceRevision &&
                        message.Stage == (int)nextStage &&
                        message.Operation == request.NextOperation &&
                        message.DispatchGeneration ==
                        request.DispatchMessage.DispatchGeneration + 1,
                    cancellationToken)
                .ConfigureAwait(false);
            nextJobId = new JobId(nextJob.Id);
            nextDispatchMessageId = new DispatchMessageId(nextDispatch.Id);
        }

        return new StageTransitionResult(
            artifact.Id,
            nextJobId,
            nextDispatchMessageId,
            ExistingTransition: true);
    }

    private async Task MarkDispatchCompleteAsync(
        FluxKnowledgeDbContext context,
        StageTransitionRequest request,
        CancellationToken cancellationToken) =>
        await MarkDispatchCompleteAsync(
                context,
                request.DispatchMessage,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task MarkDispatchCompleteAsync(
        FluxKnowledgeDbContext context,
        Application.Workers.ClaimedDispatchMessage claim,
        CancellationToken cancellationToken)
    {
        var dispatchedAt = _timeProvider.GetUtcNow();
        var acknowledged = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [OutboxMessages]
                 SET [DispatchedAtUtc] = {dispatchedAt},
                     [LeaseOwner] = NULL,
                     [LeaseExpiresAtUtc] = NULL
                 WHERE [Id] = {claim.DispatchMessageId.Value}
                   AND [DispatchedAtUtc] IS NULL
                   AND [LeaseOwner] = {claim.LeaseOwner}
                   AND [LeaseGeneration] = {claim.LeaseGeneration};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (acknowledged != 1)
        {
            throw new InvalidOperationException(
                "The DispatchMessage lease was lost before commit.");
        }
    }

    private static void ValidateRequest(StageTransitionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
        if ((request.NextStage is null) != (request.NextOperation is null))
        {
            throw new ArgumentException(
                "Next stage and next operation must either both be present or both be absent.",
                nameof(request));
        }

        if (request.Artifact.Stage != request.CurrentJob.Stage ||
            request.DispatchMessage.Stage != request.CurrentJob.Stage ||
            request.DispatchMessage.PipelineRecordId != request.CurrentJob.PipelineRecordId ||
            request.DispatchMessage.SourceRevision != request.CurrentJob.SourceRevision)
        {
            throw new ArgumentException(
                "Stage transition inputs must describe the same record revision and stage.",
                nameof(request));
        }
    }

    private void WriteIndexingOutput(FluxKnowledgeDbContext context, StageTransitionRequest request)
    {
        var output = request.IndexingOutput;
        if (output is null)
        {
            return;
        }

        if (output.Chunks is not null)
        {
            foreach (var chunk in output.Chunks)
            {
                context.TextChunks.Add(new TextChunkEntity
                {
                    ArtifactId = request.Artifact.Id,
                    SourceRevision = request.CurrentJob.SourceRevision,
                    Ordinal = chunk.Ordinal,
                    StartOffset = chunk.StartOffset,
                    Length = chunk.Length,
                    Content = chunk.Content,
                    ContentHash = chunk.ContentHash
                });
            }
        }

        if (output.IndexGenerationId is { } generationId)
        {
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = generationId,
                ModelFingerprint = output.ModelFingerprint!,
                Dimensions = output.Vectors?.FirstOrDefault()?.Dimensions ?? 256,
                IndexPath = string.Empty,
                MetadataChecksum = new string('0', 64),
                VectorCount = output.Vectors?.Count ?? 0,
                CreatedAtUtc = _timeProvider.GetUtcNow()
            });
            foreach (var vector in output.Vectors ?? [])
            {
                context.Vectors.Add(new VectorEntity
                {
                    TextChunkId = vector.TextChunkId,
                    ModelFingerprint = vector.ModelFingerprint,
                    Dimensions = vector.Dimensions,
                    Values = vector.Values,
                    TextChunkContentHash = vector.TextChunkContentHash,
                    PayloadChecksum = vector.PayloadChecksum,
                    SourceRevision = vector.SourceRevision,
                    IsDeleted = false,
                    IndexGenerationId = generationId,
                    CreatedAtUtc = _timeProvider.GetUtcNow()
                });
            }
        }
    }

    private static Task<List<CanonicalVector>> ReadEligibleVectorsAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken) =>
        (
            from vector in context.Vectors
            join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
            join record in context.PipelineRecords on artifact.PipelineRecordId equals record.Id
            where !vector.IsDeleted && !record.IsDeleted &&
                  record.Revision == context.PipelineRecords
                      .Where(candidate => candidate.SourceIdentityId == record.SourceIdentityId)
                      .Max(candidate => candidate.Revision)
            orderby vector.VectorId
            select new CanonicalVector(vector.VectorId, vector.TextChunkId,
                vector.ModelFingerprint, vector.Dimensions, vector.Values,
                vector.TextChunkContentHash, vector.PayloadChecksum,
                vector.SourceRevision))
        .ToListAsync(cancellationToken);

    private static bool SameSnapshot(
        IReadOnlyList<CanonicalVector> expected,
        IReadOnlyList<CanonicalVector> actual) =>
        expected.Count == actual.Count && expected.Zip(actual, static (left, right) =>
            left.VectorId == right.VectorId &&
            left.Dimensions == right.Dimensions &&
            string.Equals(left.ModelFingerprint, right.ModelFingerprint, StringComparison.Ordinal) &&
            string.Equals(left.TextChunkContentHash, right.TextChunkContentHash, StringComparison.Ordinal) &&
            string.Equals(left.PayloadChecksum, right.PayloadChecksum, StringComparison.Ordinal))
            .All(static equal => equal);

    private async Task EnsureGenerationExistsAsync(
        FluxKnowledgeDbContext context,
        IndexGenerationDescriptor generation,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO [IndexGenerations]
                     ([Id], [ModelFingerprint], [Dimensions], [IndexPath], [MetadataChecksum], [VectorCount], [CreatedAtUtc], [ValidatedAtUtc])
                 SELECT {generation.Id}, {generation.ModelFingerprint}, {generation.Dimensions}, {generation.IndexPath},
                        {generation.MetadataChecksum}, {generation.VectorCount}, {_timeProvider.GetUtcNow()}, {_timeProvider.GetUtcNow()}
                 WHERE NOT EXISTS
                 (
                     SELECT 1
                     FROM [IndexGenerations] WITH (UPDLOCK, HOLDLOCK)
                     WHERE [Id] = {generation.Id}
                 );
                 """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureMembershipExistsAsync(
        FluxKnowledgeDbContext context,
        Guid generationId,
        IReadOnlyList<CanonicalVector> vectors,
        CancellationToken cancellationToken)
    {
        foreach (var vector in vectors)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO [IndexGenerationVectors] ([GenerationId], [VectorId])
                     SELECT {generationId}, {vector.VectorId}
                     WHERE NOT EXISTS
                     (
                         SELECT 1
                         FROM [IndexGenerationVectors] WITH (UPDLOCK, HOLDLOCK)
                         WHERE [GenerationId] = {generationId} AND [VectorId] = {vector.VectorId}
                     );
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool SameGeneration(
        IndexGenerationEntity actual,
        IndexGenerationDescriptor expected) =>
        string.Equals(actual.ModelFingerprint, expected.ModelFingerprint, StringComparison.Ordinal) &&
        actual.Dimensions == expected.Dimensions &&
        string.Equals(actual.IndexPath, expected.IndexPath, StringComparison.Ordinal) &&
        string.Equals(actual.MetadataChecksum, expected.MetadataChecksum, StringComparison.Ordinal) &&
        actual.VectorCount == expected.VectorCount;

    private static string ComputeSnapshotChecksum(
        string fingerprint,
        int dimensions,
        IReadOnlyList<CanonicalVector> vectors)
    {
        var material = $"{fingerprint}|cos|{dimensions}|{string.Join(',', vectors.Select(vector => $"{vector.VectorId}:{vector.PayloadChecksum}"))}";
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private sealed record ValidatedClaim(
        PipelineRecordEntity PipelineRecord,
        OutboxMessageEntity DispatchMessage);
}
