using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

/// <summary>One current logical-file outcome, shared by stage summaries and bounded drilldowns.</summary>
internal sealed record PipelineFileSample(PipelineFileItem File, bool RequiresOcr, bool OcrRunning,
    bool OcrWaiting, bool OcrDecisionUnconfirmed, int? RequestedPages, bool ProcessorRunning = false)
{
    internal static async ValueTask<IReadOnlyList<PipelineFileSample>> ReadAsync(FluxKnowledgeDbContext context,
        IReadOnlyDictionary<Guid, SourceRootProjectionReader.SourceStateSummary> sources,
        IQueryable<PipelineJobActivityRow> jobs, IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string> lifecycles, DateTimeOffset now, SqlGpuActivityReadEvidence gpuEvidence,
        CancellationToken cancellationToken)
    {
        var files = sources.Values.SelectMany(s => s.Files).ToArray();
        if (files.Length == 0) return [];
        var rootIds = sources.Keys.ToArray();
        var currentJobs = await jobs.Where(j => !j.IsHistorical && j.MatchesSelectedFileBranch && j.Stage == j.CurrentStage && j.LogicalSourceRevisionId != null &&
                j.SourceRootId != null && rootIds.Contains(j.SourceRootId.Value))
            // Paths come from the sampled logical file. Do not transfer the repeated
            // paths or calculate the unused historical-attempt maximum for every job.
            .Select(j => new PipelineJobActivityRow
            {
                Id = j.Id, PipelineRecordId = j.PipelineRecordId, LogicalSourceRevisionId = j.LogicalSourceRevisionId,
                SourceRevision = j.SourceRevision, Stage = j.Stage, CurrentStage = j.CurrentStage,
                MatchesSelectedFileBranch = j.MatchesSelectedFileBranch, IsHistorical = j.IsHistorical,
                PublicState = j.PublicState, SourceAdmitted = j.SourceAdmitted, HasWorkerLease = j.HasWorkerLease,
                HasActiveGpu = j.HasActiveGpu, HasReadyGpu = j.HasReadyGpu, HasUncertainGpu = j.HasUncertainGpu,
                HasWaitingGpu = j.HasWaitingGpu, HasPendingDispatch = j.HasPendingDispatch, IsDue = j.IsDue,
                Reason = j.Reason, AttemptStartedAtUtc = j.AttemptStartedAtUtc
            })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var selected = sources.Values.SelectMany(s => s.SelectedBranches).Distinct().ToArray();
        var processors = selected.Length == 0 ? [] : await context.SourceProcessorBranches.AsNoTracking()
            .Where(b => selected.Contains(b.Id) && (b.State == 0 || b.State == 1))
            .Select(b => new PipelineFileProcessorRow(b.Id, b.SourceRevisionId,
                b.State == 1 && b.LeaseOwner != null && b.LeaseGeneration > 0 && b.LeaseExpiresAtUtc > now,
                b.State == 1 && (b.LeaseOwner == null || b.LeaseGeneration <= 0 || b.LeaseExpiresAtUtc == null || b.LeaseExpiresAtUtc <= now)))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Requested page indexes are small scalar demand metadata. No OCR result/content is selected.
        const string ocrSql = """
            SELECT p.Id AS PipelineRecordId, p.Revision AS SourceRevision,
              CAST(MAX(CASE WHEN request.State = 0 THEN 1 ELSE 0 END) AS bit) AS HasPending,
              CAST(MAX(CASE WHEN request.State = 0 AND task.State = 1 AND proof.HasActive = 1
                THEN 1 ELSE 0 END) AS bit) AS HasActive,
              CASE WHEN COUNT(DISTINCT TRY_CONVERT(int, page.[value])) = 0 OR MAX(CASE WHEN ISJSON(request.RequestedPageIndexesJson) <> 1
                OR LEFT(LTRIM(request.RequestedPageIndexesJson), 1) <> '['
                OR page.[value] IS NOT NULL AND (page.[type] <> 2 OR TRY_CONVERT(int, page.[value]) IS NULL
                  OR TRY_CONVERT(int, page.[value]) < 0) THEN 1 ELSE 0 END) = 1
                THEN NULL ELSE COUNT(DISTINCT TRY_CONVERT(int, page.[value])) END AS RequestedPages
            FROM DocumentOcrRequests request
            INNER JOIN PipelineRecords p ON p.Id = request.PipelineRecordId AND p.Revision = request.SourceRevision
              AND p.SourceRevisionId = request.RetainedSourceRevisionId
              AND p.ContentHash COLLATE Latin1_General_100_BIN2 = request.ContentSha256 COLLATE Latin1_General_100_BIN2
            INNER JOIN SourceRevisions retained ON retained.Id = p.SourceRevisionId
            INNER JOIN OPENJSON(@roots) WITH (Id uniqueidentifier '$') root ON root.Id = retained.SourceRootId
            INNER JOIN Jobs parent ON parent.Id = request.ParentJobId AND parent.PipelineRecordId = p.Id
              AND parent.SourceRevision = p.Revision AND parent.Stage = 1
            INNER JOIN GpuMiniTasks task ON task.Id = request.MiniTaskId AND task.ParentJobId = parent.Id
              AND task.SourceRevision = p.Revision
              AND task.ModelRuntimeKey COLLATE Latin1_General_100_BIN2 = request.ModelRuntimeKey COLLATE Latin1_General_100_BIN2
              AND task.SettingsFingerprint COLLATE Latin1_General_100_BIN2 = request.SettingsFingerprint COLLATE Latin1_General_100_BIN2
            LEFT JOIN OPENJSON(@ownedGpuTasks) WITH (Id uniqueidentifier '$.Id', BatchId uniqueidentifier '$.BatchId',
              AdmissionGeneration bigint '$.AdmissionGeneration', HasActive bit '$.HasActive') proof
              ON proof.Id = task.Id AND proof.BatchId = task.BatchId AND proof.AdmissionGeneration = task.AdmissionGeneration
            OUTER APPLY OPENJSON(CASE WHEN ISJSON(request.RequestedPageIndexesJson) = 1
              THEN request.RequestedPageIndexesJson ELSE N'[]' END) page
            WHERE p.IsDeleted = 0 AND p.CompletionCriteriaMet = 0
            GROUP BY p.Id, p.Revision
            """;
        var ocr = files.Any(f => f.Category == "Pending")
            ? await context.Database.SqlQueryRaw<PipelineFileOcrRow>(ocrSql,
                new Microsoft.Data.SqlClient.SqlParameter("@roots", System.Data.SqlDbType.NVarChar, -1)
                    { Value = System.Text.Json.JsonSerializer.Serialize(rootIds) },
                new Microsoft.Data.SqlClient.SqlParameter("@ownedGpuTasks", System.Data.SqlDbType.NVarChar, -1)
                    { Value = gpuEvidence.OwnedTasksJson }).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var projected = Project(files, currentJobs, processors, ocr, lifecycles);
        return projected.Select(sample => sample with { File = sample.File with
            { SourceName = names.GetValueOrDefault(sample.File.SourceRootId) ?? "Source binding unavailable" } }).ToArray();
    }

    internal static IReadOnlyList<PipelineFileSample> Project(IReadOnlyList<SourceLogicalFileState> files,
        IReadOnlyList<PipelineJobActivityRow> jobs, IReadOnlyList<PipelineFileProcessorRow> processors,
        IReadOnlyList<PipelineFileOcrRow> ocr, IReadOnlyDictionary<Guid, string> lifecycles)
    {
        var jobsByFile = jobs.Where(j => !j.IsHistorical && j.MatchesSelectedFileBranch && j.Stage == j.CurrentStage && j.LogicalSourceRevisionId != null)
            .GroupBy(j => j.LogicalSourceRevisionId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        var processorsByBranch = processors.ToDictionary(p => p.BranchId);
        var ocrByRecord = ocr.ToDictionary(o => (o.PipelineRecordId, o.SourceRevision));
        return files.Select(file =>
        {
            var current = file.Category is "Pending" or "Failed" or "Blocked" or "Deferred"
                ? jobsByFile.GetValueOrDefault(file.SourceRevisionId) ?? [] : [];
            var selected = PipelineFilePresentation.SelectJob(current);
            var demands = current.Where(_ => file.Category == "Pending").Select(j => ocrByRecord.GetValueOrDefault((j.PipelineRecordId, j.SourceRevision)))
                .Where(o => o is not null).Select(o => o!).DistinctBy(o => o.PipelineRecordId).ToArray();
            var requiresOcr = demands.Length > 0;
            var pendingOcr = demands.Any(o => o.HasPending);
            var ocrRunning = pendingOcr && demands.Any(d => d.HasActive);
            var branch = file.SelectedBranchId is { } branchId ? processorsByBranch.GetValueOrDefault(branchId) : null;
            var preferProcessor = branch?.Running == true && (selected is null || PipelineFilePresentation.Classify(selected).State != PipelineWorkState.Running);
            var stage = selected is null ? branch?.Running == true ? "Extracting" : branch?.OwnershipUnconfirmed == true ? "Stage unconfirmed" : "Awaiting extraction" : selected.Stage switch
            {
                (int)PipelineStage.Extract when pendingOcr => ocrRunning ? "OCR running" : "Awaiting OCR",
                (int)PipelineStage.Extract => PipelineFilePresentation.Classify(selected).State == PipelineWorkState.Running ? "Extracting" : "Awaiting extraction",
                (int)PipelineStage.Normalise or (int)PipelineStage.CanonicalIndex => "Normalising/chunking",
                (int)PipelineStage.Embed => "Embedding",
                (int)PipelineStage.Publish => "Publishing",
                (int)PipelineStage.Identify => "Awaiting extraction",
                _ => "Stage unconfirmed"
            };
            var state = selected is null ? branch?.Running == true ? "Running" : branch?.OwnershipUnconfirmed == true ? "Needs attention" : "Waiting for dependency"
                : PipelineFilePresentation.WorkState(selected);
            var reason = file.Reason ?? (selected is null
                ? branch?.Running == true ? "Retained processor lease confirmed." : branch?.OwnershipUnconfirmed == true
                    ? "Retained processor execution ownership is unconfirmed." : "Awaiting a bound pipeline job or retained processor."
                : selected.Reason ?? PipelineFilePresentation.Classify(selected).Reason);
            if (preferProcessor) { stage = "Extracting"; state = "Running"; reason = file.Reason ?? "Retained processor lease confirmed; other current jobs are shown in details."; }
            if (file.Category != "Pending")
            {
                stage = file.Category == "Unconfirmed" ? "Stage unconfirmed" : selected is null ? "Complete or held" : stage;
                state = file.Category;
                reason = file.Reason ?? selected?.Reason ?? (file.Category == "Published" ? "Extracted text has completed indexing." : "No current reason recorded.");
            }
            else if (lifecycles.TryGetValue(file.SourceRootId, out var lifecycle) && lifecycle != "Enabled" && state != "Running")
            {
                state = "Waiting for dependency";
                reason = $"Source is {lifecycle}. {reason}";
            }
            var jobItems = current.OrderBy(j => j.Stage).ThenBy(j => j.Id).Select(j => new PipelineFileJob(j.Id,
                j.PipelineRecordId, ((PipelineStage)j.Stage).ToString(), PipelineFilePresentation.WorkState(j),
                j.Reason ?? PipelineFilePresentation.Classify(j).Reason, j.AttemptStartedAtUtc)).ToArray();
            return new PipelineFileSample(new(file.SourceRootId, file.SourceRevisionId, "", file.FullPath,
                file.ArchivePath, file.Category, stage, state, reason, PipelineFilePresentation.NextAction(file.Reason ?? selected?.Reason),
                jobItems, []), requiresOcr, ocrRunning, pendingOcr && !ocrRunning,
                file.Category == "Pending" && !requiresOcr && file.Classification is "" or "PdfDocumentContainer" or "ImageDocumentContainer" &&
                    (selected is null || selected.Stage <= (int)PipelineStage.Extract),
                requiresOcr && demands.All(o => o.RequestedPages is not null) ? demands.Sum(o => o.RequestedPages) : null,
                file.Category == "Pending" && branch?.Running == true);
        }).ToArray();
    }
}

internal sealed record PipelineFileProcessorRow(Guid BranchId, Guid SourceRevisionId, bool Running, bool OwnershipUnconfirmed = false);
internal sealed class PipelineFileOcrRow
{
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public bool HasPending { get; set; }
    public bool HasActive { get; set; }
    public int? RequestedPages { get; set; }
}
