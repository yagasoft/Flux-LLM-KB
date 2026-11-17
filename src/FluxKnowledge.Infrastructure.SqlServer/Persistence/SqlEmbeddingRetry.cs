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
    IReadOnlyList<NativeTargetVersion> Targets, bool HasUnstartedHandoff = false, bool BeforeFirstDraft = false,
    bool RequiresSettledGpuPublicationProof = false)
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
        if (source is null || source.SuppressedAtUtc is not null || source.ContentSha256 != record.ContentHash ||
            !await SqlPhysicalSourceHistory.IsValidAsync(context, source, ct)) Refuse();
        var root = await context.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == source.SourceRootId, ct);
        if (root is null || root.State != (int)SourceRootState.Enabled) Refuse();
        var hasUnstartedHandoff = await SqlEmbeddingGpuRequestStore.UnstartedRequests(context).AnyAsync(value => value.ParentJobId == job.Id, ct);
        var settledRecovery = await context.EmbeddingGpuRequests.AnyAsync(value => value.ParentJobId == job.Id && value.State == 2 && value.ResultDigest == null, ct);
        var draft = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.EmbeddingJobId == jobId, ct);
        var needsCurrentGitCapture = (hasUnstartedHandoff || settledRecovery || draft is null) && root.CrawlMode == (int)SourceDiscoveryMode.GitTracked;
        var qualifiedCurrentGit = needsCurrentGitCapture &&
            await SqlGitTextSelection.ReadAsync(context, source.Id, ct) is not null;
        if (needsCurrentGitCapture &&
            (!qualifiedCurrentGit || record.RepositoryRecoveryBindingJson is null)) Refuse();
        var state = await context.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, ct);
        if (state.CorpusRebuildOperationId is not null || await context.CorpusRebuildSupersededJobs.AnyAsync(value => value.JobId == jobId, ct) ||
            !qualifiedCurrentGit && await context.SourceRevisions.AnyAsync(value => value.Id != source.Id && value.SourceRootId == source.SourceRootId &&
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
        if (canonical is null || canonical.SearchText is null ||
            CodeDisclosureIntegrity.Hash(canonical.SearchText) != canonical.ContentHash) Refuse();
        if (draft is null)
            return await ReadBeforeFirstDraftAsync(context, job, dispatch, record, source, root, activity, canonical, state, runtime, ct);
        if (state.ActiveIndexGenerationId == draft.Id) Refuse();
        string vectors;
        try
        {
            SqlEmbeddingCheckpointStore.ValidateDraft(draft, jobId, runtime.Profile, state.CorpusEpoch);
            vectors = await SqlEmbeddingCheckpointStore.ReadCheckpointChecksumAsync(context, draft, record.Id, record.Revision,
                requireComplete: false, ct);
        }
        catch (InvalidOperationException) { Refuse(); throw; }
        var inputs = await ReadInputBindingAsync(context, record, canonical, draft, ct);
        if (settledRecovery && !await SqlExplicitGpuRecovery.HasCurrentInputAsync(context, source.Id, ct)) Refuse();
        var gpu = await ReadSettledGpuBindingAsync(context, job, draft, runtime, ct, repositoryRecovery: settledRecovery,
            allowUnresolvedGroup: settledRecovery, allowUnstartedHandoff: true);
        return new(job, dispatch, record, source, root, activity,
        [
            Version("embedding-job", job.Id, job.RowVersion), Version("embedding-dispatch", dispatch.Id, dispatch.RowVersion),
            Version("record", record.Id, record.RowVersion), Version("source", source.Id, source.RowVersion),
            Version("root", root.Id, root.RowVersion), Version("activity", activity.Id, activity.RowVersion),
            Version("generation", draft.Id, draft.RowVersion), new($"canonical-artifact:{canonical.Id:D}", Hash(canonical)),
            new($"embedding-inputs:{jobId:D}", Hash(new { inputs, vectors })), new($"embedding-gpu:{jobId:D}", gpu),
            new("embedding-epoch-profile", Hash(new { state.CorpusEpoch, runtime }))
        ], hasUnstartedHandoff, RequiresSettledGpuPublicationProof: settledRecovery);
    }

    internal static async Task<string> ReadInputBindingAsync(FluxKnowledgeDbContext context, PipelineRecordEntity record,
        ArtifactEntity canonical, IndexGenerationEntity? draft, CancellationToken ct)
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
        if (draft is null && lastId == 0 && !string.IsNullOrWhiteSpace(canonical.SearchText)) Refuse();
        long lastVector = 0;
        while (draft is not null)
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

    private static async Task<SqlEmbeddingRetry> ReadBeforeFirstDraftAsync(FluxKnowledgeDbContext context, JobEntity job,
        OutboxMessageEntity dispatch, PipelineRecordEntity record, SourceRevisionEntity source, SourceRootConfigurationEntity root,
        SourceActivityEntity activity, ArtifactEntity canonical, IndexStateEntity state, EmbeddingGpuRuntime runtime, CancellationToken ct)
    {
        var prior = await (from delivery in context.OutboxMessages.AsNoTracking()
            join parent in context.Jobs.AsNoTracking() on delivery.JobId equals parent.Id
            where parent.PipelineRecordId == record.Id && parent.SourceRevision == record.Revision && parent.Stage == (int)PipelineStage.CanonicalIndex &&
                parent.Operation == PipelineOperations.CanonicalIndex && parent.PublicState == (int)PublicJobState.Completed &&
                parent.LeaseOwner == null && parent.LeaseExpiresAtUtc == null && delivery.CompletedArtifactId == canonical.Id &&
                delivery.PipelineRecordId == record.Id && delivery.SourceRevision == record.Revision && delivery.Stage == parent.Stage &&
                EF.Functions.Collate(delivery.Operation, "Latin1_General_100_BIN2") == EF.Functions.Collate(parent.Operation, "Latin1_General_100_BIN2") &&
                delivery.DispatchGeneration == dispatch.DispatchGeneration - 1 && delivery.DispatchedAtUtc != null &&
                delivery.LeaseOwner == null && delivery.LeaseExpiresAtUtc == null
            select new { Job = parent, Delivery = delivery }).ToArrayAsync(ct);
        if (prior.Length != 1 ||
            await context.Jobs.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision && value.Id != job.Id && value.Stage >= job.Stage, ct) ||
            await context.EmbeddingGpuRequests.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision, ct) ||
            await context.GpuMiniTasks.AnyAsync(value => context.Jobs.Any(parent => parent.Id == value.ParentJobId && parent.PipelineRecordId == record.Id && parent.SourceRevision == record.Revision), ct) ||
            await context.IndexGenerations.AnyAsync(value => context.Jobs.Any(parent => parent.Id == value.EmbeddingJobId && parent.PipelineRecordId == record.Id && parent.SourceRevision == record.Revision), ct) ||
            await context.Vectors.AnyAsync(value => context.TextChunks.Any(chunk => chunk.Id == value.TextChunkId && chunk.Artifact.PipelineRecordId == record.Id && chunk.Artifact.SourceRevision == record.Revision), ct)) Refuse();
        var inputs = await ReadInputBindingAsync(context, record, canonical, null, ct);
        return new(job, dispatch, record, source, root, activity,
        [
            Version("embedding-job", job.Id, job.RowVersion), Version("embedding-dispatch", dispatch.Id, dispatch.RowVersion),
            Version("record", record.Id, record.RowVersion), Version("source", source.Id, source.RowVersion),
            Version("root", root.Id, root.RowVersion), Version("activity", activity.Id, activity.RowVersion),
            Version("canonical-job", prior[0].Job.Id, prior[0].Job.RowVersion), Version("canonical-delivery", prior[0].Delivery.Id, prior[0].Delivery.RowVersion),
            new($"canonical-artifact:{canonical.Id:D}", Hash(canonical)),
            new($"embedding-before-first-draft:{job.Id:D}", Hash(new { state.CorpusEpoch, runtime, inputs, prior[0].Delivery.CompletedArtifactId }))
        ], BeforeFirstDraft: true);
    }

    private sealed record GpuInput(long Id, string Hash);
    internal static async Task<string> ReadSettledGpuBindingAsync(FluxKnowledgeDbContext context, JobEntity job,
        IndexGenerationEntity draft, EmbeddingGpuRuntime runtime, CancellationToken ct,
        bool repositoryRecovery = false, bool allowUnresolvedGroup = false, bool allowUnstartedHandoff = false)
    {
        var requests = await context.EmbeddingGpuRequests.AsNoTracking().Where(value => value.ParentJobId == job.Id)
            .OrderBy(value => value.MiniTaskId).ToArrayAsync(ct);
        var tasks = await context.GpuMiniTasks.AsNoTracking().Where(value => value.ParentJobId == job.Id)
            .OrderBy(value => value.Id).ToArrayAsync(ct);
        EmbeddingGpuRequestEntity? unresolved = null;
        using var binding = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var unstarted = requests.Where(value => !tasks.Any(task => task.Id == value.MiniTaskId)).ToArray();
        if (unstarted.Length > 1 || requests.Length != tasks.Length + unstarted.Length ||
            tasks.Any(task => !requests.Any(request => request.MiniTaskId == task.Id))) Refuse();
        if (unstarted.Length == 1)
        {
            if (!allowUnstartedHandoff) Refuse();
            await ValidateUnstartedHandoffAsync(context, job, draft, runtime, unstarted[0], ct);
            binding.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { UnstartedRequest = unstarted[0], runtime.RuntimeKey, runtime.SettingsFingerprint }));
            requests = requests.Where(value => value.MiniTaskId != unstarted[0].MiniTaskId).ToArray();
        }
        var batchIds = tasks.Where(task => task.BatchId != null).Select(task => task.BatchId!.Value).Distinct().ToArray();
        var batches = await context.GpuBatches.AsNoTracking().Where(value => batchIds.Contains(value.Id)).ToArrayAsync(ct);
        var dispatches = await context.GpuExecutorDispatches.AsNoTracking().Where(value => batchIds.Contains(value.BatchId)).ToArrayAsync(ct);
        var reserved = await context.GpuCapacitySlots.Where(value => value.ActiveBatchId != null && batchIds.Contains(value.ActiveBatchId.Value))
            .Select(value => value.ActiveBatchId!.Value).ToArrayAsync(ct);
        foreach (var request in requests)
        {
            var task = tasks.Single(value => value.Id == request.MiniTaskId);
            if (request.PipelineRecordId != job.PipelineRecordId || request.SourceRevision != job.SourceRevision ||
                request.GenerationId != draft.Id || request.CorpusEpoch != draft.CorpusEpoch || request.ModelFingerprint != runtime.Profile.ModelFingerprint ||
                request.Dimensions != runtime.Profile.Dimensions || request.State != 2 || !request.NativeCleanupConfirmed ||
                request.CleanupConfirmedAtUtc is null ||
                request.ExecutorInstanceId is null || request.ClaimOperationId is null || request.OwnerProcessId is null ||
                request.OwnerStartedAtUtc is null || string.IsNullOrEmpty(request.OwnerMachineFingerprint) ||
                task.SourceRevision != job.SourceRevision || task.BatchId is null ||
                task.PriorityLane != (int)GpuPriorityLane.DocumentIndexing || task.ModelRuntimeKey != runtime.RuntimeKey ||
                task.SettingsFingerprint != runtime.SettingsFingerprint || CodeDisclosureIntegrity.Hash(request.InputsJson) != request.InputDigest) Refuse();
            GpuInput[] inputs;
            try { inputs = JsonSerializer.Deserialize<GpuInput[]>(request.InputsJson) ?? []; }
            catch (JsonException) { Refuse(); throw; }
            if (inputs.Length is < 1 or > EmbeddingGpuRuntime.MaximumRequestChunks || inputs.Select(value => value.Id).Distinct().Count() != inputs.Length) Refuse();
            // A missing result is never evidence of success. Validate its original
            // canonical group even when a later attempt owns the saved vectors.
            var batch = batches.SingleOrDefault(value => value.Id == task.BatchId);
            var deliveries = dispatches.Where(value => value.BatchId == task.BatchId).ToArray();
            if (batch is null || batch.ItemCount != 1 ||
                batch.ModelRuntimeKey != task.ModelRuntimeKey || batch.SettingsFingerprint != task.SettingsFingerprint ||
                batch.AdmissionGeneration != task.AdmissionGeneration || deliveries.Length != 1 || reserved.Contains(batch.Id)) Refuse();
            var dispatch = deliveries[0];
            if (request.DispatchId != dispatch.DispatchId ||
                dispatch.ExecutorKey != EmbeddingGpuExecutor.Name || dispatch.AdmissionGeneration != batch.AdmissionGeneration ||
                dispatch.CapacitySlotKey != batch.CapacitySlotKey || dispatch.OwnerKey != batch.OwnerKey) Refuse();

            if (request.ResultDigest is { Length: 32 })
            {
                if (task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed || batch.State != (int)GpuBatchState.Completed ||
                    dispatch.State != (int)GpuExecutorDispatchState.Terminal) Refuse();
                using var resultHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var ids = inputs.Select(value => value.Id).ToArray();
                var payloads = await (from chunk in SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision)
                    join vector in context.Vectors on chunk.Id equals vector.TextChunkId
                    where ids.Contains(chunk.Id) && vector.IndexGenerationId == draft.Id
                    select new { chunk.Id, chunk.SearchInputHash, vector.Values }).ToArrayAsync(ct);
                foreach (var input in inputs)
                {
                    var payload = payloads.SingleOrDefault(value => value.Id == input.Id && value.SearchInputHash == input.Hash)?.Values;
                    if (payload is null) Refuse();
                    resultHash.AppendData(payload);
                }
                if (!resultHash.GetHashAndReset().AsSpan().SequenceEqual(request.ResultDigest)) Refuse();
            }
            else
            {
                if (!repositoryRecovery || request.ResultDigest is not null || task.ExecutionState != (int)GpuMiniTaskExecutionState.OutcomeUncertain ||
                    dispatch.State != (int)GpuExecutorDispatchState.Terminal && dispatch.State != (int)GpuExecutorDispatchState.DeliveryUncertain ||
                    batch.State != (int)GpuBatchState.Released && !(batch.State == (int)GpuBatchState.CapacityUncertain &&
                        await context.GpuSchedulerOperationReceipts.AnyAsync(receipt => receipt.OperationKind == "capacity-reconciliation" &&
                            receipt.Accepted && receipt.Committed && receipt.BatchId == batch.Id &&
                            receipt.AdmissionGeneration == batch.AdmissionGeneration && receipt.CapacitySlotKey == batch.CapacitySlotKey &&
                            receipt.OwnerKey == batch.OwnerKey, ct))) Refuse();
                foreach (var input in inputs)
                {
                    if (!await SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision)
                        .AnyAsync(chunk => chunk.Id == input.Id && chunk.SearchInputHash == input.Hash, ct)) Refuse();
                }
                var replacements = requests.Where(success => success.ResultDigest is { Length: 32 } &&
                    SameGroup(success, request) && tasks.Single(value => value.Id == success.MiniTaskId).CreatedSequence > task.CreatedSequence).ToArray();
                if (replacements.Length == 0)
                {
                    if (!allowUnresolvedGroup || unresolved is not null && !SameGroup(unresolved, request) ||
                        requests.Count(value => SameGroup(value, request) && value.ResultDigest is null) >= 3 ||
                        await context.Vectors.AnyAsync(value => value.IndexGenerationId == draft.Id &&
                            inputs.Select(input => input.Id).Contains(value.TextChunkId), ct)) Refuse();
                    unresolved = request;
                }
                else if (replacements.Length != 1) Refuse();
            }
            binding.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { request, task, batch, dispatch }));
        }
        // Older failed attempts may precede this same group. The newest unresolved
        // attempt must still be the latest parent task, with no competing work.
        if (unresolved is not null && tasks.Max(value => value.CreatedSequence) !=
            tasks.Where(task => requests.Any(request => request.MiniTaskId == task.Id && SameGroup(request, unresolved)))
                .Max(value => value.CreatedSequence)) Refuse();
        if (unresolved is not null && unstarted.Length == 1 && !SameGroup(unstarted[0], unresolved)) Refuse();
        return Convert.ToHexStringLower(binding.GetHashAndReset());
    }

    internal static async Task ValidateUnstartedHandoffAsync(FluxKnowledgeDbContext context, JobEntity job, IndexGenerationEntity draft,
        EmbeddingGpuRuntime runtime, EmbeddingGpuRequestEntity request, CancellationToken ct)
    {
        if (request.ParentJobId != job.Id || request.PipelineRecordId != job.PipelineRecordId || request.SourceRevision != job.SourceRevision ||
            request.GenerationId != draft.Id || request.CorpusEpoch != draft.CorpusEpoch || request.ModelFingerprint != runtime.Profile.ModelFingerprint ||
            request.Dimensions != runtime.Profile.Dimensions || request.State != 0 || request.ResultDigest != null || request.NativeCleanupConfirmed ||
            request.CleanupConfirmedAtUtc != null || request.ExecutorInstanceId != null || request.ClaimOperationId != null || request.DispatchId != null ||
            request.OwnerProcessId != null || request.OwnerStartedAtUtc != null || request.OwnerMachineFingerprint != null ||
            CodeDisclosureIntegrity.Hash(request.InputsJson) != request.InputDigest ||
            await context.GpuMiniTasks.AnyAsync(value => value.Id == request.MiniTaskId || value.IdempotencyKey == $"embedding-gpu:{request.MiniTaskId:N}", ct) ||
            await context.GpuExecutorResultReceipts.AnyAsync(value => value.MiniTaskId == request.MiniTaskId, ct) ||
            await context.EmbeddingGpuRequests.AnyAsync(value => value.ParentJobId == job.Id && value.MiniTaskId != request.MiniTaskId && value.State < 2, ct)) Refuse();
        GpuInput[] inputs;
        try { inputs = JsonSerializer.Deserialize<GpuInput[]>(request.InputsJson) ?? []; }
        catch (JsonException) { Refuse(); throw; }
        if (inputs.Length is < 1 or > EmbeddingGpuRuntime.MaximumRequestChunks || inputs.Select(value => value.Id).Distinct().Count() != inputs.Length) Refuse();
        var ids = inputs.Select(value => value.Id).ToArray();
        if (await context.Vectors.AnyAsync(value => value.IndexGenerationId == draft.Id && ids.Contains(value.TextChunkId), ct)) Refuse();
        var chunks = await SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision).Where(value => ids.Contains(value.Id)).ToArrayAsync(ct);
        foreach (var input in inputs)
        {
            var chunk = chunks.SingleOrDefault(value => value.Id == input.Id);
            if (chunk is null || chunk.SearchInputHash != input.Hash || CodeDisclosureIntegrity.Hash(chunk.Content) != chunk.ContentHash ||
                new CanonicalTextChunk(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length, chunk.Content,
                    chunk.ContentHash, chunk.PassagePolicyFingerprint, chunk.ContextHeader).SearchInputHash != input.Hash) Refuse();
        }
    }

    private static bool SameGroup(EmbeddingGpuRequestEntity left, EmbeddingGpuRequestEntity right) =>
        left.ParentJobId == right.ParentJobId && left.PipelineRecordId == right.PipelineRecordId && left.SourceRevision == right.SourceRevision &&
        left.GenerationId == right.GenerationId && left.CorpusEpoch == right.CorpusEpoch && left.ModelFingerprint == right.ModelFingerprint &&
        left.Dimensions == right.Dimensions && left.InputDigest == right.InputDigest && left.InputsJson == right.InputsJson;

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
                requireCurrentGpuCapture = (HasUnstartedHandoff || BeforeFirstDraft || RequiresSettledGpuPublicationProof) && Root.CrawlMode == (int)SourceDiscoveryMode.GitTracked,
                requireSettledGpuPublicationProof = RequiresSettledGpuPublicationProof,
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
