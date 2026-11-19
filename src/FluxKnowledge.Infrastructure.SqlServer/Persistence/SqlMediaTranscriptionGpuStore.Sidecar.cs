using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Media;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlMediaTranscriptionGpuStore : ITranscriptSidecarPublisher
{
    public async ValueTask<string> PublishAsync(StageWorkItem workItem, SourceRevisionId revision, string inputHash,
        MediaTranscriptionCompletedResult completed, CancellationToken ct)
    {
        var job = workItem.Job;
        var dispatch = workItem.DispatchMessage;
        if (!ValidInput(job, revision, inputHash) || !completed.Result.Succeeded)
            throw new IOException("transcription-sidecar-input-invalid");

        var transcript = completed.Result.Transcript;
        if (completed.ExecutionId is { } executionId)
        {
            var worker = nativeOutput ?? throw new IOException("transcription-sidecar-output-unavailable");
            try
            {
                if (completed.UseNativeResult)
                {
                    var saved = await worker.ReadRecoveredResultAsync(executionId, ct).ConfigureAwait(false);
                    if (saved?.Transcript is { } text)
                    {
                        var utf8 = StrictUtf8.GetBytes(text);
                        if (utf8.Length != completed.TranscriptByteLength ||
                            Convert.ToHexStringLower(SHA256.HashData(utf8)) != completed.TranscriptSha256)
                            throw new IOException("transcription-sidecar-output-unavailable");
                        transcript = text;
                    }
                }
                else
                {
                    transcript = await worker.ReadPendingTranscriptAsync(executionId, completed.TranscriptByteLength!.Value,
                        completed.TranscriptSha256!, ct).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A prior attempt may have placed the exact file before losing
                // its SQL acknowledgement. Verify it against the durable receipt.
            }
        }

        await using var attempt = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await attempt.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            // This stage writes a source file. Serializable reads retain the exact
            // job, delivery and source bindings without holding the corpus fence.
            if (!await BindingIsAvailableAsync(db, job.PipelineRecordId.Value, job.SourceRevision, revision.Value,
                    inputHash, enabled: true, ct).ConfigureAwait(false))
                throw new SourceSnapshotChangedException("transcription-sidecar-recording-changed");

            var now = clock.GetUtcNow();
            var leaseUntil = await db.Jobs.AsNoTracking()
                .Where(row => row.Id == job.JobId.Value && row.PipelineRecordId == job.PipelineRecordId.Value &&
                    row.SourceRevision == job.SourceRevision && row.Operation == job.Operation &&
                    row.PublicState == (int)PublicJobState.WorkerProcessing && row.LeaseOwner == job.LeaseOwner &&
                    row.LeaseGeneration == job.LeaseGeneration && row.LeaseExpiresAtUtc > now)
                .Select(row => row.LeaseExpiresAtUtc)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var dispatchUntil = await db.OutboxMessages.AsNoTracking()
                .Where(row => row.Id == dispatch.DispatchMessageId.Value && row.JobId == job.JobId.Value &&
                    row.PipelineRecordId == job.PipelineRecordId.Value && row.SourceRevision == job.SourceRevision &&
                    row.Operation == job.Operation && row.DispatchedAtUtc == null &&
                    row.IdempotencyKey == dispatch.IdempotencyKey && row.LeaseOwner == dispatch.LeaseOwner &&
                    row.LeaseGeneration == dispatch.LeaseGeneration && row.LeaseExpiresAtUtc > now)
                .Select(row => row.LeaseExpiresAtUtc)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (leaseUntil is null || dispatchUntil is null)
                throw new SourceSnapshotChangedException("transcription-sidecar-lease-ended");

            var source = await db.SourceRevisions.AsNoTracking().Include(row => row.SourceRoot)
                .SingleAsync(row => row.Id == revision.Value, ct).ConfigureAwait(false);
            var root = SqlSourceRemovalReconciler.Restore(source.SourceRoot);
            var capture = SqlMediaTranscriptionRegistrationStore.ReadCapture(source.CurrentDiscoveryEvidenceJson);
            if (root.PhysicalIdentityFingerprint is null || source.OriginKind != 0 ||
                capture is not { Kind: "verified-physical-capture" } || capture.SourceRevisionId != source.Id ||
                capture.ObservedAt == default || capture.ObservedAt > now ||
                capture.ConfigurationRevision != root.ConfigurationRevision || capture.RootIdentityFingerprint != root.PhysicalIdentityFingerprint ||
                capture.ByteLength != source.ByteLength || capture.ContentSha256 != inputHash ||
                string.IsNullOrWhiteSpace(capture.StableSourceIdentity) || capture.LastWriteAtUtc <= DateTimeOffset.FromFileTime(0))
                throw new SourceSnapshotChangedException("transcription-sidecar-recording-changed");

            var relative = Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath);
            using var observation = new WindowsSourceObservationReader().Open(root, relative);
            if (observation.Observation.Identity != capture.StableSourceIdentity ||
                observation.Observation.ByteLength != capture.ByteLength ||
                observation.Observation.LastWriteTicks != capture.LastWriteAtUtc.UtcDateTime.ToFileTimeUtc())
                throw new SourceSnapshotChangedException("transcription-sidecar-recording-changed");

            var writer = new WindowsTranscriptSidecarWriter();
            var receipt = !string.IsNullOrWhiteSpace(transcript)
                ? await writer.WriteAsync(root, relative, observation.Observation, transcript, ct, ValidateLease).ConfigureAwait(false)
                : completed is { TranscriptSha256: { } sha, TranscriptByteLength: { } bytes }
                    ? await writer.VerifyPlacedAsync(root, relative, observation.Observation, sha, bytes, ct, ValidateLease).ConfigureAwait(false)
                    : null;
            if (receipt is null) throw new IOException("transcription-sidecar-output-unavailable");
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(receipt, JsonOptions);

            void ValidateLease()
            {
                if (clock.GetUtcNow() >= leaseUntil || clock.GetUtcNow() >= dispatchUntil)
                    throw new SourceSnapshotChangedException("transcription-sidecar-lease-ended");
                observation.ValidateCurrent();
            }
        }).ConfigureAwait(false);
    }

    public void ReleasePrivateResult(Guid executionId)
    {
        nativeOutput?.RemovePendingTranscript(executionId);
        nativeOutput?.RemoveSettledFiles(executionId);
    }
}
