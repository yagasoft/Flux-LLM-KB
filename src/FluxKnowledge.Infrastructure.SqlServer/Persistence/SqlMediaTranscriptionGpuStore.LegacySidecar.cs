using System.Data;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Text;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlMediaTranscriptionGpuStore
{
    public async ValueTask<bool> ConvertPublishedSidecarAsync(Guid revisionId, CancellationToken ct)
    {
        if (discovery is null || !discovery.CatchUpRevisionIds.Contains(revisionId))
            return false;

        await using var attempt = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await attempt.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, ct).ConfigureAwait(false);
            var eligible = await (from r in BoundRecords(db, approved: true)
                join j in db.Jobs on r.Id equals j.PipelineRecordId
                join m in db.MediaTranscriptionGpuRequests on j.Id equals m.ParentJobId
                where r.SourceRevisionId == revisionId && r.CompletionCriteriaMet &&
                    r.SourceRevision!.OriginKind == 0 && r.CurrentStage == (int)PipelineStage.Publish &&
                    r.SourceRevision.SourceRoot.State == (int)SourceRootState.Enabled &&
                    EF.Functions.Collate(r.SourceRevision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                        EF.Functions.Collate(r.ContentHash, SchemaConfiguration.SchedulerFenceCollation) &&
                    j.SourceRevision == r.Revision && j.Stage == (int)PipelineStage.Extract &&
                    j.Operation == MediaTranscriptionGpuContracts.Operation && j.PublicState == (int)PublicJobState.Completed &&
                    m.PipelineRecordId == r.Id && m.SourceRevision == r.Revision && m.RetainedSourceRevisionId == revisionId &&
                    m.ContentSha256 == EF.Functions.Collate(r.ContentHash, SchemaConfiguration.SchedulerFenceCollation) &&
                    m.RequeuedAtUtc != null &&
                    m.CleanupKind == (int)DocumentOcrCleanupKind.OwnedJobEmpty &&
                    !db.Jobs.Any(other => other.PipelineRecordId == r.Id && other.SourceRevision == r.Revision &&
                        other.PublicState != (int)PublicJobState.Completed)
                select new { Record = r, Request = m, JobId = j.Id })
                .Take(2).ToArrayAsync(ct).ConfigureAwait(false);
            if (eligible.Length != 1) return false;
            var published = eligible[0];
            if (ReadResult(published.Request) is not { Result: { Succeeded: true, Transcript: { } fullText } original })
                return false;

            var proofQuery = FormattableStringFactory.Create(
                """
                SELECT CONVERT(int, 1) AS [Value] FROM [MediaTranscriptionGpuRequests] AS m
                JOIN [Jobs] AS j ON j.Id = m.ParentJobId JOIN [GpuMiniTasks] AS t ON t.Id = m.MiniTaskId
                WHERE m.MiniTaskId = {0} AND j.Id = {1} AND
                """ + " " + SqlRemovedSourceWork.MediaCleanup("m", "t", "j"),
                published.Request.MiniTaskId, published.JobId);
            if (!await db.Database.SqlQuery<int>(proofQuery).AnyAsync(ct).ConfigureAwait(false))
                return false;

            var extract = await db.Artifacts.SingleAsync(row => row.PipelineRecordId == published.Record.Id &&
                row.SourceRevision == published.Record.Revision && row.Stage == (int)PipelineStage.Extract, ct).ConfigureAwait(false);
            if (extract.SearchText != fullText || extract.ContentHash != published.Request.ContentSha256 ||
                extract.DocumentMetadataJson != original.MetadataJson)
                return false;

            var source = await db.SourceRevisions.AsNoTracking().Include(row => row.SourceRoot)
                .SingleAsync(row => row.Id == revisionId, ct).ConfigureAwait(false);
            var root = SqlSourceRemovalReconciler.Restore(source.SourceRoot);
            var capture = SqlMediaTranscriptionRegistrationStore.ReadCapture(source.CurrentDiscoveryEvidenceJson);
            var now = clock.GetUtcNow();
            if (root.PhysicalIdentityFingerprint is null ||
                capture is not { Kind: "verified-physical-capture" } || capture.SourceRevisionId != source.Id ||
                capture.ObservedAt == default || capture.ObservedAt > now || capture.ConfigurationRevision != root.ConfigurationRevision ||
                capture.RootIdentityFingerprint != root.PhysicalIdentityFingerprint ||
                capture.ByteLength != source.ByteLength || capture.ContentSha256 != published.Request.ContentSha256 ||
                string.IsNullOrWhiteSpace(capture.StableSourceIdentity) || capture.LastWriteAtUtc <= DateTimeOffset.FromFileTime(0))
                return false;
            var relative = Path.GetRelativePath(root.CanonicalPath, source.CanonicalPath);
            using var physical = new WindowsSourceObservationReader().Open(root, relative);
            if (physical.Observation.LinkCount != 1 || physical.Observation.Identity != capture.StableSourceIdentity ||
                physical.Observation.ByteLength != capture.ByteLength ||
                physical.Observation.LastWriteTicks != capture.LastWriteAtUtc.UtcDateTime.ToFileTimeUtc())
                return false;

            var sidecar = await new WindowsTranscriptSidecarWriter().WriteAsync(root, relative, physical.Observation,
                fullText, ct, physical.ValidateCurrent).ConfigureAwait(false);
            var target = Path.Combine(root.CanonicalPath, sidecar.RelativePath);
            var replacements = await (from file in SqlSourceWorkEligibility.Inputs(db, sourceAdmission)
                join record in db.PipelineRecords on file.Id equals record.SourceRevisionId
                where file.SourceRootId == root.Id.Value && file.OriginKind == 0 && file.SuppressedAtUtc == null &&
                    file.Extension == ".txt" && file.CanonicalPath == target && file.ContentSha256 == sidecar.ContentSha256 &&
                    file.ByteLength == sidecar.ByteLength && !record.IsDeleted &&
                    EF.Functions.Collate(record.ContentHash, SchemaConfiguration.SchedulerFenceCollation) ==
                        EF.Functions.Collate(file.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                    record.CompletionCriteriaMet && record.CurrentStage == (int)PipelineStage.Publish &&
                    db.Jobs.Any(job => job.PipelineRecordId == record.Id && job.SourceRevision == record.Revision &&
                        job.Stage == (int)PipelineStage.Extract && job.Operation == PipelineOperations.ExtractUtf8 &&
                        job.PublicState == (int)PublicJobState.Completed) &&
                    db.Artifacts.Any(artifact => artifact.PipelineRecordId == record.Id && artifact.SourceRevision == record.Revision &&
                        artifact.Stage == (int)PipelineStage.Publish)
                select new { File = file, Record = record }).Take(2).ToArrayAsync(ct).ConfigureAwait(false);
            if (replacements.Length != 1) return false;
            var replacement = replacements[0];

            if (!await VerifyPublishedReplacementAsync(db, replacement.Record, sidecar.ContentSha256, fullText, ct)
                    .ConfigureAwait(false))
                return false;
            var replacementCapture = SqlMediaTranscriptionRegistrationStore.ReadCapture(replacement.File.CurrentDiscoveryEvidenceJson);
            if (replacementCapture is not { Kind: "verified-physical-capture" } ||
                replacementCapture.SourceRevisionId != replacement.File.Id ||
                replacementCapture.ConfigurationRevision != root.ConfigurationRevision ||
                replacementCapture.RootIdentityFingerprint != root.PhysicalIdentityFingerprint ||
                replacementCapture.ByteLength != sidecar.ByteLength ||
                replacementCapture.ContentSha256 != sidecar.ContentSha256 ||
                replacementCapture.LastWriteAtUtc <= DateTimeOffset.FromFileTime(0))
                return false;

            using var placed = new WindowsSourceObservationReader().Open(root, sidecar.RelativePath);
            if (placed.Observation.Identity != replacementCapture.StableSourceIdentity ||
                placed.Observation.LinkCount != 1 || placed.Observation.ByteLength != sidecar.ByteLength ||
                placed.Observation.LastWriteTicks != replacementCapture.LastWriteAtUtc.UtcDateTime.ToFileTimeUtc())
                return false;
            var current = await new WindowsTranscriptSidecarWriter().WriteAsync(root, relative, physical.Observation,
                fullText, ct, physical.ValidateCurrent).ConfigureAwait(false);
            if (current != sidecar) return false;
            placed.ValidateCurrent();

            published.Record.IsDeleted = true;
            var legacyChunks = db.TextChunks.Where(chunk => chunk.Artifact.PipelineRecordId == published.Record.Id &&
                chunk.Artifact.SourceRevision == published.Record.Revision).Select(chunk => chunk.Id);
            await db.Vectors.Where(vector => !vector.IsDeleted && legacyChunks.Contains(vector.TextChunkId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsDeleted, true), ct).ConfigureAwait(false);
            await db.Artifacts.Where(row => row.PipelineRecordId == published.Record.Id &&
                row.SourceRevision == published.Record.Revision && row.Id != extract.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SearchText, ""), ct).ConfigureAwait(false);
            extract.SearchText = "";
            extract.ContentType = MediaTranscriptionGpuContracts.SidecarReceiptContentType;
            extract.DocumentMetadataJson = JsonSerializer.Serialize(sidecar, JsonOptions);
            published.Request.ResultJson = JsonSerializer.Serialize(new RetiredTranscriptReceipt(
                2, Convert.ToHexStringLower(published.Request.ResultDigest!), replacement.File.Id, sidecar,
                original.ReasonCode, original.MetadataJson!), JsonOptions);
            // The digest is immutable evidence in the scheduler history. Version 2 records a
            // retired result; there is no eligible extraction or GPU completion to replay.
            await SqlPublishedPassageSelection.AdvanceVersionAsync(db, now, ct,
                [new IndexAffectedScope(1, published.Record.SourceIdentityId)]).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static async ValueTask<bool> VerifyPublishedReplacementAsync(FluxKnowledgeDbContext db,
        PipelineRecordEntity record, string hash, string fullText, CancellationToken ct)
    {
        var artifacts = await db.Artifacts.AsNoTracking().Where(row => row.PipelineRecordId == record.Id &&
                row.SourceRevision == record.Revision && (row.Stage == (int)PipelineStage.Extract ||
                    row.Stage == (int)PipelineStage.CanonicalIndex))
            .Select(row => new { row.Id, row.Stage, row.ContentHash, row.SearchText }).ToArrayAsync(ct).ConfigureAwait(false);
        var extract = artifacts.SingleOrDefault(row => row.Stage == (int)PipelineStage.Extract);
        var canonical = artifacts.SingleOrDefault(row => row.Stage == (int)PipelineStage.CanonicalIndex);
        if (extract?.ContentHash != hash || extract.SearchText != fullText || canonical is null) return false;
        var expected = UnicodeTextNormalisation.FormKC(fullText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        if (canonical.SearchText != expected) return false;

        var passages = await db.TextChunks.AsNoTracking().Where(chunk => chunk.ArtifactId == canonical.Id &&
                chunk.SourceRevision == record.Revision)
            .OrderBy(chunk => chunk.StartOffset).Select(chunk => new { chunk.StartOffset, chunk.Length, chunk.Content })
            .ToArrayAsync(ct).ConfigureAwait(false);
        if (passages.Length == 0) return false;
        var covered = 0;
        foreach (var passage in passages)
        {
            if (passage.StartOffset < 0 || passage.Length <= 0 || passage.StartOffset > expected.Length - passage.Length ||
                !expected.AsSpan(passage.StartOffset, passage.Length).SequenceEqual(passage.Content) ||
                passage.StartOffset > covered && !OnlyWhitespace(expected.AsSpan(covered, passage.StartOffset - covered)))
                return false;
            covered = Math.Max(covered, passage.StartOffset + passage.Length);
        }
        return OnlyWhitespace(expected.AsSpan(covered));

        static bool OnlyWhitespace(ReadOnlySpan<char> text)
        {
            foreach (var character in text)
                if (!char.IsWhiteSpace(character)) return false;
            return true;
        }
    }

    private sealed record RetiredTranscriptReceipt(int Version, string LegacyResultSha256, Guid SidecarSourceRevisionId,
        TranscriptSidecarReceipt Sidecar, string OriginalResultReason, string OriginalMetadataJson);
}
