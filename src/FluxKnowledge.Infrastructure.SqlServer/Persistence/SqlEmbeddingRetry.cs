using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Explicit terminal Embed recovery preserves checkpoint and delivery identities.</summary>
internal sealed record SqlEmbeddingRetry(JobEntity Job, OutboxMessageEntity Dispatch, PipelineRecordEntity Record,
    SourceRevisionEntity Source, SourceRootConfigurationEntity Root, SourceActivityEntity Activity,
    IReadOnlyList<NativeTargetVersion> Targets)
{
    internal static async Task<SqlEmbeddingRetry> ReadEligibleAsync(FluxKnowledgeDbContext context, Guid jobId,
        EmbeddingGpuRuntime? runtime, bool forCommit, CancellationToken ct)
    {
        if (runtime is null) Refuse();
        // The commit owns the publication fence. Match ordinary delivery/job lock order.
        var dispatches = forCommit
            ? await context.OutboxMessages.FromSqlInterpolated($"SELECT * FROM [OutboxMessages] WITH (UPDLOCK,HOLDLOCK) WHERE [JobId]={jobId}").ToArrayAsync(ct)
            : await context.OutboxMessages.AsNoTracking().Where(value => value.JobId == jobId).ToArrayAsync(ct);
        var job = forCommit
            ? await context.Jobs.FromSqlInterpolated($"SELECT * FROM [Jobs] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={jobId}").SingleOrDefaultAsync(ct)
            : await context.Jobs.AsNoTracking().SingleOrDefaultAsync(value => value.Id == jobId, ct);
        if (job is null) throw new NativeOperationException("target-not-found");
        if (dispatches.Length != 1 || job.Stage != (int)PipelineStage.Embed || job.Operation != PipelineOperations.Embed ||
            job.PublicState != (int)PublicJobState.Failed || job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null ||
            job.LeaseGeneration == long.MaxValue) Refuse();
        var dispatch = dispatches[0];
        if (dispatch.PipelineRecordId != job.PipelineRecordId || dispatch.SourceRevision != job.SourceRevision ||
            dispatch.Stage != job.Stage || dispatch.Operation != job.Operation || dispatch.DispatchedAtUtc is null ||
            dispatch.CompletedArtifactId is not null || dispatch.LeaseOwner is not null || dispatch.LeaseExpiresAtUtc is not null ||
            dispatch.LeaseGeneration == long.MaxValue) Refuse();
        var record = await context.PipelineRecords.SingleOrDefaultAsync(value => value.Id == job.PipelineRecordId, ct);
        if (record is null || record.Revision != job.SourceRevision || record.IsDeleted || record.CompletionCriteriaMet ||
            record.CurrentStage != (int)PipelineStage.Embed || record.SourceRevisionId is null) Refuse();
        var source = await context.SourceRevisions.SingleOrDefaultAsync(value => value.Id == record.SourceRevisionId, ct);
        if (source is null || source.SuppressedAtUtc is not null || source.ParentSourceRevisionId is not null ||
            source.OriginKind != 0 || source.ContentSha256 != record.ContentHash) Refuse();
        var root = await context.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == source.SourceRootId, ct);
        if (root is null || root.State != (int)SourceRootState.Enabled) Refuse();
        var state = await context.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, ct);
        if (state.CorpusRebuildOperationId is not null || await context.CorpusRebuildSupersededJobs.AnyAsync(value => value.JobId == jobId, ct) ||
            await context.SourceRevisions.AnyAsync(value => value.Id != source.Id && value.SourceRootId == source.SourceRootId &&
                value.StableSourceIdentity == source.StableSourceIdentity && value.SuppressedAtUtc == null, ct) ||
            await context.Jobs.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision && value.Id != jobId &&
                value.PublicState != (int)PublicJobState.Completed && value.PublicState != (int)PublicJobState.Failed, ct) ||
            await context.OutboxMessages.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision &&
                value.Id != dispatch.Id && value.DispatchedAtUtc == null, ct) ||
            await context.Artifacts.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision &&
                value.Stage >= (int)PipelineStage.Embed, ct) ||
            await context.DocumentPublications.AnyAsync(value => value.PipelineRecordId == record.Id && value.PipelineRecordRevision == record.Revision, ct)) Refuse();
        var activities = await context.SourceActivities.Where(value => value.SourceRevisionId == source.Id &&
            value.ActivityKind == (int)SourceActivityKind.TextExtraction && value.ResultingPipelineRecordId == record.Id &&
            value.ResultingPipelineRecordRevision == record.Revision).ToArrayAsync(ct);
        if (activities.Length != 1 || activities[0].State != (int)SourceActivityState.FailedTerminal) Refuse();
        var activity = activities[0];
        var canonical = await context.Artifacts.AsNoTracking().SingleOrDefaultAsync(value => value.PipelineRecordId == record.Id &&
            value.SourceRevision == record.Revision && value.Stage == (int)PipelineStage.CanonicalIndex, ct);
        var draft = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.EmbeddingJobId == jobId, ct);
        if (canonical is null || canonical.SearchText is null || draft is null || state.ActiveIndexGenerationId == draft.Id ||
            CodeDisclosureIntegrity.Hash(canonical.SearchText) != canonical.ContentHash) Refuse();
        string vectors;
        try
        {
            SqlEmbeddingCheckpointStore.ValidateDraft(draft, jobId, runtime.Profile, state.CorpusEpoch);
            vectors = await SqlEmbeddingCheckpointStore.ReadCheckpointChecksumAsync(context, draft, record.Id, record.Revision,
                requireComplete: false, ct);
        }
        catch (InvalidOperationException) { Refuse(); throw; }
        var inputs = await ReadInputBindingAsync(context, record, canonical, draft, ct);
        var gpu = await ReadSettledGpuBindingAsync(context, job, draft, runtime, ct);
        return new(job, dispatch, record, source, root, activity,
        [
            Version("embedding-job", job.Id, job.RowVersion), Version("embedding-dispatch", dispatch.Id, dispatch.RowVersion),
            Version("record", record.Id, record.RowVersion), Version("source", source.Id, source.RowVersion),
            Version("root", root.Id, root.RowVersion), Version("activity", activity.Id, activity.RowVersion),
            Version("generation", draft.Id, draft.RowVersion), new($"canonical-artifact:{canonical.Id:D}", Hash(canonical)),
            new($"embedding-inputs:{jobId:D}", Hash(new { inputs, vectors })), new($"embedding-gpu:{jobId:D}", gpu),
            new("embedding-epoch-profile", Hash(new { state.CorpusEpoch, runtime }))
        ]);
    }

    internal static async Task<string> ReadInputBindingAsync(FluxKnowledgeDbContext context, PipelineRecordEntity record,
        ArtifactEntity canonical, IndexGenerationEntity draft, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long lastId = 0;
        while (true)
        {
            var page = await SqlEmbeddingCheckpointStore.Chunks(context, record.Id, record.Revision).AsNoTracking()
                .Where(value => value.Id > lastId).OrderBy(value => value.Id).Take(128).ToArrayAsync(ct);
            if (page.Length == 0) break;
            foreach (var chunk in page)
            {
                var input = new CanonicalTextChunk(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length,
                    chunk.Content, chunk.ContentHash, chunk.PassagePolicyFingerprint, chunk.ContextHeader);
                if (chunk.ArtifactId != canonical.Id || chunk.Length != chunk.Content.Length || chunk.StartOffset < 0 ||
                    (long)chunk.StartOffset + chunk.Length > canonical.SearchText.Length ||
                    !canonical.SearchText.AsSpan(chunk.StartOffset, chunk.Length).SequenceEqual(chunk.Content.AsSpan()) ||
                    CodeDisclosureIntegrity.Hash(chunk.Content) != chunk.ContentHash || input.SearchInputHash != chunk.SearchInputHash) Refuse();
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(input));
            }
            lastId = page[^1].Id;
        }
        long lastVector = 0;
        while (true)
        {
            var page = await context.Vectors.AsNoTracking().Where(value => value.IndexGenerationId == draft.Id && value.VectorId > lastVector)
                .OrderBy(value => value.VectorId).Take(128).Select(value => new { value.VectorId, value.TextChunkId, value.RowVersion,
                    value.PayloadChecksum, value.SearchInputHash, value.TextChunkContentHash }).ToArrayAsync(ct);
            if (page.Length == 0) break;
            foreach (var vector in page) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(vector));
            lastVector = page[^1].VectorId;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record GpuInput(long Id, string Hash);
    internal static async Task<string> ReadSettledGpuBindingAsync(FluxKnowledgeDbContext context, JobEntity job,
        IndexGenerationEntity draft, EmbeddingGpuRuntime runtime, CancellationToken ct)
    {
        var requests = await context.EmbeddingGpuRequests.AsNoTracking().Where(value => value.ParentJobId == job.Id)
            .OrderBy(value => value.MiniTaskId).ToArrayAsync(ct);
        var tasks = await context.GpuMiniTasks.AsNoTracking().Where(value => value.ParentJobId == job.Id)
            .OrderBy(value => value.Id).ToArrayAsync(ct);
        if (requests.Length != tasks.Length || requests.Any(value => !tasks.Any(task => task.Id == value.MiniTaskId))) Refuse();
        using var binding = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var request in requests)
        {
            var task = tasks.Single(value => value.Id == request.MiniTaskId);
            if (request.PipelineRecordId != job.PipelineRecordId || request.SourceRevision != job.SourceRevision ||
                request.GenerationId != draft.Id || request.CorpusEpoch != draft.CorpusEpoch || request.ModelFingerprint != runtime.Profile.ModelFingerprint ||
                request.Dimensions != runtime.Profile.Dimensions || request.State != 2 || !request.NativeCleanupConfirmed ||
                request.CleanupConfirmedAtUtc is null || request.ResultDigest is not { Length: 32 } ||
                request.ExecutorInstanceId is null || request.ClaimOperationId is null || request.OwnerProcessId is null ||
                request.OwnerStartedAtUtc is null || string.IsNullOrEmpty(request.OwnerMachineFingerprint) ||
                task.SourceRevision != job.SourceRevision || task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed || task.BatchId is null ||
                task.PriorityLane != (int)GpuPriorityLane.DocumentIndexing || task.ModelRuntimeKey != runtime.RuntimeKey ||
                task.SettingsFingerprint != runtime.SettingsFingerprint || CodeDisclosureIntegrity.Hash(request.InputsJson) != request.InputDigest) Refuse();
            GpuInput[] inputs;
            try { inputs = JsonSerializer.Deserialize<GpuInput[]>(request.InputsJson) ?? []; }
            catch (JsonException) { Refuse(); throw; }
            if (inputs.Length is < 1 or > 4 || inputs.Select(value => value.Id).Distinct().Count() != inputs.Length) Refuse();
            using var resultHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var input in inputs)
            {
                var payload = await (from chunk in SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision)
                    join vector in context.Vectors on chunk.Id equals vector.TextChunkId
                    where chunk.Id == input.Id && chunk.SearchInputHash == input.Hash && vector.IndexGenerationId == draft.Id
                    select vector.Values).SingleOrDefaultAsync(ct);
                if (payload is null) Refuse();
                resultHash.AppendData(payload);
            }
            if (!resultHash.GetHashAndReset().AsSpan().SequenceEqual(request.ResultDigest)) Refuse();
            var batch = await context.GpuBatches.AsNoTracking().SingleOrDefaultAsync(value => value.Id == task.BatchId, ct);
            var dispatches = await context.GpuExecutorDispatches.AsNoTracking().Where(value => value.BatchId == task.BatchId).ToArrayAsync(ct);
            if (batch is null || batch.State != (int)GpuBatchState.Completed || batch.ItemCount != 1 ||
                batch.ModelRuntimeKey != task.ModelRuntimeKey || batch.SettingsFingerprint != task.SettingsFingerprint ||
                batch.AdmissionGeneration != task.AdmissionGeneration || dispatches.Length != 1 ||
                await context.GpuCapacitySlots.AnyAsync(value => value.ActiveBatchId == batch.Id, ct)) Refuse();
            var dispatch = dispatches[0];
            if (request.DispatchId != dispatch.DispatchId || dispatch.State != (int)GpuExecutorDispatchState.Terminal ||
                dispatch.ExecutorKey != EmbeddingGpuExecutor.Name || dispatch.AdmissionGeneration != batch.AdmissionGeneration ||
                dispatch.CapacitySlotKey != batch.CapacitySlotKey || dispatch.OwnerKey != batch.OwnerKey) Refuse();
            binding.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { request, task, batch, dispatch }));
        }
        return Convert.ToHexStringLower(binding.GetHashAndReset());
    }

    internal void Requeue(FluxKnowledgeDbContext context, DateTimeOffset now, string actor, string idempotencyKey, string requestFingerprint)
    {
        context.AuditEvents.Add(new AuditEventEntity
        {
            PipelineRecordId = Record.Id, SourceRootId = Root.Id, SourceRevisionId = Source.Id, SourceActivityId = Activity.Id,
            EventType = "embedding recovery queued", EventFamily = "pipeline", Severity = "information", Actor = actor, OccurredAtUtc = now,
            CorrelationId = $"native:{requestFingerprint}", DetailsJson = JsonSerializer.Serialize(new
            {
                idempotencyKey, requestFingerprint, jobId = Job.Id, dispatchId = Dispatch.Id,
                Job.SourceRevision, Job.Reason, Job.ErrorDetails, Job.AttemptCount,
                jobLeaseGeneration = Job.LeaseGeneration, Dispatch.DispatchedAtUtc, Dispatch.DispatchGeneration,
                dispatchIdempotencyKey = Dispatch.IdempotencyKey, dispatchLeaseGeneration = Dispatch.LeaseGeneration,
                activity = new { Activity.Id, Activity.State, Activity.Reason, Activity.AttemptCount, Activity.AttemptEvidenceJson },
                targets = Targets
            })
        });
        Job.LeaseGeneration = checked(Job.LeaseGeneration + 1);
        Dispatch.LeaseGeneration = checked(Dispatch.LeaseGeneration + 1);
        Job.PublicState = (int)PublicJobState.WorkerQueued;
        Job.DueAtUtc = now;
        Job.Reason = "explicit-embedding-recovery";
        Job.ErrorDetails = null;
        Dispatch.DispatchedAtUtc = null;
        Dispatch.DueAtUtc = now;
        Activity.State = (int)SourceActivityState.FailedRetryable;
        Activity.Reason = "explicit-embedding-recovery";
        Activity.UpdatedAtUtc = now;
    }
    private static NativeTargetVersion Version(string kind, Guid id, byte[] version)=>version.Length==8
        ? new($"{kind}:{id:D}",Convert.ToBase64String(version)):throw new NativeOperationException("operation-fenced");
    private static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Refuse()=>throw new NativeOperationException("embedding-retry-not-eligible");
}
