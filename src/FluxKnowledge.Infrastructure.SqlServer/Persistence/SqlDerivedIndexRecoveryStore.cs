using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlDerivedIndexRecoveryStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : IDerivedIndexRecoveryStore
{
    private const string LockResource = "FluxKnowledge.DerivedIndexRecovery";
    private const string AuditEventType = "derived_index_recovery";
    private const string AuditActor = "DerivedIndexRecoveryService";
    private const int QueryBatchSize = 1_000;

    public async ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var executionContext = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var strategy = executionContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var context = await contextFactory
                    .CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await ReadActiveWithinTransactionAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (TryGetSqlException(exception, out var sqlException))
        {
            throw TranslateSqlException(sqlException);
        }
    }

    private static async Task<DerivedIndexRecoverySqlSnapshot> ReadActiveWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var activeGenerationId = await context.IndexState.AsNoTracking()
            .Where(state => state.Id == 1)
            .Select(state => state.ActiveIndexGenerationId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var generations = await context.IndexGenerations.AsNoTracking()
            .Select(candidate => new GenerationRow(
                candidate.Id,
                candidate.ModelFingerprint,
                candidate.Dimensions,
                candidate.IndexPath,
                candidate.MetadataChecksum,
                candidate.VectorCount,
                candidate.ValidatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var generation = activeGenerationId is { } activeId
            ? generations.Where(candidate => candidate.Id == activeId)
                .Select(candidate => candidate.ToDescriptor())
                .SingleOrDefault()
            : null;
        var membership = activeGenerationId is null
            ? []
            : await (
                    from item in context.IndexGenerationVectors.AsNoTracking()
                    join vector in context.Vectors.AsNoTracking() on item.VectorId equals vector.VectorId
                    where item.GenerationId == activeGenerationId
                    orderby vector.VectorId
                    select new CanonicalVector(
                        vector.VectorId,
                        vector.TextChunkId,
                        vector.ModelFingerprint,
                        vector.Dimensions,
                        vector.Values,
                        vector.TextChunkContentHash,
                        vector.PayloadChecksum,
                        vector.SourceRevision))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var referencedGenerationIds = (await context.Vectors.AsNoTracking()
            .Select(vector => vector.IndexGenerationId)
            .Union(context.IndexGenerationVectors.AsNoTracking().Select(item => item.GenerationId))
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        if (activeGenerationId is { } referencedActiveId)
        {
            referencedGenerationIds.Add(referencedActiveId);
        }
        var recognisedDraftIds = await ReadRecognisedUnplacedDraftIdsAsync(
                context,
                activeGenerationId,
                generations.Where(candidate => candidate.IndexPath == string.Empty).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        referencedGenerationIds.UnionWith(recognisedDraftIds);
        var referencedIndexPaths = generations
            .Where(candidate => candidate.IndexPath != string.Empty)
            .Select(candidate => candidate.IndexPath)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DerivedIndexRecoverySqlSnapshot(
            activeGenerationId,
            generation,
            [.. membership],
            referencedGenerationIds.ToImmutableHashSet(),
            referencedIndexPaths);
    }

    private static async Task<ImmutableHashSet<Guid>> ReadRecognisedUnplacedDraftIdsAsync(
        FluxKnowledgeDbContext context,
        Guid? activeGenerationId,
        IReadOnlyList<GenerationRow> drafts,
        CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
        {
            return ImmutableHashSet<Guid>.Empty;
        }

        var candidateIds = drafts.Select(draft => draft.Id).ToArray();
        var candidateSearchTexts = drafts.Select(draft => draft.Id.ToString("D")).ToArray();
        var membershipIds = new HashSet<Guid>();
        var vectors = new List<DraftVectorReference>();
        var artifacts = new List<DraftArtifactReference>();

        foreach (var candidateBatch in candidateIds.Chunk(QueryBatchSize))
        {
            var ids = candidateBatch.ToArray();
            membershipIds.UnionWith(await context.IndexGenerationVectors.AsNoTracking()
                .Where(item => ids.Contains(item.GenerationId))
                .Select(item => item.GenerationId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
            vectors.AddRange(await context.Vectors.AsNoTracking()
                .Where(vector => ids.Contains(vector.IndexGenerationId))
                .Select(vector => new DraftVectorReference(
                    vector.IndexGenerationId,
                    vector.TextChunkId,
                    vector.ModelFingerprint,
                    vector.Dimensions,
                    vector.SourceRevision,
                    vector.IsDeleted))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        foreach (var searchTextBatch in candidateSearchTexts.Chunk(QueryBatchSize))
        {
            var searchTexts = searchTextBatch.ToArray();
            artifacts.AddRange(await (
                    from artifact in context.Artifacts.AsNoTracking()
                    join record in context.PipelineRecords.AsNoTracking()
                        on artifact.PipelineRecordId equals record.Id
                    where searchTexts.Contains(artifact.SearchText)
                    select new DraftArtifactReference(
                        artifact.Id,
                        artifact.PipelineRecordId,
                        artifact.SourceRevision,
                        artifact.Stage,
                        artifact.ContentHash,
                        artifact.ContentType,
                        artifact.SearchText,
                        record.CurrentStage,
                        record.Revision))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        var recordIds = artifacts.Select(artifact => artifact.PipelineRecordId).Distinct().ToArray();
        var jobs = new List<DraftJobReference>();
        var outbox = new List<DraftOutboxReference>();
        var chunks = new List<DraftCanonicalChunkReference>();
        foreach (var recordBatch in recordIds.Chunk(QueryBatchSize))
        {
            var ids = recordBatch.ToArray();
            jobs.AddRange(await context.Jobs.AsNoTracking()
                .Where(job => ids.Contains(job.PipelineRecordId))
                .Select(job => new DraftJobReference(
                    job.PipelineRecordId,
                    job.SourceRevision,
                    job.Stage,
                    job.Operation,
                    job.PublicState))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
            outbox.AddRange(await context.OutboxMessages.AsNoTracking()
                .Where(message => ids.Contains(message.PipelineRecordId))
                .Select(message => new DraftOutboxReference(
                    message.PipelineRecordId,
                    message.SourceRevision,
                    message.Stage,
                    message.Operation,
                    message.DispatchGeneration,
                    message.DispatchedAtUtc))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
            chunks.AddRange(await (
                    from chunk in context.TextChunks.AsNoTracking()
                    join artifact in context.Artifacts.AsNoTracking()
                        on chunk.ArtifactId equals artifact.Id
                    where ids.Contains(artifact.PipelineRecordId)
                    select new DraftCanonicalChunkReference(
                        chunk.Id,
                        chunk.SourceRevision,
                        artifact.PipelineRecordId,
                        artifact.SourceRevision,
                        artifact.Stage))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        var recognised = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var draft in drafts)
        {
            if (!IsRecognisedUnplacedDraft(
                    draft,
                    activeGenerationId,
                    membershipIds,
                    vectors,
                    artifacts,
                    jobs,
                    outbox,
                    chunks))
            {
                throw new InvalidOperationException(
                    "An unplaced index generation draft has inconsistent durable pipeline provenance.");
            }

            recognised.Add(draft.Id);
        }

        return recognised.ToImmutable();
    }

    private static bool IsRecognisedUnplacedDraft(
        GenerationRow draft,
        Guid? activeGenerationId,
        ISet<Guid> membershipIds,
        IReadOnlyList<DraftVectorReference> vectors,
        IReadOnlyList<DraftArtifactReference> artifacts,
        IReadOnlyList<DraftJobReference> jobs,
        IReadOnlyList<DraftOutboxReference> outbox,
        IReadOnlyList<DraftCanonicalChunkReference> chunks)
    {
        if (activeGenerationId == draft.Id ||
            draft.IndexPath != string.Empty ||
            draft.ValidatedAtUtc is not null ||
            !string.Equals(draft.MetadataChecksum, EmbedDraftDefaults.MetadataChecksum, StringComparison.Ordinal) ||
            membershipIds.Contains(draft.Id))
        {
            return false;
        }

        var expectedSearchText = draft.Id.ToString("D");
        var matchingArtifacts = artifacts
            .Where(artifact => string.Equals(artifact.SearchText, expectedSearchText, StringComparison.Ordinal))
            .ToArray();
        if (matchingArtifacts.Length != 1)
        {
            return false;
        }

        var embedArtifact = matchingArtifacts[0];
        if (embedArtifact.Stage != (int)PipelineStage.Embed ||
            !string.Equals(embedArtifact.ContentType, EmbedDraftDefaults.ArtifactContentType, StringComparison.Ordinal) ||
            embedArtifact.CurrentStage != (int)PipelineStage.Publish ||
            embedArtifact.PipelineRecordRevision != embedArtifact.SourceRevision ||
            !HasExpectedLifecycle(embedArtifact, jobs, outbox))
        {
            return false;
        }

        var draftVectors = vectors.Where(vector => vector.GenerationId == draft.Id).ToArray();
        if (draft.VectorCount != draftVectors.LongLength)
        {
            return false;
        }

        var canonicalChunks = chunks
            .Where(chunk =>
                chunk.PipelineRecordId == embedArtifact.PipelineRecordId &&
                chunk.ArtifactSourceRevision == embedArtifact.SourceRevision &&
                chunk.SourceRevision == embedArtifact.SourceRevision &&
                chunk.ArtifactStage == (int)PipelineStage.CanonicalIndex)
            .ToArray();
        if (draft.VectorCount == 0)
        {
            return string.Equals(draft.ModelFingerprint, EmbedDraftDefaults.ModelFingerprint, StringComparison.Ordinal) &&
                   draft.Dimensions == EmbedDraftDefaults.Dimensions &&
                   draftVectors.Length == 0 &&
                   canonicalChunks.Length == 0 &&
                   string.Equals(embedArtifact.ContentHash, EmbedDraftDefaults.EmptyArtifactContentHash,
                       StringComparison.Ordinal);
        }

        return draftVectors.All(vector =>
            !vector.IsDeleted &&
            string.Equals(vector.ModelFingerprint, draft.ModelFingerprint, StringComparison.Ordinal) &&
            vector.Dimensions == draft.Dimensions &&
            vector.SourceRevision == embedArtifact.SourceRevision &&
            canonicalChunks.Any(chunk =>
                chunk.Id == vector.TextChunkId &&
                chunk.SourceRevision == vector.SourceRevision));
    }

    private static bool HasExpectedLifecycle(
        DraftArtifactReference embedArtifact,
        IReadOnlyList<DraftJobReference> jobs,
        IReadOnlyList<DraftOutboxReference> outbox)
    {
        var recordJobs = jobs.Where(job =>
            job.PipelineRecordId == embedArtifact.PipelineRecordId &&
            job.SourceRevision == embedArtifact.SourceRevision).ToArray();
        var embedJobs = recordJobs.Where(job => job.Stage == (int)PipelineStage.Embed).ToArray();
        var publishJobs = recordJobs.Where(job => job.Stage == (int)PipelineStage.Publish).ToArray();
        if (embedJobs.Length != 1 ||
            !string.Equals(embedJobs[0].Operation, PipelineOperations.Embed, StringComparison.Ordinal) ||
            embedJobs[0].PublicState != (int)PublicJobState.Completed ||
            publishJobs.Length != 1 ||
            !string.Equals(publishJobs[0].Operation, PipelineOperations.Publish, StringComparison.Ordinal) ||
            !IsValidPublishState(publishJobs[0].PublicState))
        {
            return false;
        }

        var recordOutbox = outbox.Where(message =>
            message.PipelineRecordId == embedArtifact.PipelineRecordId &&
            message.SourceRevision == embedArtifact.SourceRevision).ToArray();
        var embedOutbox = recordOutbox.Where(message => message.Stage == (int)PipelineStage.Embed).ToArray();
        var publishOutbox = recordOutbox.Where(message => message.Stage == (int)PipelineStage.Publish).ToArray();
        if (embedOutbox.Length != 1 ||
            !string.Equals(embedOutbox[0].Operation, PipelineOperations.Embed, StringComparison.Ordinal) ||
            publishOutbox.Length != 1 ||
            !string.Equals(publishOutbox[0].Operation, PipelineOperations.Publish, StringComparison.Ordinal) ||
            embedOutbox[0].DispatchedAtUtc is null ||
            !HasExpectedPublishDispatch(publishJobs[0], publishOutbox[0]))
        {
            return false;
        }

        return embedOutbox[0].DispatchGeneration >= 0 &&
               publishOutbox[0].DispatchGeneration >= 0 &&
               embedOutbox[0].DispatchGeneration < long.MaxValue &&
                publishOutbox[0].DispatchGeneration == embedOutbox[0].DispatchGeneration + 1;
    }

    private static bool HasExpectedPublishDispatch(
        DraftJobReference publishJob,
        DraftOutboxReference publishOutbox) =>
        publishJob.PublicState switch
        {
            (int)PublicJobState.WorkerQueued or (int)PublicJobState.WorkerProcessing =>
                publishOutbox.DispatchedAtUtc is null,
            (int)PublicJobState.Completed or (int)PublicJobState.Failed =>
                publishOutbox.DispatchedAtUtc is not null,
            _ => false
        };

    private static bool IsValidPublishState(int publicState) =>
        publicState == (int)PublicJobState.WorkerQueued ||
        publicState == (int)PublicJobState.WorkerProcessing ||
        publicState == (int)PublicJobState.Completed ||
        publicState == (int)PublicJobState.Failed;

    public async ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        if (lockTimeout < TimeSpan.Zero || lockTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        string connectionString;
        await using (var context = await contextFactory
                         .CreateDbContextAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The recovery SQL connection string is unavailable.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(
                """
                DECLARE @result int;
                EXEC @result = sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = @lockTimeout;
                SELECT @result;
                """,
                connection);
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
            command.Parameters.Add("@lockTimeout", SqlDbType.Int).Value = (int)Math.Ceiling(lockTimeout.TotalMilliseconds);
            var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? -999);
            if (result == -1)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            if (result == -2 && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"SQL application-lock acquisition failed with result code {result}.");
            }

            return new SqlDerivedIndexRecoveryLease(connection);
        }
        catch (SqlException) when (cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SqlException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw TranslateSqlException(exception);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> TryUpdateRecoveryPathAsync(
        Guid expectedActiveGenerationId,
        string expectedIndexPath,
        string replacementIndexPath,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIndexPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementIndexPath);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE generation
                SET IndexPath = {replacementIndexPath}, ValidatedAtUtc = {validatedAtUtc}
                FROM IndexGenerations AS generation
                INNER JOIN IndexState AS state ON state.Id = 1
                WHERE generation.Id = {expectedActiveGenerationId}
                  AND state.ActiveIndexGenerationId = {expectedActiveGenerationId}
                  AND generation.IndexPath = {expectedIndexPath};
                """, cancellationToken)
                .ConfigureAwait(false);
            return updated == 1;
        }
        catch (Exception exception) when (TryGetSqlException(exception, out var sqlException))
        {
            throw TranslateSqlException(sqlException);
        }
    }

    public async ValueTask AppendAuditAsync(
        DerivedIndexRecoveryAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        try
        {
            await using var context = await contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            context.AuditEvents.Add(new AuditEventEntity
            {
                PipelineRecordId = null,
                EventType = AuditEventType,
                Actor = AuditActor,
                DetailsJson = CreateAuditDetails(auditEvent),
                OccurredAtUtc = timeProvider.GetUtcNow()
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TryGetSqlException(exception, out var sqlException))
        {
            throw TranslateSqlException(sqlException);
        }
    }

    private static bool TryGetSqlException(Exception exception, out SqlException sqlException)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException found)
            {
                sqlException = found;
                return true;
            }
        }
        sqlException = null!;
        return false;
    }

    private static Exception TranslateSqlException(SqlException exception) => exception.Number switch
    {
        207 or 208 or 2812 => new DerivedIndexRecoverySqlSchemaException(exception),
        229 or 230 or 916 or 18456 => new DerivedIndexRecoverySqlPermissionException(exception),
        4060 => new DerivedIndexRecoverySqlConfigurationException(exception),
        _ => exception
    };

    private static string CreateAuditDetails(DerivedIndexRecoveryAuditEvent auditEvent) =>
        JsonSerializer.Serialize(new
        {
            category = SanitizeCategory(auditEvent.EventType),
            activeGenerationId = auditEvent.ActiveGenerationId?.ToString("D"),
            failureCategory = auditEvent.FailureCategory?.ToString(),
            attemptCount = Math.Clamp(auditEvent.AttemptCount, 0, 100_000),
            elapsedMilliseconds = Math.Clamp((long)auditEvent.Elapsed.TotalMilliseconds, 0, 86_400_000),
            nextRetryAtUtc = auditEvent.NextRetryAtUtc,
            cleanedCandidateCount = Math.Clamp(auditEvent.CleanedCandidateCount, 0, 1_000_000)
        });

    private static string SanitizeCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (category.Length > 64 ||
            !char.IsAsciiLetter(category[0]) ||
            category.Any(character => char.IsAsciiLetterOrDigit(character) is false && character != '_') ||
            category.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown";
        }

        return category.ToLowerInvariant();
    }

    private sealed record GenerationRow(
        Guid Id,
        string ModelFingerprint,
        int Dimensions,
        string IndexPath,
        string MetadataChecksum,
        long VectorCount,
        DateTimeOffset? ValidatedAtUtc)
    {
        public IndexGenerationDescriptor ToDescriptor() =>
            new(Id, ModelFingerprint, Dimensions, IndexPath, MetadataChecksum, VectorCount);
    }

    private sealed record DraftVectorReference(
        Guid GenerationId,
        long TextChunkId,
        string ModelFingerprint,
        int Dimensions,
        long SourceRevision,
        bool IsDeleted);

    private sealed record DraftArtifactReference(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string ContentHash,
        string ContentType,
        string SearchText,
        int CurrentStage,
        long PipelineRecordRevision);

    private sealed record DraftJobReference(
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string Operation,
        int PublicState);

    private sealed record DraftOutboxReference(
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string Operation,
        long DispatchGeneration,
        DateTimeOffset? DispatchedAtUtc);

    private sealed record DraftCanonicalChunkReference(
        long Id,
        long SourceRevision,
        Guid PipelineRecordId,
        long ArtifactSourceRevision,
        int ArtifactStage);

    private sealed class SqlDerivedIndexRecoveryLease(SqlConnection connection)
        : IDerivedIndexRecoveryLease
    {
        private SqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            var heldConnection = Interlocked.Exchange(ref _connection, null);
            if (heldConnection is null)
            {
                return;
            }

            try
            {
                await using var command = new SqlCommand(
                    """
                    EXEC sp_releaseapplock
                        @Resource = @resource,
                        @LockOwner = 'Session';
                    """,
                    heldConnection);
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await heldConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
