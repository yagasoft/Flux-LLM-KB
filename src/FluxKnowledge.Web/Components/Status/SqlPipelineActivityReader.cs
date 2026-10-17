using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

internal sealed class SqlPipelineActivityReader(IDeploymentValidationHold? hold, TimeProvider clock)
{
    public async ValueTask<PipelineActivitySummary> ReadAsync(FluxKnowledgeDbContext context,
        DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var roots = await context.SourceRootConfigurations.AsNoTracking().OrderBy(r => r.DisplayName).ThenBy(r => r.Id)
            .Select(r => new RootRow(r.Id, r.DisplayName, r.CanonicalPath, r.State, r.LastScanCompletedAtUtc,
                context.SourceScanRequests.Where(request => request.SourceRootId == r.Id &&
                        (request.State == (int)SourceScanRequestState.Completed || request.State == (int)SourceScanRequestState.Failed))
                    .OrderByDescending(request => request.RequestedAtUtc).ThenByDescending(request => request.Id)
                    .Select(request => (int?)request.ErrorFileCount).FirstOrDefault() ?? 0))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var sourceStates = await SourceRootProjectionReader.ReadStateSummariesAsync(context,
            roots.Select(r => r.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        var branches = JsonSerializer.Serialize(sourceStates.Values.SelectMany(s => s.SelectedBranches).Distinct());
        var admission = hold?.ReadAdmissionState() ?? new(false, null);
        var gpuEvidence = await SqlGpuActivityReadEvidence.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        var jobRows = await SqlPipelineActivityQuery.Jobs(context, now, admission, branches, gpuEvidence)
            .GroupBy(row => new
            {
                row.SourceRootId, row.Stage, row.PublicState, row.IsHistorical, row.SourceAdmitted, row.HasWorkerLease,
                row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu, row.HasWaitingGpu, row.HasPendingDispatch, row.IsDue,
                Reason = row.Reason == "repository-source-blocked" || row.Reason == "repository-source-deferred" ||
                    row.Reason == "source-content-not-available-locally" ? row.Reason : null
            })
            .Select(group => new { group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var names = roots.ToDictionary(r => r.Id, r => r.Name);
        var jobs = jobRows.Select(group =>
        {
            var row = group.Key;
            var result = PipelineActivityClassifier.Classify(new((PublicJobState)row.PublicState, row.IsHistorical,
                row.SourceAdmitted, row.HasWorkerLease, row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu, row.IsDue,
                row.Reason, row.HasWaitingGpu, row.HasPendingDispatch));
            return new PipelineWorkGroup(row.SourceRootId, SourceName(row.SourceRootId, names),
                ((PipelineStage)row.Stage).ToString(), result.State, row.IsHistorical, result.Reason, group.Count);
        }).GroupBy(j => new { j.SourceRootId, j.SourceName, j.Stage, j.State, j.IsHistorical, j.Reason })
            .Select(group => new PipelineWorkGroup(group.Key.SourceRootId, group.Key.SourceName, group.Key.Stage,
                group.Key.State, group.Key.IsHistorical, group.Key.Reason, group.Sum(j => j.Count)))
            .OrderBy(j => j.State).ThenBy(j => j.SourceName).ThenBy(j => j.Stage).ToArray();
        var gpuRows = await SqlPipelineActivityQuery.GpuTasks(context, now, admission, branches, gpuEvidence)
            .GroupBy(row => new { row.SourceRootId, row.State, row.IsInteractive })
            .Select(group => new { group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var gpu = gpuRows.Select(group => new GpuActivityGroup(group.Key.SourceRootId,
            group.Key.IsInteractive ? "Interactive search" : SourceName(group.Key.SourceRootId, names),
            group.Key.State, group.Count)).ToArray();

        var busyScanRoots = (from job in context.SourceScanJobs.AsNoTracking()
                join request in context.SourceScanRequests.AsNoTracking() on job.SourceScanRequestId equals request.Id
                where job.State == (int)SourceScanJobState.Running && job.LeaseExpiresAtUtc > now
                select new { SourceRootId = (Guid?)request.SourceRootId }).Distinct();
        var scans = await (from job in context.SourceScanJobs.AsNoTracking()
                join request in context.SourceScanRequests.AsNoTracking() on job.SourceScanRequestId equals request.Id
                join configuration in context.SourceRootConfigurations.AsNoTracking() on request.SourceRootId equals configuration.Id
                join busyRoot in busyScanRoots on (Guid?)request.SourceRootId equals busyRoot.SourceRootId into busyRoots
                from busyRoot in busyRoots.DefaultIfEmpty()
                where job.State == (int)SourceScanJobState.Running || job.State == (int)SourceScanJobState.Pending &&
                    (job.DueAtUtc <= now || request.RequestKind != 1 || !request.IsReleased || job.AttemptCount > 0 || job.Reason != null)
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
                select new { revision.SourceRootId, Confirmed = branch.LeaseOwner != null && branch.LeaseGeneration > 0 && branch.LeaseExpiresAtUtc > now })
            .GroupBy(row => new { row.SourceRootId, row.Confirmed })
            .Select(group => new { group.Key, Count = group.Count() }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var deletions = await context.SourceDeletionOperations.AsNoTracking()
            .ToDictionaryAsync(d => d.SourceRootId, cancellationToken).ConfigureAwait(false);
        var sources = roots.Select(root =>
        {
            var counts = sourceStates.GetValueOrDefault(root.Id) ?? SourceRootProjectionReader.SourceStateSummary.Empty;
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
                    processors.Where(p => p.Key.SourceRootId == root.Id && !p.Key.Confirmed).Sum(p => p.Count));
        }).ToArray();
        return new(startedAt, clock.GetUtcNow(), sources, jobs, gpu,
            scans.Where(s => s.Key.Confirmed).Sum(s => s.Count), processors.Where(p => p.Key.Confirmed).Sum(p => p.Count),
            scans.Where(s => s.Key.State == (int)SourceScanJobState.Running && !s.Key.Confirmed).Sum(s => s.Count) + processors.Where(p => !p.Key.Confirmed).Sum(p => p.Count))
        {
            UncertainCapacitySlots = gpuEvidence.UncertainCapacitySlots
        };
    }

    public async ValueTask<PipelineAttentionPage?> ReadAttentionAsync(FluxKnowledgeDbContext context, string unit,
        Guid? sourceRootId, int page, CancellationToken cancellationToken)
    {
        if (unit is not ("jobs" or "gpu")) throw new ArgumentException("Unknown attention counting unit.", nameof(unit));
        var before = await SqlPipelineActivityQuery.ReadFenceAsync(context, cancellationToken).ConfigureAwait(false);
        if (!before.IsStable) throw new InvalidOperationException("Status has a pending transaction.");
        var names = await context.SourceRootConfigurations.AsNoTracking()
            .ToDictionaryAsync(r => r.Id, r => r.DisplayName, cancellationToken).ConfigureAwait(false);
        var states = await SourceRootProjectionReader.ReadStateSummariesAsync(context, names.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
        var branches = JsonSerializer.Serialize(states.Values.SelectMany(s => s.SelectedBranches).Distinct());
        var admission = hold?.ReadAdmissionState() ?? new(false, null);
        var now = clock.GetUtcNow();
        var gpuEvidence = await SqlGpuActivityReadEvidence.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        const int pageSize = 25;
        int total;
        PipelineAttentionItem[] items;
        if (unit == "jobs")
        {
            var query = SqlPipelineActivityQuery.Jobs(context, now, admission, branches, gpuEvidence);
            if (sourceRootId is { } root) query = query.Where(row => row.SourceRootId == root);
            total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
            page = Math.Clamp(page, 0, Math.Max(0, (total - 1) / pageSize));
            var rows = await query.OrderBy(row => row.Id).Skip(page * pageSize).Take(pageSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            items = rows.Select(row => new PipelineAttentionItem(row.Id, row.SourceRootId, row.PipelineRecordId,
                SourceName(row.SourceRootId, names), "Pipeline job", ((PublicJobState)row.PublicState).ToString(),
                row.IsHistorical ? "Historical" : "Current", row.Reason ?? PipelineActivityClassifier.Classify(
                    new((PublicJobState)row.PublicState, row.IsHistorical, row.SourceAdmitted, row.HasWorkerLease,
                        row.HasActiveGpu, row.HasReadyGpu, row.HasUncertainGpu, row.IsDue, row.Reason,
                        row.HasWaitingGpu, row.HasPendingDispatch)).Reason, row.RecordedAtUtc)).ToArray();
        }
        else
        {
            var query = SqlPipelineActivityQuery.GpuTasks(context, now, admission, branches, gpuEvidence)
                .Where(row => row.State == "Outcome uncertain" || row.State == "Ownership unconfirmed" || row.State == "Historical unresolved");
            if (sourceRootId is { } root) query = query.Where(row => row.SourceRootId == root);
            total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
            page = Math.Clamp(page, 0, Math.Max(0, (total - 1) / pageSize));
            var rows = await query.OrderBy(row => row.Id).Skip(page * pageSize).Take(pageSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            items = rows.Select(row => new PipelineAttentionItem(row.Id, row.SourceRootId, row.PipelineRecordId,
                row.IsInteractive ? "Interactive search" : SourceName(row.SourceRootId, names), "GPU task", row.State,
                row.State == "Historical unresolved" ? "Historical" : "Outcome or ownership unresolved",
                "Prior result and native cleanup require separate evidence before recovery.", row.CreatedAtUtc)).ToArray();
        }
        var after = await SqlPipelineActivityQuery.ReadFenceAsync(context, cancellationToken).ConfigureAwait(false);
        if (!before.Accepts(after)) return null;
        return new(total, page, pageSize, items);
    }

    private static string SourceName(Guid? root, IReadOnlyDictionary<Guid, string> names) =>
        root is { } id ? names.GetValueOrDefault(id) ?? "Source binding unavailable" : "Local ingress";

    private sealed record RootRow(Guid Id, string Name, string Path, int State, DateTimeOffset? LastScanCompletedAtUtc, int ScanErrors);
}
