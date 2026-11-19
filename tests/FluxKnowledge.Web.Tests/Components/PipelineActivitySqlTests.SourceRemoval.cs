using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GPU_history_preserves_the_selected_branch_of_an_archive_member_or_document_owner(int origin)
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var outer = Revision(Guid.NewGuid(), root, "owner.zip", now, suppressed: false);
        var input = Revision(Guid.NewGuid(), root, "member.pdf", now, suppressed: false);
        input.OriginKind = origin;
        input.ParentSourceRevisionId = outer.Id;
        await using var db = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        db.SourceRevisions.Add(outer);
        await db.SaveChangesAsync();
        var recordId = await SeedActivityJobAsync(root, now, PublicJobState.GpuProcessing, null, source: input);
        var job = await db.Jobs.SingleAsync(item => item.PipelineRecordId == recordId);
        var task = new GpuMiniTaskEntity
        {
            Id = Guid.NewGuid(), ParentJobId = job.Id, SourceRevision = job.SourceRevision, ModelRuntimeKey = "synthetic",
            SettingsFingerprint = "synthetic", IdempotencyKey = $"selected-gpu:{job.Id:N}", EstimatedBytes = 1,
            AdmissionGeneration = 1, ReservationAttemptCount = 1, ExecutionState = (int)GpuMiniTaskExecutionState.OutcomeUncertain,
            CreatedAtUtc = now
        };
        db.GpuMiniTasks.Add(task);
        await db.SaveChangesAsync();
        var reader = SnapshotReader();
        Assert.Equal(task.Id, Assert.Single((await reader.ReadPipelineAttentionAsync("current-gpu", root, 0, default)).Items).Id);

        var owner = origin == 1 ? input : outer;
        var activity = Activity(owner.Id, SourceActivityState.Pending);
        activity.ProcessorVersion = PdfDocumentProcessor.Capability.ProcessorVersion;
        activity.DescriptorFingerprint = PdfDocumentProcessor.Capability.ProcessorFingerprint;
        activity.InputFingerprint = owner.ContentSha256;
        db.SourceActivities.Add(activity);
        db.SourceProcessorBranches.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = owner.Id, SourceActivityId = activity.Id,
            InputSha256 = owner.ContentSha256, ProcessorVersion = activity.ProcessorVersion, ProcessorFingerprint = activity.DescriptorFingerprint,
            State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();

        var history = await reader.ReadPipelineAttentionAsync("historical-gpu", root, 0, "Historical unresolved", default);
        Assert.Equal(1, history.TotalCount);
        Assert.Equal(task.Id, Assert.Single(history.Items).Id);
        Assert.Empty((await reader.ReadPipelineAttentionAsync("current-gpu", root, 0, default)).Items);
        const string alternateReason = "historical unresolved ";
        var collationMatches = await db.Database.SqlQuery<bool>($"""
            SELECT CAST(CASE WHEN N'Historical unresolved' = {alternateReason} THEN 1 ELSE 0 END AS bit) AS [Value]
            """).SingleAsync();
        var filtered = await reader.ReadPipelineAttentionAsync("historical-gpu", root, 99, alternateReason, default);
        Assert.Equal(collationMatches ? 1 : 0, filtered.TotalCount);
        Assert.Equal(filtered.TotalCount, filtered.Items.Count);
        Assert.Equal(1, Assert.Single(filtered.Reasons).Count);
        Assert.Equal(0, filtered.Page);
    }

    [NativeSqlServerFact]
    public async Task Removed_source_cleanup_blockers_remain_in_current_attention_after_other_removed_work_retires()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var workerRecord = await SeedActivityJobAsync(root, now, PublicJobState.WorkerProcessing,
            now.AddSeconds(-1), leaseGeneration: 1, source: Removed("worker.txt"));
        var gpuRecord = await SeedActivityJobAsync(root, now, PublicJobState.GpuProcessing, null, source: Removed("gpu.txt"));
        var terminalGpuRecord = await SeedActivityJobAsync(root, now, PublicJobState.GpuProcessing, null,
            source: Removed("terminal-gpu.txt"));
        var retiredRecord = await SeedActivityJobAsync(root, now, PublicJobState.Failed, null, source: Removed("retired.txt"));
        var earlierRetiredRecord = await SeedActivityJobAsync(root, now, PublicJobState.Failed, null,
            leaseGeneration: 1, source: Removed("earlier-retired.txt"));
        var excluded = Revision(Guid.NewGuid(), root, "excluded.txt", now, suppressed: true);
        excluded.RetentionEvidenceJson = JsonSerializer.Serialize(new
        {
            Version = 1, SourceRootId = root, SourceRevisionId = excluded.Id, SuppressedAtUtc = now,
            Category = "OutOfScope", LeaseGeneration = 1, ManifestId = Guid.NewGuid(), ConfigurationRevision = 1,
            RelativePath = "excluded.txt", SourceScanRequestId = Guid.NewGuid(), reason = "unseen-during-authoritative-scan"
        });
        var historicalRecord = await SeedActivityJobAsync(root, now, PublicJobState.WorkerProcessing,
            now.AddSeconds(-1), leaseGeneration: 1, source: excluded);
        var legacy = Revision(Guid.NewGuid(), root, "legacy.txt", now, suppressed: true);
        legacy.RetentionEvidenceJson = "{\"reason\":\"unseen-during-authoritative-scan\"}";
        var legacyRecord = await SeedActivityJobAsync(root, now, PublicJobState.WorkerProcessing,
            now.AddSeconds(-1), leaseGeneration: 1, source: legacy);
        await using var db = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var gpuJob = await db.Jobs.SingleAsync(job => job.PipelineRecordId == gpuRecord);
        var terminalGpuJob = await db.Jobs.SingleAsync(job => job.PipelineRecordId == terminalGpuRecord);
        var retiredJob = await db.Jobs.SingleAsync(job => job.PipelineRecordId == retiredRecord);
        retiredJob.Reason = "source-removed";
        var earlierRetiredJob = await db.Jobs.SingleAsync(job => job.PipelineRecordId == earlierRetiredRecord);
        earlierRetiredJob.Reason = "source-removed";
        var unconfirmedDelivery = await db.OutboxMessages.SingleAsync(delivery => delivery.JobId == earlierRetiredJob.Id);
        unconfirmedDelivery.LeaseOwner = "earlier-worker";
        unconfirmedDelivery.LeaseGeneration = 1;
        unconfirmedDelivery.LeaseExpiresAtUtc = now.AddSeconds(-1);
        GpuMiniTaskEntity GpuTask(Guid jobId, GpuMiniTaskExecutionState state) => new()
        {
            Id = Guid.NewGuid(), ParentJobId = jobId, SourceRevision = 1, ModelRuntimeKey = "synthetic",
            SettingsFingerprint = "synthetic", IdempotencyKey = $"removed-source:{jobId:N}", EstimatedBytes = 1,
            AdmissionGeneration = 1, ReservationAttemptCount = 1, ExecutionState = (int)state,
            CreatedAtUtc = now
        };
        var gpu = GpuTask(gpuJob.Id, GpuMiniTaskExecutionState.OutcomeUncertain);
        db.GpuMiniTasks.AddRange(gpu, GpuTask(terminalGpuJob.Id, GpuMiniTaskExecutionState.Completed));
        await db.SaveChangesAsync();

        var reader = ActivityReader();
        var snapshot = await reader.ReadOverviewAsync(default);
        var view = PipelineActivityView.From(snapshot.Activity, root);
        Assert.Equal(4, view.AttentionJobs);
        Assert.Equal(1, view.UncertainGpuTasks);
        Assert.Equal(0, view.QueuedJobs);
        var jobs = await reader.ReadPipelineAttentionAsync("current-jobs", root, 0, default);
        Assert.Equal(4, jobs.Items.Count);
        Assert.Contains(jobs.Items, item => item.PipelineRecordId == workerRecord);
        Assert.Contains(jobs.Items, item => item.PipelineRecordId == earlierRetiredRecord);
        Assert.Contains(jobs.Items, item => item.PipelineRecordId == legacyRecord);
        Assert.Contains(jobs.Items, item => item.PipelineRecordId == terminalGpuRecord &&
            item.Reason.Contains("GPU cleanup", StringComparison.Ordinal));
        var historical = await reader.ReadPipelineAttentionAsync("historical-jobs", root, 0, default);
        Assert.Contains(historical.Items, item => item.PipelineRecordId == retiredRecord);
        Assert.Contains(historical.Items, item => item.PipelineRecordId == historicalRecord);
        Assert.DoesNotContain(historical.Items, item => item.PipelineRecordId == earlierRetiredRecord);
        var gpuTasks = await reader.ReadPipelineAttentionAsync("current-gpu", root, 0, default);
        Assert.Equal(gpu.Id, Assert.Single(gpuTasks.Items).Id);

        SourceRevisionEntity Removed(string file)
        {
            var revision = Revision(Guid.NewGuid(), root, file, now, suppressed: true);
            revision.RetentionEvidenceJson = JsonSerializer.Serialize(new SourceGuardedRemovalWitness(
                2, "guarded-path-removal", root, revision.Id, file, 1, new string('a', 64),
                Guid.NewGuid(), 1, now, 0, 0, null, 0, null, null, null));
            return revision;
        }
    }
}
