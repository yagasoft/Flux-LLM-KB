using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Durable private recording results and the exact scheduler reservation that produced them.</summary>
public sealed partial class SqlMediaTranscriptionGpuStore(
    IDbContextFactory<FluxKnowledgeDbContext> factory,
    GpuSchedulerCoordinator scheduler,
    MediaTranscriptionGpuRuntime runtime,
    TimeProvider clock,
    ISourceWorkAdmission sourceAdmission,
    MediaTranscriptionDiscoveryOptions? discovery = null, MediaTranscriptionLocalExecutor? nativeOutput = null) : IMediaTranscriptionGpuQueue, IDocumentOcrExecutionObserver
{
    private readonly Guid[]? _approvedRoots = discovery?.SourceRootIds.ToArray();

    // This private payload is persisted and parsed as JSON. Keep spoken text in its original script.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<MediaTranscriptionGpuHandoff> QueueAsync(ClaimedJob job, SourceRevisionId revision,
        string contentSha256, CancellationToken ct)
    {
        if (!ValidInput(job, revision, contentSha256))
            return new(false, "media-transcription-input-invalid");
        var key = OperationFingerprint("request", job.JobId.Value, revision.Value, job.SourceRevision);
        var id = new Guid(Convert.FromHexString(key).AsSpan(0, 16));
        var canQueue = await MutateAsync(async db =>
        {
            if (!await BindingIsAvailableAsync(db, job.PipelineRecordId.Value, job.SourceRevision, revision.Value,
                    contentSha256, true, ct).ConfigureAwait(false))
                return false;
            var existing = await db.MediaTranscriptionGpuRequests.SingleOrDefaultAsync(r => r.MiniTaskId == id, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!MatchesInput(existing, job, revision, contentSha256))
                    return false;
                if (await db.GpuMiniTasks.AnyAsync(task => task.Id == id, ct).ConfigureAwait(false))
                    return true;
            }
            var now = clock.GetUtcNow();
            if (!await db.Jobs.AnyAsync(j => j.Id == job.JobId.Value && j.PipelineRecordId == job.PipelineRecordId.Value &&
                    j.SourceRevision == job.SourceRevision && j.Stage == (int)PipelineStage.Extract &&
                    j.Operation == MediaTranscriptionGpuContracts.Operation && j.PublicState == (int)PublicJobState.WorkerProcessing &&
                    j.LeaseOwner == job.LeaseOwner && j.LeaseGeneration == job.LeaseGeneration && j.LeaseExpiresAtUtc > now, ct)
                    .ConfigureAwait(false))
                return false;
            if (existing is null)
                db.MediaTranscriptionGpuRequests.Add(new()
                {
                    MiniTaskId = id, ParentJobId = job.JobId.Value, PipelineRecordId = job.PipelineRecordId.Value,
                    SourceRevision = job.SourceRevision, RetainedSourceRevisionId = revision.Value, ContentSha256 = contentSha256,
                    ModelRuntimeKey = runtime.ModelRuntimeKey, SettingsFingerprint = runtime.SettingsFingerprint, CreatedAtUtc = now
                });
            return true;
        }, ct).ConfigureAwait(false);
        if (!canQueue)
            return new(false, "media-transcription-input-or-claim-unavailable");

        var handoff = await scheduler.HandoffAsync(new(job, MediaTranscriptionGpuRuntime.PriorityLane, runtime.ModelRuntimeKey,
            runtime.SettingsFingerprint, runtime.EstimatedBytes, $"media-transcription:{key}", id), ct).ConfigureAwait(false);
        if (!handoff.Committed || handoff.MiniTaskId != id)
            throw new InvalidOperationException("media-transcription-handoff-unconfirmed");
        return new(true, "media-transcription-queued", id);
    }

    public async ValueTask<MediaTranscriptionCompletedResult?> ReadCompletedAsync(ClaimedJob job, SourceRevisionId revision,
        string contentSha256, CancellationToken ct)
    {
        if (!ValidInput(job, revision, contentSha256)) return null;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await BindingIsAvailableAsync(db, job.PipelineRecordId.Value, job.SourceRevision, revision.Value,
                contentSha256, true, ct).ConfigureAwait(false))
            return null;
        var request = await db.MediaTranscriptionGpuRequests.AsNoTracking().SingleOrDefaultAsync(r =>
            r.ParentJobId == job.JobId.Value && r.PipelineRecordId == job.PipelineRecordId.Value &&
            r.SourceRevision == job.SourceRevision && r.RetainedSourceRevisionId == revision.Value &&
            r.ContentSha256 == contentSha256 && r.ModelRuntimeKey == runtime.ModelRuntimeKey &&
            r.SettingsFingerprint == runtime.SettingsFingerprint && r.RequeuedAtUtc != null, ct).ConfigureAwait(false);
        return request is null ? null : ReadResult(request) ??
            new(new(false, "transcription-result-receipt-invalid", null, null));
    }

    private bool MatchesInput(MediaTranscriptionGpuRequestEntity request, ClaimedJob job, SourceRevisionId revision, string hash) =>
        request.ParentJobId == job.JobId.Value && request.PipelineRecordId == job.PipelineRecordId.Value &&
        request.SourceRevision == job.SourceRevision && request.RetainedSourceRevisionId == revision.Value &&
        request.ContentSha256 == hash && request.ModelRuntimeKey == runtime.ModelRuntimeKey &&
        request.SettingsFingerprint == runtime.SettingsFingerprint;

    private static bool ValidInput(ClaimedJob job, SourceRevisionId revision, string contentSha256) =>
        job.Stage == PipelineStage.Extract && job.Operation == MediaTranscriptionGpuContracts.Operation &&
        job.PublicState == PublicJobState.WorkerProcessing && revision.Value != Guid.Empty && contentSha256.Length == 64 &&
        contentSha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private Task<bool> BindingIsAvailableAsync(FluxKnowledgeDbContext db, Guid recordId, long revision,
        Guid retainedSourceRevisionId, string contentSha256, bool enabled, CancellationToken ct) =>
        BoundRecords(db, enabled)
            .AnyAsync(r => r.Id == recordId && r.Revision == revision && r.CurrentStage == (int)PipelineStage.Extract &&
                r.SourceRevisionId == retainedSourceRevisionId &&
                EF.Functions.Collate(r.ContentHash, "Latin1_General_100_BIN2") == contentSha256 &&
                EF.Functions.Collate(r.SourceRevision!.ContentSha256, "Latin1_General_100_BIN2") == contentSha256 &&
                (!enabled || r.SourceRevision.SourceRoot.State == (int)SourceRootState.Enabled), ct);

    private IQueryable<PipelineRecordEntity> BoundRecords(FluxKnowledgeDbContext db, bool approved)
    {
        var records = SqlSourceWorkEligibility.Records(db, sourceAdmission, includeAdmission: approved);
        if (!approved || _approvedRoots is not { } roots) return records;
        if (roots.Length == 0) return records.Where(_ => false);
        return records.Where(record => roots.Contains(record.SourceRevision!.SourceRootId));
    }

    private async ValueTask<T> MutateAsync<T>(Func<FluxKnowledgeDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var execution = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
            await SqlGpuSchedulerStore.AcquireMutationLockAsync(db, transaction.GetDbTransaction(), ct).ConfigureAwait(false);
            var result = await action(db).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private static string OperationFingerprint(string action, Guid first, Guid second, long generation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"media-transcription-v1|{action}|{first:N}|{second:N}|{generation}"))));
}
