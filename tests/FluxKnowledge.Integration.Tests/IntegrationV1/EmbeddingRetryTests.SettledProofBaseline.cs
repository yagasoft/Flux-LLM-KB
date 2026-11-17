using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed partial class EmbeddingRetryTests
{
    // Frozen 5d4dcd07 proof encoding: a differential oracle for the batching
    // change, including SQL GUID ordering and unchanged confirmation targets.
    internal static async Task<SqlEmbeddingRetry> ReadLegacyEligibleAsync(FluxKnowledgeDbContext context, Guid jobId,
        EmbeddingGpuRuntime? runtime, bool forCommit, CancellationToken ct)
    {
        if (runtime is null) LegacyRefuse();
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
            job.LeaseGeneration == long.MaxValue) LegacyRefuse();
        var dispatch = dispatches[0];
        if (dispatch.PipelineRecordId != job.PipelineRecordId || dispatch.SourceRevision != job.SourceRevision ||
            dispatch.Stage != job.Stage || dispatch.Operation != job.Operation || dispatch.DispatchedAtUtc is null ||
            dispatch.CompletedArtifactId is not null || dispatch.LeaseOwner is not null || dispatch.LeaseExpiresAtUtc is not null ||
            dispatch.LeaseGeneration == long.MaxValue) LegacyRefuse();
        var record = await context.PipelineRecords.SingleOrDefaultAsync(value => value.Id == job.PipelineRecordId, ct);
        if (record is null || record.Revision != job.SourceRevision || record.IsDeleted || record.CompletionCriteriaMet ||
            record.CurrentStage != (int)PipelineStage.Embed || record.SourceRevisionId is null) LegacyRefuse();
        var source = await context.SourceRevisions.SingleOrDefaultAsync(value => value.Id == record.SourceRevisionId, ct);
        if (source is null || source.SuppressedAtUtc is not null || source.ContentSha256 != record.ContentHash ||
            !await SqlPhysicalSourceHistory.IsValidAsync(context, source, ct)) LegacyRefuse();
        var root = await context.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == source.SourceRootId, ct);
        if (root is null || root.State != (int)SourceRootState.Enabled) LegacyRefuse();
        var hasUnstartedHandoff = await SqlEmbeddingGpuRequestStore.UnstartedRequests(context).AnyAsync(value => value.ParentJobId == job.Id, ct);
        var settledRecovery = await context.EmbeddingGpuRequests.AnyAsync(value => value.ParentJobId == job.Id && value.State == 2 && value.ResultDigest == null, ct);
        var draft = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value => value.EmbeddingJobId == jobId, ct);
        var needsCurrentGitCapture = (hasUnstartedHandoff || settledRecovery || draft is null) && root.CrawlMode == (int)SourceDiscoveryMode.GitTracked;
        var qualifiedCurrentGit = needsCurrentGitCapture &&
            await SqlGitTextSelection.ReadAsync(context, source.Id, ct) is not null;
        if (needsCurrentGitCapture &&
            (!qualifiedCurrentGit || record.RepositoryRecoveryBindingJson is null)) LegacyRefuse();
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
            await context.DocumentPublications.AnyAsync(value => value.PipelineRecordId == record.Id && value.PipelineRecordRevision == record.Revision, ct)) LegacyRefuse();
        var activities = await context.SourceActivities.Where(value => value.SourceRevisionId == source.Id &&
            value.ActivityKind == (int)SourceActivityKind.TextExtraction && value.ResultingPipelineRecordId == record.Id &&
            value.ResultingPipelineRecordRevision == record.Revision).ToArrayAsync(ct);
        if (activities.Length != 1 || activities[0].State != (int)SourceActivityState.FailedTerminal) LegacyRefuse();
        var activity = activities[0];
        var canonical = await context.Artifacts.AsNoTracking().SingleOrDefaultAsync(value => value.PipelineRecordId == record.Id &&
            value.SourceRevision == record.Revision && value.Stage == (int)PipelineStage.CanonicalIndex, ct);
        if (canonical is null || canonical.SearchText is null ||
            CodeDisclosureIntegrity.Hash(canonical.SearchText) != canonical.ContentHash) LegacyRefuse();
        if (draft is null) throw new InvalidOperationException("Frozen proof fixture requires a draft.");
        if (state.ActiveIndexGenerationId == draft.Id) LegacyRefuse();
        string vectors;
        try
        {
            SqlEmbeddingCheckpointStore.ValidateDraft(draft, jobId, runtime.Profile, state.CorpusEpoch);
            vectors = await SqlEmbeddingCheckpointStore.ReadCheckpointChecksumAsync(context, draft, record.Id, record.Revision,
                requireComplete: false, ct);
        }
        catch (InvalidOperationException) { LegacyRefuse(); throw; }
        var inputs = await SqlEmbeddingRetry.ReadInputBindingAsync(context, record, canonical, draft, ct);
        if (settledRecovery && !await SqlExplicitGpuRecovery.HasCurrentInputAsync(context, source.Id, ct)) LegacyRefuse();
        var gpu = await ReadLegacySettledBindingAsync(context, job, draft, runtime, ct, repositoryRecovery: settledRecovery,
            allowUnresolvedGroup: settledRecovery, allowUnstartedHandoff: true);
        return new(job, dispatch, record, source, root, activity,
        [
            LegacyVersion("embedding-job", job.Id, job.RowVersion), LegacyVersion("embedding-dispatch", dispatch.Id, dispatch.RowVersion),
            LegacyVersion("record", record.Id, record.RowVersion), LegacyVersion("source", source.Id, source.RowVersion),
            LegacyVersion("root", root.Id, root.RowVersion), LegacyVersion("activity", activity.Id, activity.RowVersion),
            LegacyVersion("generation", draft.Id, draft.RowVersion), new($"canonical-artifact:{canonical.Id:D}", LegacyHash(canonical)),
            new($"embedding-inputs:{jobId:D}", LegacyHash(new { inputs, vectors })), new($"embedding-gpu:{jobId:D}", gpu),
            new("embedding-epoch-profile", LegacyHash(new { state.CorpusEpoch, runtime }))
        ], hasUnstartedHandoff, RequiresSettledGpuPublicationProof: settledRecovery);
    }

    internal static async Task<string> ReadLegacySettledBindingAsync(FluxKnowledgeDbContext context, JobEntity job,
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
            tasks.Any(task => !requests.Any(request => request.MiniTaskId == task.Id))) LegacyRefuse();
        if (unstarted.Length == 1)
        {
            if (!allowUnstartedHandoff) LegacyRefuse();
            await SqlEmbeddingRetry.ValidateUnstartedHandoffAsync(context, job, draft, runtime, unstarted[0], ct);
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
                task.SettingsFingerprint != runtime.SettingsFingerprint || CodeDisclosureIntegrity.Hash(request.InputsJson) != request.InputDigest) LegacyRefuse();
            LegacyGpuInput[] inputs;
            try { inputs = JsonSerializer.Deserialize<LegacyGpuInput[]>(request.InputsJson) ?? []; }
            catch (JsonException) { LegacyRefuse(); throw; }
            if (inputs.Length is < 1 or > EmbeddingGpuRuntime.MaximumRequestChunks || inputs.Select(value => value.Id).Distinct().Count() != inputs.Length) LegacyRefuse();
            // A missing result is never evidence of success. Validate its original
            // canonical group even when a later attempt owns the saved vectors.
            var batch = batches.SingleOrDefault(value => value.Id == task.BatchId);
            var deliveries = dispatches.Where(value => value.BatchId == task.BatchId).ToArray();
            if (batch is null || batch.ItemCount != 1 ||
                batch.ModelRuntimeKey != task.ModelRuntimeKey || batch.SettingsFingerprint != task.SettingsFingerprint ||
                batch.AdmissionGeneration != task.AdmissionGeneration || deliveries.Length != 1 || reserved.Contains(batch.Id)) LegacyRefuse();
            var dispatch = deliveries[0];
            if (request.DispatchId != dispatch.DispatchId ||
                dispatch.ExecutorKey != EmbeddingGpuExecutor.Name || dispatch.AdmissionGeneration != batch.AdmissionGeneration ||
                dispatch.CapacitySlotKey != batch.CapacitySlotKey || dispatch.OwnerKey != batch.OwnerKey) LegacyRefuse();

            if (request.ResultDigest is { Length: 32 })
            {
                if (task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed || batch.State != (int)GpuBatchState.Completed ||
                    dispatch.State != (int)GpuExecutorDispatchState.Terminal) LegacyRefuse();
                using var resultHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var ids = inputs.Select(value => value.Id).ToArray();
                var payloads = await (from chunk in SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision)
                    join vector in context.Vectors on chunk.Id equals vector.TextChunkId
                    where ids.Contains(chunk.Id) && vector.IndexGenerationId == draft.Id
                    select new { chunk.Id, chunk.SearchInputHash, vector.Values }).ToArrayAsync(ct);
                foreach (var input in inputs)
                {
                    var payload = payloads.SingleOrDefault(value => value.Id == input.Id && value.SearchInputHash == input.Hash)?.Values;
                    if (payload is null) LegacyRefuse();
                    resultHash.AppendData(payload);
                }
                if (!resultHash.GetHashAndReset().AsSpan().SequenceEqual(request.ResultDigest)) LegacyRefuse();
            }
            else
            {
                if (!repositoryRecovery || request.ResultDigest is not null || task.ExecutionState != (int)GpuMiniTaskExecutionState.OutcomeUncertain ||
                    dispatch.State != (int)GpuExecutorDispatchState.Terminal && dispatch.State != (int)GpuExecutorDispatchState.DeliveryUncertain ||
                    batch.State != (int)GpuBatchState.Released && !(batch.State == (int)GpuBatchState.CapacityUncertain &&
                        await context.GpuSchedulerOperationReceipts.AnyAsync(receipt => receipt.OperationKind == "capacity-reconciliation" &&
                            receipt.Accepted && receipt.Committed && receipt.BatchId == batch.Id &&
                            receipt.AdmissionGeneration == batch.AdmissionGeneration && receipt.CapacitySlotKey == batch.CapacitySlotKey &&
                            receipt.OwnerKey == batch.OwnerKey, ct))) LegacyRefuse();
                foreach (var input in inputs)
                {
                    if (!await SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision)
                        .AnyAsync(chunk => chunk.Id == input.Id && chunk.SearchInputHash == input.Hash, ct)) LegacyRefuse();
                }
                var replacements = requests.Where(success => success.ResultDigest is { Length: 32 } &&
                    LegacySameGroup(success, request) && tasks.Single(value => value.Id == success.MiniTaskId).CreatedSequence > task.CreatedSequence).ToArray();
                if (replacements.Length == 0)
                {
                    if (!allowUnresolvedGroup || unresolved is not null && !LegacySameGroup(unresolved, request) ||
                        requests.Count(value => LegacySameGroup(value, request) && value.ResultDigest is null) >= 3 ||
                        await context.Vectors.AnyAsync(value => value.IndexGenerationId == draft.Id &&
                            inputs.Select(input => input.Id).Contains(value.TextChunkId), ct)) LegacyRefuse();
                    unresolved = request;
                }
                else if (replacements.Length != 1) LegacyRefuse();
            }
            binding.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { request, task, batch, dispatch }));
        }
        // Older failed attempts may precede this same group. The newest unresolved
        // attempt must still be the latest parent task, with no competing work.
        if (unresolved is not null && tasks.Max(value => value.CreatedSequence) !=
            tasks.Where(task => requests.Any(request => request.MiniTaskId == task.Id && LegacySameGroup(request, unresolved)))
                .Max(value => value.CreatedSequence)) LegacyRefuse();
        if (unresolved is not null && unstarted.Length == 1 && !LegacySameGroup(unstarted[0], unresolved)) LegacyRefuse();
        return Convert.ToHexStringLower(binding.GetHashAndReset());
    }

    private static bool LegacySameGroup(EmbeddingGpuRequestEntity left, EmbeddingGpuRequestEntity right) =>
        left.ParentJobId == right.ParentJobId && left.PipelineRecordId == right.PipelineRecordId && left.SourceRevision == right.SourceRevision &&
        left.GenerationId == right.GenerationId && left.CorpusEpoch == right.CorpusEpoch && left.ModelFingerprint == right.ModelFingerprint &&
        left.Dimensions == right.Dimensions && left.InputDigest == right.InputDigest && left.InputsJson == right.InputsJson;


    private sealed record LegacyGpuInput(long Id, string Hash);
    private static NativeTargetVersion LegacyVersion(string kind, Guid id, byte[] version) => version.Length == 8
        ? new($"{kind}:{id:D}", Convert.ToBase64String(version)) : throw new NativeOperationException("operation-fenced");
    private static string LegacyHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void LegacyRefuse() => throw new NativeOperationException("embedding-retry-not-eligible");
}

