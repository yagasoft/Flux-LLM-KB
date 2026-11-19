using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

internal static class MediaTranscriptionFileReader
{
    internal static async Task<IReadOnlyDictionary<Guid, FileTranscriptionStatus>> ReadAsync(FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> revisionIds, CancellationToken token)
    {
        if (revisionIds.Count == 0) return new Dictionary<Guid, FileTranscriptionStatus>();
        var values = await (
            from activity in context.SourceActivities.AsNoTracking()
            join source in context.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals source.Id
            join recordValue in context.PipelineRecords.AsNoTracking() on activity.ResultingPipelineRecordId equals (Guid?)recordValue.Id into records
            from record in records.DefaultIfEmpty()
            join jobValue in context.Jobs.AsNoTracking().Where(job => job.Stage == (int)PipelineStage.Extract &&
                job.Operation == MediaTranscriptionGpuContracts.Operation)
                on new { Id = activity.ResultingPipelineRecordId, Revision = activity.ResultingPipelineRecordRevision }
                equals new { Id = (Guid?)jobValue.PipelineRecordId, Revision = (long?)jobValue.SourceRevision } into jobs
            from job in jobs.DefaultIfEmpty()
            join workValue in context.MediaTranscriptionGpuRequests.AsNoTracking()
                on new { Job = (Guid?)job.Id, Record = activity.ResultingPipelineRecordId, Revision = activity.ResultingPipelineRecordRevision }
                equals new { Job = (Guid?)workValue.ParentJobId, Record = (Guid?)workValue.PipelineRecordId, Revision = (long?)workValue.SourceRevision } into requests
            from work in requests.DefaultIfEmpty()
            where revisionIds.Contains(source.Id) && source.OriginKind == 0 && source.SuppressedAtUtc == null &&
                activity.ActivityKind == (int)SourceActivityKind.MediaTranscription &&
                activity.ProcessorVersion == MediaTranscriptionDiscoveryOptions.ProcessorVersion &&
                activity.DescriptorFingerprint == MediaTranscriptionDiscoveryOptions.DescriptorFingerprint &&
                EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(source.ContentSha256, SchemaConfiguration.SchedulerFenceCollation)
            select new
            {
                RevisionId = source.Id, source.SourceRootId, source.CanonicalPath, activity.UpdatedAtUtc,
                activity.Reason, activity.ResultingPipelineRecordId,
                SidecarReceipt = record == null || record.SourceRevisionId != source.Id ||
                    record.Revision != activity.ResultingPipelineRecordRevision ||
                    EF.Functions.Collate(record.ContentHash, SchemaConfiguration.SchedulerFenceCollation) !=
                        EF.Functions.Collate(source.ContentSha256, SchemaConfiguration.SchedulerFenceCollation)
                    ? null : context.Artifacts.Where(artifact =>
                    artifact.PipelineRecordId == record.Id && artifact.SourceRevision == record.Revision &&
                    artifact.Stage == (int)PipelineStage.Extract &&
                    artifact.ContentType == MediaTranscriptionGpuContracts.SidecarReceiptContentType)
                    .Select(artifact => artifact.DocumentMetadataJson).FirstOrDefault(),
                Bound = record != null && job != null && record.SourceRevisionId == source.Id &&
                    record.Revision == activity.ResultingPipelineRecordRevision && !record.IsDeleted &&
                    EF.Functions.Collate(record.ContentHash, SchemaConfiguration.SchedulerFenceCollation) ==
                        EF.Functions.Collate(source.ContentSha256, SchemaConfiguration.SchedulerFenceCollation),
                Published = record != null && job != null && job.PublicState == (int)PublicJobState.Completed &&
                    record.CompletionCriteriaMet && record.CurrentStage == (int)PipelineStage.Publish &&
                    context.Artifacts.Any(artifact => artifact.PipelineRecordId == record.Id && artifact.SourceRevision == record.Revision &&
                        artifact.Stage == (int)PipelineStage.Publish) &&
                    context.Jobs.Any(publish => publish.PipelineRecordId == record.Id && publish.SourceRevision == record.Revision &&
                        publish.Stage == (int)PipelineStage.Publish && publish.Operation == PipelineOperations.Publish &&
                        publish.PublicState == (int)PublicJobState.Completed),
                JobState = job == null ? (int?)null : job.PublicState,
                JobReason = job == null ? null : job.Reason,
                Uncertain = work != null && work.RetainedSourceRevisionId == source.Id &&
                    context.GpuMiniTasks.Any(task => task.Id == work.MiniTaskId && task.ParentJobId == work.ParentJobId &&
                        task.SourceRevision == work.SourceRevision && (task.ExecutionState == (int)GpuMiniTaskExecutionState.OutcomeUncertain ||
                         task.BatchId != null && context.GpuCapacitySlots.Any(slot => slot.ActiveBatchId == task.BatchId &&
                             slot.State == (int)GpuCapacitySlotState.Uncertain))),
                Permitted = work != null && work.RetainedSourceRevisionId == source.Id &&
                    work.ExecutionPhase == (int)DocumentOcrExecutionPhase.NativeStartPermitted && work.CleanupConfirmedAtUtc == null
            }).ToArrayAsync(token).ConfigureAwait(false);
        var current = values.GroupBy(value => value.RevisionId).Select(group => group.MaxBy(value => value.UpdatedAtUtc)!).ToArray();
        var saved = current.Where(row => row.SidecarReceipt is not null && row.JobState == (int)PublicJobState.Completed)
            .Select(row => (row.RevisionId, row.SourceRootId, Path: row.CanonicalPath + MediaTranscriptionGpuContracts.SidecarSuffix,
                Receipt: ReadSidecarReceipt(row.SidecarReceipt!)))
            .Where(row => row.Receipt is not null &&
                string.Equals(Path.GetFileName(row.Path), Path.GetFileName(row.Receipt.RelativePath), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(row => row.RevisionId);
        var result = current.ToDictionary(row => row.RevisionId, row => Project(row.Bound, row.Published, row.Reason,
            (PublicJobState?)row.JobState, row.JobReason, row.Uncertain, row.Permitted, row.ResultingPipelineRecordId));
        if (saved.Count == 0) return result;

        var hashes = saved.Values.Select(value => value.Receipt!.ContentSha256).Distinct().ToArray();
        var paths = saved.Values.Select(value => value.Path).Distinct().ToArray();
        var sidecars = await (
            from source in context.SourceRevisions.AsNoTracking()
            join record in context.PipelineRecords.AsNoTracking() on source.Id equals record.SourceRevisionId
            where paths.Contains(source.CanonicalPath) && hashes.Contains(source.ContentSha256) &&
                source.OriginKind == 0 && source.SuppressedAtUtc == null && source.Extension == ".txt" &&
                !record.IsDeleted && record.CompletionCriteriaMet && record.CurrentStage == (int)PipelineStage.Publish &&
                EF.Functions.Collate(record.ContentHash, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(source.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                context.Jobs.Any(job => job.PipelineRecordId == record.Id && job.SourceRevision == record.Revision &&
                    job.Stage == (int)PipelineStage.Extract && job.Operation == PipelineOperations.ExtractUtf8 &&
                    job.PublicState == (int)PublicJobState.Completed) &&
                context.Artifacts.Any(artifact => artifact.PipelineRecordId == record.Id &&
                    artifact.SourceRevision == record.Revision && artifact.Stage == (int)PipelineStage.Publish)
            select new { source.SourceRootId, source.CanonicalPath, source.ContentSha256, source.ByteLength, record.Id })
            .ToArrayAsync(token).ConfigureAwait(false);

        foreach (var row in saved.Values)
        {
            var records = sidecars.Where(sidecar => sidecar.SourceRootId == row.SourceRootId &&
                string.Equals(sidecar.CanonicalPath, row.Path, StringComparison.OrdinalIgnoreCase) &&
                sidecar.ContentSha256 == row.Receipt!.ContentSha256 && sidecar.ByteLength == row.Receipt.ByteLength).ToArray();
            result[row.RevisionId] = records.Length == 1
                ? new("Transcript indexed", "Saved beside the recording. Accuracy unverified.", records[0].Id)
                : new("Transcript saved beside recording", "Waiting for the source scanner to index the text file.");
        }
        return result;
    }

    private static TranscriptSidecarReceipt? ReadSidecarReceipt(string json)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<TranscriptSidecarReceipt>(json, JsonSerializerOptions.Web);
            return receipt is { RelativePath.Length: > 0, ContentSha256.Length: 64, ByteLength: > 0 } ? receipt : null;
        }
        catch (JsonException) { return null; }
    }

    internal static FileTranscriptionStatus Project(bool bound, bool published, string? reason, PublicJobState? jobState,
        string? jobReason, bool uncertain, bool permitted, Guid? recordId)
    {
        if (!bound)
        {
            return reason switch
            {
                "media-transcription-runtime-unavailable" => new("Waiting for transcription runtime", "The offline speech-to-text runtime is unavailable."),
                "media-adjacent-transcript-unverified" => new("Transcript needs review", "An adjacent caption file needs to be matched to this recording revision."),
                "media-transcription-existing-work-conflict" => new("Transcription needs review", "Existing work is already linked to this recording revision."),
                "media-source-current-ownership-unconfirmed" => new("Waiting for source verification", "The current recording revision has not been confirmed."),
                "media-transcription-input-too-large" => new("Recording exceeds the transcription size limit", "Review the source recording."),
                "media-transcription-format-unverified" => new("Recording format unconfirmed", "The retained content did not match a supported recording format."),
                "media-retained-input-unavailable" => new("Waiting for retained recording", "The retained recording could not be verified."),
                _ => new("Transcription binding unconfirmed", "Inspect the current recording and its extraction work.")
            };
        }

        if (uncertain) return new("Transcription needs attention", "The GPU worker’s outcome or cleanup needs verification.");
        if (published) return new("Transcript indexed", "Machine-generated transcript. Accuracy unverified.", recordId);
        return jobState switch
        {
            PublicJobState.GpuQueued => new("Transcription waiting for GPU", "Uses the shared GPU scheduler."),
            PublicJobState.GpuProcessing when permitted => new("Transcribing", "The GPU worker has permission to process this recording."),
            PublicJobState.GpuProcessing => new("Starting transcription", "The scheduler has reserved capacity for this recording."),
            PublicJobState.WorkerProcessing => new("Preparing transcription", null),
            PublicJobState.Completed => new("Transcript indexing", null),
            PublicJobState.Failed when jobReason == "transcription-sidecar-access-denied" =>
                new("Transcript could not be saved", "The app needs permission to create a transcript beside this recording."),
            PublicJobState.Failed when jobReason == "transcription-sidecar-existing-file-conflicts" =>
                new("Transcript filename already exists", "Review or rename the existing text file before saving the transcript."),
            PublicJobState.Failed when jobReason == "transcription-sidecar-source-policy" =>
                new("Transcript cannot be indexed from this folder", "Check this source’s file rules."),
            PublicJobState.Failed when jobReason == "transcription-no-speech-found" => new("No speech found", null),
            PublicJobState.Failed when jobReason == "transcription-no-audio-track" => new("No audio track", null),
            PublicJobState.Failed when jobReason == "transcription-recording-too-long" => new("Recording exceeds the transcription time limit", null),
            PublicJobState.Failed => new("Transcription needs attention", "Open the job details for the recorded failure."),
            _ => new("Transcription queued", null)
        };
    }
}
