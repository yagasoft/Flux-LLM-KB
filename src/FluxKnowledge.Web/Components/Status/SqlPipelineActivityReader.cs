using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

internal sealed class SqlPipelineActivityReader(IDeploymentValidationHold? hold, TimeProvider clock, SourceJournalOptions? journalOptions = null)
{
    public async ValueTask<PipelineActivitySummary> ReadAsync(FluxKnowledgeDbContext context,
        DateTimeOffset startedAt, CancellationToken cancellationToken, DeploymentHoldAdmission? capturedAdmission = null,
        bool materializeGpuFacts = false)
    {
        var now = startedAt;
        var roots = await context.SourceRootConfigurations.AsNoTracking().OrderBy(r => r.DisplayName).ThenBy(r => r.Id)
            .Select(r => new RootRow(r.Id, r.DisplayName, r.CanonicalPath, r.State, r.LastScanCompletedAtUtc, r.ReconciliationCadenceSeconds,
                context.SourceScanRequests.Where(request => request.SourceRootId == r.Id && request.RequestKind != SqlSourceVerificationStore.RequestKind &&
                        (request.State == (int)SourceScanRequestState.Completed || request.State == (int)SourceScanRequestState.Failed))
                    .OrderByDescending(request => request.RequestedAtUtc).ThenByDescending(request => request.Id)
                    .Select(request => (int?)request.ErrorFileCount).FirstOrDefault() ?? 0))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var sourceStates = await SourceRootProjectionReader.ReadStateSummariesAsync(context,
            roots.Select(r => r.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        await using var branches = await SqlSelectedBranchReadScope.CreateAsync(context,
            sourceStates.Values.SelectMany(s => s.SelectedBranches).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
        var admission = capturedAdmission ?? hold?.ReadAdmissionState() ?? new(false, null);
        var gpuEvidence = await SqlGpuActivityReadEvidence.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (materializeGpuFacts)
        {
            await branches.MaterializeGpuFactsAsync(now, gpuEvidence, cancellationToken).ConfigureAwait(false);
            await branches.MaterializeJobFactsAsync(now, admission, gpuEvidence, cancellationToken).ConfigureAwait(false);
        }
        var jobRows = await SqlPipelineActivityQuery.JobClassifications(context, now, admission, branches, gpuEvidence)
            .GroupBy(row => new
            {
                row.SourceRootId, row.Stage, row.PublicState, row.IsHistorical, row.RemovalCleanupBlocker, row.SourceAdmitted, row.HasWorkerLease,
                row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu, row.HasWaitingGpu, row.HasPendingDispatch, row.IsDue,
                Reason = row.Reason == "repository-source-blocked" || row.Reason == "repository-source-deferred" ||
                    row.Reason == "source-content-not-available-locally" || row.Reason == "index-storage-capacity-deferred" ||
                    row.Reason == "publication-snapshot-conflict" || row.Reason == "embedding-gpu-retry" ? row.Reason : null
            })
            .Select(group => new { group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var names = roots.ToDictionary(r => r.Id, r => r.Name);
        var jobs = jobRows.Select(group =>
        {
            var row = group.Key;
            var result = PipelineActivityClassifier.Classify(new((PublicJobState)row.PublicState, row.IsHistorical,
                row.SourceAdmitted, row.HasWorkerLease, row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu, row.IsDue,
                row.Reason, row.HasWaitingGpu, row.HasPendingDispatch, row.RemovalCleanupBlocker));
            var reason = row.SourceAdmitted && !row.IsHistorical &&
                result.State is PipelineWorkState.Waiting or PipelineWorkState.Queued or PipelineWorkState.Running &&
                row.Reason is "index-storage-capacity-deferred" or "publication-snapshot-conflict" or "embedding-gpu-retry"
                ? row.Reason : result.Reason;
            return new PipelineWorkGroup(row.SourceRootId, SourceName(row.SourceRootId, names),
                ((PipelineStage)row.Stage).ToString(), result.State, row.IsHistorical, reason, group.Count);
        }).GroupBy(j => new { j.SourceRootId, j.SourceName, j.Stage, j.State, j.IsHistorical, j.Reason })
            .Select(group => new PipelineWorkGroup(group.Key.SourceRootId, group.Key.SourceName, group.Key.Stage,
                group.Key.State, group.Key.IsHistorical, group.Key.Reason, group.Sum(j => j.Count)))
            .OrderBy(j => j.State).ThenBy(j => j.SourceName).ThenBy(j => j.Stage).ToArray();
        // This query is rooted in GpuMiniTasks. An empty committed image needs
        // neither its ownership joins nor their plan compilation.
        var gpuRows = !await context.GpuMiniTasks.AnyAsync(cancellationToken).ConfigureAwait(false) ? [] :
            await SqlPipelineActivityQuery.GpuTasks(context, now, admission, branches, gpuEvidence)
            .GroupBy(row => new { row.SourceRootId, row.State, row.IsInteractive })
            .Select(group => new { group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var gpu = gpuRows.Select(group => new GpuActivityGroup(group.Key.SourceRootId,
            group.Key.IsInteractive ? "Interactive search" : SourceName(group.Key.SourceRootId, names),
            group.Key.State, group.Count) { IsInteractive = group.Key.IsInteractive }).ToArray();

        var busyScanRoots = (from job in context.SourceScanJobs.AsNoTracking()
                join request in context.SourceScanRequests.AsNoTracking() on job.SourceScanRequestId equals request.Id
                where job.State == (int)SourceScanJobState.Running && job.LeaseExpiresAtUtc > now
                select new { SourceRootId = (Guid?)request.SourceRootId }).Distinct();
        var scans = await (from job in context.SourceScanJobs.AsNoTracking()
                join request in context.SourceScanRequests.AsNoTracking() on job.SourceScanRequestId equals request.Id
                join configuration in context.SourceRootConfigurations.AsNoTracking() on request.SourceRootId equals configuration.Id
                join busyRoot in busyScanRoots on (Guid?)request.SourceRootId equals busyRoot.SourceRootId into busyRoots
                from busyRoot in busyRoots.DefaultIfEmpty()
                where request.RequestKind != SqlSourceVerificationStore.RequestKind &&
                    (job.State == (int)SourceScanJobState.Running || job.State == (int)SourceScanJobState.Pending &&
                    (job.DueAtUtc <= now || request.RequestKind != 1 || !request.IsReleased || job.AttemptCount > 0 || job.Reason != null))
                select new { request.SourceRootId, job.State,
                    Confirmed = job.State == (int)SourceScanJobState.Running && job.LeaseOwner != null && job.LeaseGeneration > 0 && job.LeaseExpiresAtUtc > now,
                    Admitted = !admission.IsHeld && request.IsReleased && configuration.State == (int)SourceRootState.Enabled &&
                        (job.LeaseExpiresAtUtc == null || job.LeaseExpiresAtUtc <= now) &&
                        (configuration.CrawlMode != (int)SourceDiscoveryMode.GitTracked || busyRoot.SourceRootId == null),
                    IsDue = job.DueAtUtc <= now })
            .GroupBy(row => new { row.SourceRootId, row.State, row.Confirmed, row.Admitted, row.IsDue })
            .Select(group => new { group.Key, Count = group.Count() }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var processors = await (from branch in context.SourceProcessorBranches.AsNoTracking()
                join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
                where branch.State == (int)RetainedProcessorBranchState.Running
                select new { revision.SourceRootId, Confirmed = branch.LeaseOwner != null && branch.LeaseGeneration > 0 && branch.LeaseExpiresAtUtc > now,
                    IsCode = branch.ProcessorVersion == RetainedCsharpCodeProcessor.ProcessorVersion &&
                        branch.ProcessorFingerprint == RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint })
            .GroupBy(row => new { row.SourceRootId, row.Confirmed, row.IsCode })
            .Select(group => new { group.Key, Count = group.Count() }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var deletions = await context.SourceDeletionOperations.AsNoTracking()
            .ToDictionaryAsync(d => d.SourceRootId, cancellationToken).ConfigureAwait(false);
        var journalStates = await (from state in context.SourceRootScanStates.AsNoTracking()
                join checkpoint in context.SourceVolumeJournalCheckpoints.AsNoTracking() on state.VolumeIdentity equals checkpoint.VolumeIdentity into checkpoints
                from checkpoint in checkpoints.DefaultIfEmpty()
                select new { state.SourceRootId, state.VolumeIdentity, state.Mode, state.RecoveryRequired, state.LastReconciliationAtUtc,
                    HasCheckpoint = checkpoint != null, UnavailableSinceUtc = checkpoint == null ? null : checkpoint.UnavailableSinceUtc,
                    Reason = checkpoint == null ? state.Reason : checkpoint.LastFailureCode,
                    NextRetryAtUtc = checkpoint == null ? null : checkpoint.DueAtUtc })
            .ToDictionaryAsync(x => x.SourceRootId, cancellationToken).ConfigureAwait(false);
        var sources = roots.Select(root =>
        {
            var counts = sourceStates.GetValueOrDefault(root.Id) ?? SourceRootProjectionReader.SourceStateSummary.Empty;
            var journal = journalStates.GetValueOrDefault(root.Id);
            var approved = journalOptions is null || journal is not null &&
                (string.IsNullOrEmpty(journal.VolumeIdentity) || journalOptions.ApprovedVolumes.Contains(journal.VolumeIdentity, StringComparer.OrdinalIgnoreCase));
            return new SourcePipelineProgress(root.Id, root.Name, root.Path, ((SourceRootState)root.State).ToString(),
                counts.Known, counts.Indexed, counts.Pending, counts.Deferred, counts.Blocked, counts.Error,
                counts.MetadataOnly, counts.WithoutText, root.LastScanCompletedAtUtc,
                counts.LastDiscoveryAtUtc, counts.LastProgressAtUtc,
                scans.Any(s => s.Key.SourceRootId == root.Id && s.Key.Confirmed), deletions.GetValueOrDefault(root.Id)?.ReasonCode,
                processors.Where(p => p.Key.SourceRootId == root.Id && p.Key.Confirmed).Sum(p => p.Count), root.ScanErrors,
                deletions.TryGetValue(root.Id, out var deletion) && deletion.LeaseId != null && deletion.LeaseExpiresAtUtc > now ? 1 : 0,
                deletions.GetValueOrDefault(root.Id)?.Phase, counts.Provisional,
                scans.Where(s => s.Key.SourceRootId == root.Id && s.Key.State == (int)SourceScanJobState.Pending && s.Key.Admitted && s.Key.IsDue).Sum(s => s.Count),
                scans.Where(s => s.Key.SourceRootId == root.Id && s.Key.State == (int)SourceScanJobState.Pending && !(s.Key.Admitted && s.Key.IsDue)).Sum(s => s.Count),
                scans.Where(s => s.Key.SourceRootId == root.Id && s.Key.State == (int)SourceScanJobState.Running && !s.Key.Confirmed).Sum(s => s.Count) +
                    processors.Where(p => p.Key.SourceRootId == root.Id && !p.Key.Confirmed).Sum(p => p.Count))
            {
                JournalHealth = journal is null || journalOptions is { Enabled: false } ? null : new(approved ? journal.Mode : "Fallback",
                    !approved || journal.UnavailableSinceUtc is not null || !journal.HasCheckpoint && journal.Mode == "Fallback",
                    approved && (journal.RecoveryRequired || journal.Mode is "BaselineRequired" or "Baselining"),
                    approved ? journal.Reason : "journal-volume-not-approved",
                    approved ? journal.UnavailableSinceUtc : null, approved ? journal.NextRetryAtUtc : null,
                    (journal.LastReconciliationAtUtc ?? root.LastScanCompletedAtUtc)?.AddSeconds(root.ReconciliationCadenceSeconds),
                    root.ReconciliationCadenceSeconds)
            };
        }).ToArray();
        var jobQuery = SqlPipelineActivityQuery.Jobs(context, now, admission, branches, gpuEvidence);
        var files = await PipelineFileSample.ReadAsync(context, sourceStates, jobQuery, names,
            sources.ToDictionary(s => s.Id, s => s.Lifecycle), now, gpuEvidence, cancellationToken).ConfigureAwait(false);
        // The aggregate and details share the same committed image. Avoid repeating the
        // ownership queries when that image already confirms there are no running rows.
        var runningJobs = jobRows.Any(g => g.Key.HasActiveGpu || g.Key.PublicState == (int)PublicJobState.WorkerProcessing && g.Key.HasWorkerLease)
            ? await jobQuery.Where(j => j.HasActiveGpu || j.PublicState == (int)PublicJobState.WorkerProcessing && j.HasWorkerLease)
                .OrderBy(j => j.Id).Take(25).ToArrayAsync(cancellationToken).ConfigureAwait(false) : [];
        var interactive = gpuRows.Any(g => g.Key.IsInteractive && g.Key.State == "Active")
            ? await SqlPipelineActivityQuery.GpuTasks(context, now, admission, branches, gpuEvidence)
                .Where(t => t.IsInteractive && t.State == "Active").OrderBy(t => t.Id).Take(25)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false) : [];
        // Code parsing is a companion to source-text indexing. Its execution identity
        // must not change the selected text outcome or headline file stage.
        PipelineRunningTask[] codeTasks = [];
        var codeTaskCount = 0;
        if (processors.Any(p => p.Key.Confirmed && p.Key.IsCode))
        {
            var currentFiles = sourceStates.Values.SelectMany(s => s.Files)
                .Where(f => f.Category != "Unconfirmed" && roots.Any(r => r.Id == f.SourceRootId && r.State != (int)SourceRootState.Deleting))
                .ToDictionary(f => f.SourceRevisionId);
            var codeQuery = from branch in context.SourceProcessorBranches.AsNoTracking()
                join activity in SqlCurrentSourceActivities.Query(context) on branch.SourceActivityId equals activity.Id
                join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
                where branch.State == (int)RetainedProcessorBranchState.Running && branch.LeaseOwner != null && branch.LeaseGeneration > 0 && branch.LeaseExpiresAtUtc > now &&
                    branch.ProcessorVersion == RetainedCsharpCodeProcessor.ProcessorVersion && branch.ProcessorFingerprint == RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint &&
                    activity.SourceRevisionId == revision.Id && activity.State == (int)SourceActivityState.Pending && activity.ActivityKind == (int)SourceActivityKind.CodeParsing &&
                    activity.ExecutionClass == (int)ExecutionClass.InProcess && revision.Extension == ".cs" && revision.Classification == "AcceptedUtf8Text" &&
                    EF.Functions.Collate(activity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                    EF.Functions.Collate(revision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                    EF.Functions.Collate(activity.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(branch.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) &&
                    EF.Functions.Collate(activity.DescriptorFingerprint, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(branch.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation)
                select new { branch.Id, branch.SourceRevisionId };
            // Read only two IDs per confirmed live code claim, then apply the already
            // sampled file bindings. Sending the whole file inventory back to SQL
            // makes this small active-work lookup scale with discovered file count.
            var candidates = await codeQuery.OrderBy(b => b.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var rows = candidates.Where(b => currentFiles.ContainsKey(b.SourceRevisionId)).ToArray();
            codeTaskCount = rows.Length;
            codeTasks = rows.Take(25).Select(b =>
            {
                var file = currentFiles[b.SourceRevisionId];
                return new PipelineRunningTask(b.Id, file.SourceRootId, null, SourceName(file.SourceRootId, names),
                    file.FullPath, file.ArchivePath, "Code analysis", "Running", false, null);
            }).ToArray();
        }
        var fileAttention = files.Where(file => FileAttentionIssue.IsCurrent(file.File)).GroupBy(file => file.File.SourceRootId)
            .Select(group => new PipelineFileAttentionCounts(group.Key,
                group.Count(file => file.File.Category == "Blocked" && !file.File.IsIgnored),
                group.Count(file => file.File.Category == "Failed" && !file.File.IsIgnored),
                group.Count(file => file.File.Category == "Deferred" && !file.File.IsIgnored),
                group.Count(file => file.File.Category == "Unconfirmed"),
                group.Count(file => file.File.IsIgnored))).ToArray();
        var runningTasks = runningJobs.Select(j => new PipelineRunningTask(j.Id, j.SourceRootId, j.PipelineRecordId,
                SourceName(j.SourceRootId, names), j.FullPath, j.ArchivePath,
                ((PipelineStage)j.Stage) + (!j.MatchesSelectedFileBranch && !j.IsHistorical ? " (admitted index rebuild)" : ""),
                "Running", j.IsHistorical, j.AttemptStartedAtUtc))
            .Concat(interactive.Select(t => new PipelineRunningTask(t.Id, null, null, "Interactive search", "",
                null, "Query embedding", "Running", false, null)))
            .Concat(files.Where(f => f.ProcessorRunning)
                .Select(f => new PipelineRunningTask(f.File.SourceRevisionId, f.File.SourceRootId, null,
                    f.File.SourceName, f.File.FullPath, f.File.ArchivePath, f.File.Stage, "Running", false, null)))
            .Concat(codeTasks)
            .Take(25).ToArray();
        await branches.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return new(startedAt, clock.GetUtcNow(), sources, jobs, gpu,
            scans.Where(s => s.Key.Confirmed).Sum(s => s.Count), processors.Where(p => p.Key.Confirmed).Sum(p => p.Count),
            scans.Where(s => s.Key.State == (int)SourceScanJobState.Running && !s.Key.Confirmed).Sum(s => s.Count) + processors.Where(p => !p.Key.Confirmed).Sum(p => p.Count))
        {
            UncertainCapacitySlots = gpuEvidence.UncertainCapacitySlots,
            FileStages = files.Where(f => f.File.Category == "Pending")
                .GroupBy(f => new { f.File.SourceRootId, f.File.Stage, f.File.State })
                .Select(g => new PipelineFileStageGroup(g.Key.SourceRootId, g.Key.Stage, g.Key.State, g.Count())).ToArray(),
            FileAttention = fileAttention,
            FileCauses = files.Where(f => !f.File.IsIgnored && f.File.Category is "Blocked" or "Failed" or "Deferred")
                .GroupBy(f => new { f.File.SourceRootId, f.File.Category, f.File.Reason, f.File.NextAction })
                .Select(g => new PipelineFileCauseGroup(g.Key.SourceRootId, g.Key.Category, g.Key.Reason, g.Key.NextAction, g.Count()))
                .GroupBy(g => g.SourceRootId).SelectMany(g => g.OrderByDescending(c => c.Count).ThenBy(c => c.Reason).Take(20)).ToArray(),
            OcrDemand = files.GroupBy(f => f.File.SourceRootId).Select(g => new PipelineOcrDemand(g.Key,
                g.Count(f => f.RequiresOcr), g.Count(f => f.OcrWaiting), g.Count(f => f.OcrRunning),
                g.Count(f => f.OcrDecisionUnconfirmed), g.Where(f => f.RequiresOcr).All(f => f.RequestedPages is not null)
                    ? g.Where(f => f.RequiresOcr).Sum(f => f.RequestedPages) : null)).ToArray(),
            RunningTasks = runningTasks,
            RunningTaskCount = jobs.Where(j => j.State == PipelineWorkState.Running).Sum(j => j.Count) +
                gpu.Where(g => g.SourceRootId is null && g.SourceName == "Interactive search" && g.State == "Active").Sum(g => g.Count) +
                files.Count(f => f.ProcessorRunning) + codeTaskCount
        };
    }

    public async ValueTask<PipelineAttentionPage?> ReadAttentionAsync(FluxKnowledgeDbContext context, string unit,
        Guid? sourceRootId, int page, string? reason, bool snapshot, CancellationToken cancellationToken)
    {
        if (unit is not ("jobs" or "gpu" or "current-jobs" or "current-gpu" or "historical-jobs" or "historical-gpu"))
            throw new ArgumentException("Unknown attention counting unit.", nameof(unit));
        var before = snapshot ? new ActivityReadFence { IsStable = true }
            : await SqlPipelineActivityQuery.ReadFenceAsync(context, cancellationToken).ConfigureAwait(false);
        if (!before.IsStable) throw new InvalidOperationException("Status has a pending transaction.");
        var jobsRequested = unit.EndsWith("jobs", StringComparison.Ordinal);
        var names = await context.SourceRootConfigurations.AsNoTracking()
            .Select(r => new { r.Id, r.DisplayName })
            .ToDictionaryAsync(r => r.Id, r => r.DisplayName, cancellationToken).ConfigureAwait(false);
        // GPU job history can depend on a selected processor branch only
        // for Ready and OutcomeUncertain tasks whose logical owner has a branch.
        // Use the full existing source selection when any binding may apply.
        Guid[] selected = [];
        if (jobsRequested || await GpuMayHaveSelectedBranchAsync(context, sourceRootId, cancellationToken).ConfigureAwait(false))
        {
            var states = await SourceRootProjectionReader.ReadStateSummariesAsync(context, names.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
            selected = states.Values.SelectMany(s => s.SelectedBranches).Distinct().ToArray();
        }
        await using var branches = await SqlSelectedBranchReadScope.CreateAsync(context, selected, cancellationToken).ConfigureAwait(false);
        var admission = hold?.ReadAdmissionState() ?? new(false, null);
        var now = clock.GetUtcNow();
        var gpuEvidence = await SqlGpuActivityReadEvidence.ReadAsync(context, cancellationToken,
            jobsRequested ? SqlGpuActivityReadPurpose.Jobs : SqlGpuActivityReadPurpose.GpuTasks).ConfigureAwait(false);
        const int pageSize = 25;
        int total;
        PipelineAttentionItem[] items;
        PipelineAttentionReasonGroup[] reasons;
        if (jobsRequested)
        {
            if (snapshot)
            {
                await branches.MaterializeGpuFactsAsync(now, gpuEvidence, cancellationToken).ConfigureAwait(false);
                await branches.MaterializeJobFactsAsync(now, admission, gpuEvidence, cancellationToken).ConfigureAwait(false);
            }
            var query = SqlPipelineActivityQuery.Jobs(context, now, admission, branches, gpuEvidence);
            if (sourceRootId is { } root) query = query.Where(row => row.SourceRootId == root);
            if (unit == "historical-jobs") query = query.Where(row => row.IsHistorical &&
                (row.RemovalCleanupBlocker == 0 || row.HasActiveGpu ||
                 row.PublicState == (int)PublicJobState.WorkerProcessing && row.HasWorkerLease));
            if (unit == "current-jobs")
            {
                // Apply the Attention branches of PipelineActivityClassifier before counting and paging.
                query = query.Where(row => !row.HasActiveGpu &&
                    !(row.PublicState == (int)PublicJobState.WorkerProcessing && row.HasWorkerLease) &&
                    (row.RemovalCleanupBlocker != 0 || !row.IsHistorical &&
                    (row.PublicState == (int)PublicJobState.Failed ||
                     row.HasUncertainGpu && !row.HasReadyGpu && !row.HasWaitingGpu ||
                     row.Reason == "repository-source-blocked" ||
                     row.SourceAdmitted && row.Reason != "repository-source-deferred" && row.Reason != "source-content-not-available-locally" &&
                     (row.PublicState == (int)PublicJobState.WorkerProcessing ||
                      !row.HasWaitingGpu &&
                      ((row.PublicState == (int)PublicJobState.GpuQueued || row.PublicState == (int)PublicJobState.GpuProcessing) && !row.HasReadyGpu ||
                       row.PublicState == (int)PublicJobState.WorkerQueued && row.IsDue && !row.HasPendingDispatch)))));
            }
            var byReason = query.Select(row => new
            {
                Row = row,
                Reason = row.RemovalCleanupBlocker == 1 ? "worker-cleanup-unconfirmed-after-removal" :
                    row.RemovalCleanupBlocker == 2 ? "gpu-cleanup-unconfirmed-after-removal" :
                    row.HasActiveGpu || row.PublicState == (int)PublicJobState.WorkerProcessing && row.HasWorkerLease ? "execution-confirmed" :
                    row.IsHistorical ? row.Reason ?? "previous-source-revision" :
                    row.PublicState == (int)PublicJobState.Failed ? row.Reason ?? "job-failed" :
                    row.HasUncertainGpu && !row.HasReadyGpu && !row.HasWaitingGpu ? "gpu-outcome-unverified" :
                    row.PublicState == (int)PublicJobState.WorkerProcessing ? "worker-claim-unconfirmed" :
                    (row.PublicState == (int)PublicJobState.GpuQueued || row.PublicState == (int)PublicJobState.GpuProcessing) && !row.HasReadyGpu && !row.HasWaitingGpu ? "gpu-claim-unconfirmed" :
                    row.PublicState == (int)PublicJobState.WorkerQueued && row.IsDue && !row.HasPendingDispatch ? "dispatch-unconfirmed" :
                    row.Reason ?? "no-reason-recorded"
            });
            var groups = await byReason.GroupBy(entry => entry.Reason).Select(group => new { Reason = group.Key, Count = group.Count() })
                .OrderByDescending(group => group.Count).ThenBy(group => group.Reason).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            reasons = groups.Select(group => new PipelineAttentionReasonGroup(group.Reason, AttentionReasonLabel(group.Reason), group.Count)).ToArray();
            if (reason is not null) byReason = byReason.Where(entry => entry.Reason == reason);
            total = reason is null ? reasons.Sum(group => group.Count) : await byReason.CountAsync(cancellationToken).ConfigureAwait(false);
            page = Math.Clamp(page, 0, Math.Max(0, (total - 1) / pageSize));
            var rows = await byReason.OrderBy(entry => entry.Row.Id).Skip(page * pageSize).Take(pageSize).Select(entry => entry.Row)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            items = rows.Select(row => new PipelineAttentionItem(row.Id, row.SourceRootId, row.PipelineRecordId,
                SourceName(row.SourceRootId, names), "Pipeline job", ((PublicJobState)row.PublicState).ToString(),
                row.RemovalCleanupBlocker != 0 ? "Removed source" : row.IsHistorical ? "Historical" : "Current",
                (row.RemovalCleanupBlocker != 0 ? null : row.Reason) ?? PipelineActivityClassifier.Classify(
                    new((PublicJobState)row.PublicState, row.IsHistorical, row.SourceAdmitted, row.HasWorkerLease,
                        row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu, row.IsDue, row.Reason,
                        row.HasWaitingGpu, row.HasPendingDispatch, row.RemovalCleanupBlocker)).Reason, row.RecordedAtUtc)
                { Stage = ((PipelineStage)row.Stage).ToString() }).ToArray();
        }
        else
        {
            var query = SqlPipelineActivityQuery.GpuTasks(context, now, admission, branches, gpuEvidence)
                .Where(row => row.State == "Outcome uncertain" || row.State == "Ownership unconfirmed" || row.State == "Historical unresolved");
            if (sourceRootId is { } root) query = query.Where(row => row.SourceRootId == root);
            if (unit == "current-gpu") query = query.Where(row => row.State != "Historical unresolved");
            if (unit == "historical-gpu") query = query.Where(row => row.State == "Historical unresolved");
            var groups = await query.GroupBy(row => row.State).Select(group => new
                { Reason = group.Key, Count = group.Count(), Matches = reason == null || group.Key == reason })
                .OrderByDescending(group => group.Count).ThenBy(group => group.Reason).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            reasons = groups.Select(group => new PipelineAttentionReasonGroup(group.Reason, AttentionReasonLabel(group.Reason), group.Count)).ToArray();
            if (reason is not null) query = query.Where(row => row.State == reason);
            total = groups.Where(group => group.Matches).Sum(group => group.Count);
            page = Math.Clamp(page, 0, Math.Max(0, (total - 1) / pageSize));
            var rows = await query.OrderBy(row => row.Id).Skip(page * pageSize).Take(pageSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var released = JsonSerializer.Deserialize<HashSet<Guid>>(gpuEvidence.ReleasedOutcomesJson) ?? [];
            items = rows.Select(row => new PipelineAttentionItem(row.Id, row.SourceRootId, row.PipelineRecordId,
                row.IsInteractive ? "Interactive search" : SourceName(row.SourceRootId, names), "GPU task", row.State,
                row.State == "Historical unresolved" ? "Historical" : "Outcome or ownership unresolved",
                "Prior result and native cleanup require separate evidence before recovery.", row.CreatedAtUtc)
            {
                ResultStatus = row.ExecutionState == (int)GpuMiniTaskExecutionState.OutcomeUncertain
                    ? "Result could not be verified" : "No verified result shown in this sample",
                CapacityStatus = released.Contains(row.Id) ? "Scheduler release recorded" : "Release evidence unavailable in this sample"
            }).ToArray();
        }
        var after = snapshot ? before
            : await SqlPipelineActivityQuery.ReadFenceAsync(context, cancellationToken).ConfigureAwait(false);
        await branches.CompleteAsync(cancellationToken).ConfigureAwait(false);
        if (!before.Accepts(after)) return null;
        return new(total, page, pageSize, items) { Reasons = reasons };
    }

    private static Task<bool> GpuMayHaveSelectedBranchAsync(FluxKnowledgeDbContext context, Guid? sourceRootId,
        CancellationToken cancellationToken)
    {
        var candidates = from task in context.GpuMiniTasks.AsNoTracking()
            join job in context.Jobs.AsNoTracking() on task.ParentJobId equals (Guid?)job.Id
            join record in context.PipelineRecords.AsNoTracking() on job.PipelineRecordId equals record.Id
            join source in context.SourceRevisions.AsNoTracking() on record.SourceRevisionId equals (Guid?)source.Id
            where (sourceRootId == null || source.SourceRootId == sourceRootId) &&
                (task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready ||
                 task.ExecutionState == (int)GpuMiniTaskExecutionState.OutcomeUncertain)
            select source.OriginKind == 0 || source.OriginKind == 1 ? (Guid?)source.Id :
                source.OriginKind == 2 || source.OriginKind == 3 ? source.ParentSourceRevisionId : null;
        return candidates.AnyAsync(owner => owner != null &&
            context.SourceProcessorBranches.Any(branch => branch.SourceRevisionId == owner), cancellationToken);
    }

    private static string AttentionReasonLabel(string reason) => reason switch
    {
        "worker-cleanup-unconfirmed-after-removal" => "Worker cleanup unconfirmed after source removal",
        "gpu-cleanup-unconfirmed-after-removal" => "GPU cleanup unconfirmed after source removal",
        "worker-claim-unconfirmed" => "Worker claim could not be verified",
        "gpu-claim-unconfirmed" or "Ownership unconfirmed" => "GPU claim could not be verified",
        "gpu-outcome-unverified" or "Outcome uncertain" => "GPU result could not be verified",
        "dispatch-unconfirmed" => "Queue handoff could not be verified",
        "repository-source-blocked" => "Source processing is blocked",
        "execution-confirmed" => "Execution confirmed",
        "previous-source-revision" => "Previous source revision",
        "source-removed" => "Source removed",
        "source-revision-withdrawn" => "Source revision withdrawn",
        "Historical unresolved" => "Earlier attempt still has unresolved evidence",
        "job-failed" => "Processing failed",
        "no-reason-recorded" => "Reason not recorded",
        _ => FileAttentionIssue.Label(reason)
    };

    private static string SourceName(Guid? root, IReadOnlyDictionary<Guid, string> names) =>
        root is { } id ? names.GetValueOrDefault(id) ?? "Source binding unavailable" : "Local ingress";

    private sealed record RootRow(Guid Id, string Name, string Path, int State, DateTimeOffset? LastScanCompletedAtUtc, long ReconciliationCadenceSeconds, int ScanErrors);
}
