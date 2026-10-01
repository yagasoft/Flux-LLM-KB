using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Eligibility and evidence for one explicitly confirmed terminal Publish recovery.</summary>
internal sealed record SqlPublicationRetry(JobEntity Job, OutboxMessageEntity Dispatch, PipelineRecordEntity Record,
    SourceRevisionEntity Source, SourceRootConfigurationEntity Root, SourceActivityEntity Activity,
    IReadOnlyList<NativeTargetVersion> Targets)
{
    internal static async Task<SqlPublicationRetry> ReadEligibleAsync(FluxKnowledgeDbContext context, Guid jobId, bool forCommit, CancellationToken ct)
    {
        // Commit already holds the publication fence. Lock the delivery before the
        // job, matching failure/claim mutation order; range locks reject new competitors.
        var dispatches = forCommit
            ? await context.OutboxMessages.FromSqlInterpolated($"SELECT * FROM [OutboxMessages] WITH (UPDLOCK,HOLDLOCK) WHERE [JobId]={jobId}").ToArrayAsync(ct)
            : await context.OutboxMessages.AsNoTracking().Where(value => value.JobId == jobId).ToArrayAsync(ct);
        var job = forCommit
            ? await context.Jobs.FromSqlInterpolated($"SELECT * FROM [Jobs] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={jobId}").SingleOrDefaultAsync(ct)
            : await context.Jobs.AsNoTracking().SingleOrDefaultAsync(value => value.Id == jobId, ct);
        if (job is null) throw new NativeOperationException("target-not-found");
        if (dispatches.Length != 1 || job.PublicState != (int)PublicJobState.Failed || job.Stage != (int)PipelineStage.Publish ||
            job.Operation != PipelineOperations.Publish || job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null ||
            job.LeaseGeneration == long.MaxValue) Refuse();
        var dispatch = dispatches[0];
        if (dispatch.PipelineRecordId != job.PipelineRecordId || dispatch.SourceRevision != job.SourceRevision || dispatch.Stage != job.Stage ||
            dispatch.Operation != job.Operation || dispatch.DispatchedAtUtc is null || dispatch.CompletedArtifactId is not null ||
            dispatch.LeaseOwner is not null || dispatch.LeaseExpiresAtUtc is not null || dispatch.LeaseGeneration == long.MaxValue) Refuse();
        var record = await context.PipelineRecords.SingleOrDefaultAsync(value => value.Id == job.PipelineRecordId, ct);
        if (record is null || record.Revision != job.SourceRevision || record.IsDeleted || record.CompletionCriteriaMet ||
            record.CurrentStage != (int)PipelineStage.Publish || record.SourceRevisionId is null) Refuse();
        var source = await context.SourceRevisions.SingleOrDefaultAsync(value => value.Id == record!.SourceRevisionId, ct);
        if (source is null || source.SuppressedAtUtc is not null || source.ParentSourceRevisionId is not null || source.OriginKind != 0) Refuse();
        var root = await context.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == source!.SourceRootId, ct);
        if (root is null || root.State != (int)SourceRootState.Enabled) Refuse();
        if (await context.SourceRevisions.AnyAsync(value => value.Id != source!.Id && value.SourceRootId == source.SourceRootId &&
                value.StableSourceIdentity == source.StableSourceIdentity && value.SuppressedAtUtc == null, ct) ||
            await context.Jobs.AnyAsync(value => value.PipelineRecordId == record!.Id && value.SourceRevision == record.Revision && value.Id != job.Id &&
                value.PublicState != (int)PublicJobState.Completed && value.PublicState != (int)PublicJobState.Failed, ct) ||
            await context.OutboxMessages.AnyAsync(value => value.PipelineRecordId == record!.Id && value.SourceRevision == record.Revision &&
                value.Id != dispatch.Id && value.DispatchedAtUtc == null, ct) ||
            await context.Artifacts.AnyAsync(value => value.PipelineRecordId == record!.Id && value.SourceRevision == record.Revision && value.Stage == (int)PipelineStage.Publish, ct) ||
            await context.DocumentPublications.AnyAsync(value => value.PipelineRecordId == record!.Id && value.PipelineRecordRevision == record.Revision, ct)) Refuse();
        var state = await context.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, ct);
        if (state.CorpusRebuildOperationId is not null ||
            await context.CorpusRebuildSupersededJobs.AnyAsync(value => value.JobId == jobId, ct)) Refuse();
        var activities = await context.SourceActivities.Where(value => value.SourceRevisionId == source!.Id &&
            value.ActivityKind == (int)SourceActivityKind.TextExtraction && value.ResultingPipelineRecordId == record!.Id &&
            value.ResultingPipelineRecordRevision == record.Revision).ToArrayAsync(ct);
        if (activities.Length != 1 || activities[0].State != (int)SourceActivityState.FailedTerminal) Refuse();
        var activity = activities[0];
        var artifacts = await context.Artifacts.AsNoTracking().Where(value => value.PipelineRecordId == record!.Id &&
            value.SourceRevision == record.Revision && (value.Stage == (int)PipelineStage.Embed || value.Stage == (int)PipelineStage.CanonicalIndex)).ToArrayAsync(ct);
        var embed = artifacts.SingleOrDefault(value => value.Stage == (int)PipelineStage.Embed);
        var canonical = artifacts.SingleOrDefault(value => value.Stage == (int)PipelineStage.CanonicalIndex);
        if (embed is null || canonical is null || embed.ContentType != EmbedDraftDefaults.ArtifactContentType || !Guid.TryParse(embed.SearchText, out _)) Refuse();
        var generationId = Guid.Parse(embed!.SearchText);
        var generation = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == generationId, ct);
        if (generation is null || generation.RetiredAtUtc is not null || generation.Dimensions < 1 ||
            (generation.CorpusEpoch is not null && generation.CorpusEpoch != state.CorpusEpoch)) Refuse();
        var embedJobs = await context.Jobs.AsNoTracking().Where(value => value.PipelineRecordId == record!.Id && value.SourceRevision == record.Revision &&
            value.Stage == (int)PipelineStage.Embed && value.Operation == PipelineOperations.Embed && value.PublicState == (int)PublicJobState.Completed).ToArrayAsync(ct);
        if (embedJobs.Length != 1 || (generation!.EmbeddingJobId is not null && generation.EmbeddingJobId != embedJobs[0].Id)) Refuse();
        if (generation.EmbeddingJobId is not null)
        {
            try { SqlEmbeddingCheckpointStore.ValidateDraft(generation, embedJobs[0].Id, new(generation.ModelFingerprint, generation.Dimensions), state.CorpusEpoch); }
            catch (InvalidOperationException) { Refuse(); }
        }
        else if (generation.IndexPath.Length != 0 || generation.ValidatedAtUtc is not null ||
                 generation.MetadataChecksum != EmbedDraftDefaults.MetadataChecksum) Refuse();
        var embedDeliveries = await context.OutboxMessages.AsNoTracking().Where(value => value.JobId == embedJobs[0].Id &&
            value.CompletedArtifactId == embed.Id && value.DispatchedAtUtc != null && value.PipelineRecordId == record.Id &&
            value.SourceRevision == record.Revision && value.Stage == (int)PipelineStage.Embed && value.Operation == PipelineOperations.Embed).ToArrayAsync(ct);
        if (embedDeliveries.Length != 1) Refuse();
        var vectorBinding = await ValidateVectorsAsync(context, record!.Id, record.Revision, generation, embed.ContentHash, ct);
        var targets = new List<NativeTargetVersion>
        {
            Version("publication-job", job.Id, job.RowVersion), Version("publication-dispatch", dispatch.Id, dispatch.RowVersion),
            Version("record", record.Id, record.RowVersion), Version("source", source!.Id, source.RowVersion),
            Version("root", root!.Id, root.RowVersion), Version("activity", activity.Id, activity.RowVersion),
            Version("generation", generation.Id, generation.RowVersion), Version("embed-job", embedJobs[0].Id, embedJobs[0].RowVersion),
            Version("embed-delivery", embedDeliveries[0].Id, embedDeliveries[0].RowVersion),
            new($"embed-artifact:{embed.Id:D}", Hash(embed)), new($"canonical-artifact:{canonical!.Id:D}", Hash(canonical)),
            new($"publication-inputs:{job.Id:D}", vectorBinding), new("publication-epoch", state.CorpusEpoch.ToString("D"))
        };
        return new(job, dispatch, record, source, root, activity, targets);
    }

    private static async Task<string> ValidateVectorsAsync(FluxKnowledgeDbContext context, Guid recordId, long revision,
        IndexGenerationEntity generation, string embedChecksum, CancellationToken ct)
    {
        var chunks = SqlEmbeddingCheckpointStore.Chunks(context, recordId, revision);
        if (await chunks.LongCountAsync(ct) != generation.VectorCount ||
            await context.Vectors.LongCountAsync(value => value.IndexGenerationId == generation.Id, ct) != generation.VectorCount ||
            await context.Vectors.AnyAsync(value => value.IndexGenerationId == generation.Id &&
                (value.IsDeleted || value.SourceRevision != revision || value.ModelFingerprint != generation.ModelFingerprint || value.Dimensions != generation.Dimensions ||
                 !chunks.Any(chunk => chunk.Id == value.TextChunkId && chunk.ContentHash == value.TextChunkContentHash &&
                    (value.SearchInputHash == null || chunk.SearchInputHash == value.SearchInputHash))), ct)) Refuse();
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lastOrdinal = -1;
        var lastChunk = 0L;
        while (true)
        {
            // Reuse the checkpoint paging size; never buffer all vector payloads.
            var page = await context.Vectors.AsNoTracking().Include(value => value.TextChunk)
                .Where(value => value.IndexGenerationId == generation.Id && (value.TextChunk.Ordinal > lastOrdinal ||
                    (value.TextChunk.Ordinal == lastOrdinal && value.TextChunkId > lastChunk)))
                .OrderBy(value => value.TextChunk.Ordinal).ThenBy(value => value.TextChunkId).Take(128).ToArrayAsync(ct);
            if (page.Length == 0) break;
            foreach (var vector in page)
            {
                var chunk = vector.TextChunk;
                var currentSearchHash = new CanonicalTextChunk(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length, chunk.Content,
                    chunk.ContentHash, chunk.PassagePolicyFingerprint, chunk.ContextHeader).SearchInputHash;
                if (vector.Values.LongLength != (long)generation.Dimensions * sizeof(float) ||
                    vector.ModelFingerprint != generation.ModelFingerprint || vector.TextChunkContentHash != chunk.ContentHash ||
                    Convert.ToHexStringLower(SHA256.HashData(vector.Values)) != vector.PayloadChecksum ||
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Content))) != chunk.ContentHash ||
                    currentSearchHash != chunk.SearchInputHash ||
                    (vector.SearchInputHash is not null && vector.SearchInputHash != currentSearchHash) ||
                    (generation.EmbeddingJobId is not null && vector.SearchInputHash is null)) Refuse();
                var values = new float[generation.Dimensions];
                Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
                if (values.Any(value => !float.IsFinite(value)) || Math.Abs(values.Sum(value => (double)value * value) - 1) > 0.001) Refuse();
                payloadHash.AppendData(vector.Values);
                identityHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { vector.VectorId, vector.TextChunkId, vector.RowVersion,
                    vector.PayloadChecksum, vector.TextChunkContentHash, vector.SearchInputHash, chunk = new { vector.TextChunk.Ordinal,
                        vector.TextChunk.SourceRevision, vector.TextChunk.ContentHash, vector.TextChunk.SearchInputHash,
                        vector.TextChunk.StartOffset, vector.TextChunk.Length, vector.TextChunk.PassagePolicyFingerprint, vector.TextChunk.ContextHeader } }));
            }
            lastOrdinal = page[^1].TextChunk.Ordinal;
            lastChunk = page[^1].TextChunkId;
        }
        if (Convert.ToHexStringLower(payloadHash.GetHashAndReset()) != embedChecksum) Refuse();
        return Convert.ToHexStringLower(identityHash.GetHashAndReset());
    }

    internal void Requeue(FluxKnowledgeDbContext context, DateTimeOffset now, string actor, string idempotencyKey, string requestFingerprint)
    {
        // Capture the old evidence before changing any current state. This audit and
        // the native confirmation receipt commit atomically with the same scheduling.
        context.AuditEvents.Add(new AuditEventEntity
        {
            PipelineRecordId = Record.Id, SourceRootId = Root.Id, SourceRevisionId = Source.Id, SourceActivityId = Activity.Id,
            EventType = "publication recovery queued", EventFamily = "pipeline", Severity = "information", Actor = actor, OccurredAtUtc = now,
            CorrelationId = $"native:{requestFingerprint}",
            DetailsJson = JsonSerializer.Serialize(new { idempotencyKey, requestFingerprint, jobId = Job.Id, dispatchId = Dispatch.Id,
                Job.SourceRevision, Job.Reason, Job.ErrorDetails, Job.AttemptCount, jobLeaseGeneration = Job.LeaseGeneration,
                Dispatch.DispatchedAtUtc, Dispatch.CompletedArtifactId, Dispatch.DispatchGeneration, dispatchIdempotencyKey = Dispatch.IdempotencyKey,
                dispatchLeaseGeneration = Dispatch.LeaseGeneration,
                activity = new { Activity.Id, Activity.State, Activity.Reason, Activity.AttemptCount, Activity.AttemptEvidenceJson,
                    Activity.ResultingPipelineRecordId, Activity.ResultingPipelineRecordRevision }, targets = Targets })
        });
        Job.LeaseGeneration = checked(Job.LeaseGeneration + 1);
        Dispatch.LeaseGeneration = checked(Dispatch.LeaseGeneration + 1);
        Job.PublicState = (int)PublicJobState.WorkerQueued;
        Job.DueAtUtc = now;
        Job.Reason = "explicit-publication-recovery";
        Job.ErrorDetails = null;
        Dispatch.DispatchedAtUtc = null;
        Dispatch.DueAtUtc = now;
        Activity.State = (int)SourceActivityState.FailedRetryable;
        Activity.Reason = "explicit-publication-recovery";
        Activity.UpdatedAtUtc = now;
    }

    private static NativeTargetVersion Version(string prefix, Guid id, byte[] version) => version.Length == 8
        ? new($"{prefix}:{id:D}", Convert.ToBase64String(version)) : throw new NativeOperationException("operation-fenced");
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse() => throw new NativeOperationException("publication-retry-not-eligible");
}
