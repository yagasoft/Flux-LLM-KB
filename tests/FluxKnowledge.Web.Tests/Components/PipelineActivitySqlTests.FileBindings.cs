using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_archive_processor_excludes_old_direct_work_unless_its_exact_activity_binds_that_record(bool bindsRecord)
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var archiveSource = Revision(Guid.NewGuid(), root, "owner.zip", now, false);
        archiveSource.Classification = "ArchiveZip";
        var recordId = await SeedActivityJobAsync(root, now, PublicJobState.WorkerProcessing, now.AddMinutes(5), 7, source: archiveSource);
        Guid branchId;
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var record = await setup.PipelineRecords.SingleAsync(p => p.Id == recordId);
            record.CurrentStage = (int)PipelineStage.Embed;
            var job = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == recordId);
            job.Stage = (int)PipelineStage.Embed; job.Operation = PipelineOperations.Embed;
            var dispatch = await setup.OutboxMessages.SingleAsync(d => d.JobId == job.Id);
            dispatch.Stage = job.Stage; dispatch.Operation = job.Operation;
            setup.JobAttempts.AddRange(
                new JobAttemptEntity { JobId = job.Id, AttemptNumber = 1, LeaseGeneration = 7, LeaseOwner = "activity-worker", StartedAtUtc = now.AddMinutes(-2) },
                new JobAttemptEntity { JobId = job.Id, AttemptNumber = 2, LeaseGeneration = 6, LeaseOwner = "previous-worker", StartedAtUtc = now.AddSeconds(-10) });
            var source = await setup.SourceRevisions.SingleAsync(s => s.Id == record.SourceRevisionId);
            var activity = Activity(source.Id, SourceActivityState.Pending, bindsRecord ? recordId : null);
            activity.ActivityKind = (int)SourceActivityKind.ArchiveExpansion;
            activity.ProcessorVersion = ZipArchiveRetainedProcessor.Capability.ProcessorVersion;
            activity.DescriptorFingerprint = ZipArchiveRetainedProcessor.Capability.ProcessorFingerprint;
            setup.SourceActivities.Add(activity);
            branchId = Guid.NewGuid();
            setup.SourceProcessorBranches.Add(new() { Id = branchId, SourceActivityId = activity.Id, SourceRevisionId = source.Id,
                InputSha256 = source.ContentSha256, ProcessorVersion = activity.ProcessorVersion,
                ProcessorFingerprint = activity.DescriptorFingerprint, State = (int)RetainedProcessorBranchState.Running,
                LeaseOwner = "archive-worker", LeaseGeneration = 1, LeaseExpiresAtUtc = now.AddMinutes(5), CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var reader = SnapshotReader();
        var sample = await reader.ReadOverviewAsync(default);
        var stage = Assert.Single(sample.Activity.FileStages, s => s.SourceRootId == root);
        Assert.Equal(bindsRecord ? "Embedding" : "Extracting", stage.Stage);
        Assert.Equal("Running", stage.State); Assert.Equal(1, stage.Count);
        var direct = Assert.Single(sample.Activity.RunningTasks, t => t.PipelineRecordId == recordId);
        Assert.Equal(!bindsRecord, direct.IsHistorical);
        Assert.InRange(direct.AttemptStartedAtUtc!.Value, now.AddMinutes(-2).AddMilliseconds(-1), now.AddMinutes(-2).AddMilliseconds(1));
        if (!bindsRecord)
        {
            Assert.Contains(sample.Activity.RunningTasks, t => t.SourceRootId == root && t.PipelineRecordId == null && t.Stage == "Extracting");
            await AssertAdmittedRebuildBindingAsync(recordId, root, now);
            await using var update = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            var branch = await update.SourceProcessorBranches.SingleAsync(b => b.Id == branchId);
            branch.State = (int)RetainedProcessorBranchState.Completed; branch.CompletedMemberCount = 1;
            var activity = await update.SourceActivities.SingleAsync(a => a.Id == branch.SourceActivityId);
            activity.State = (int)SourceActivityState.Completed;
            var member = Revision(Guid.NewGuid(), root, "inside.txt", now, false);
            member.OriginKind = 1; member.ParentSourceRevisionId = branch.SourceRevisionId;
            member.CanonicalPath = "archive://container.zip/inside.txt";
            update.SourceRevisions.Add(member);
            update.SourceProcessorBranchMembers.Add(new() { Id = Guid.NewGuid(), BranchId = branchId,
                ChildSourceRevisionId = member.Id, MemberFingerprint = new string('c', 64), Disposition = "completed", ByteLength = 1, CreatedAtUtc = now });
            await update.SaveChangesAsync();
            var completed = await reader.ReadOverviewAsync(default);
            var source = Assert.Single(completed.Activity.Sources, s => s.Id == root);
            Assert.Equal(2, source.KnownFiles); Assert.Equal(1, source.PendingFiles); Assert.Equal(1, source.CompletedWithoutTextFiles);
            var page = await reader.ReadPipelineFilesAsync(new("NoText"), root, 0, default);
            Assert.Equal("archive-expansion-completed-members-counted-separately", Assert.Single(page.Items).Reason);
        }
    }

    private async Task AssertAdmittedRebuildBindingAsync(Guid recordId, Guid root, DateTimeOffset now)
    {
        Guid? previousOperation;
        Guid jobId;
        var operation = Guid.NewGuid();
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var state = await setup.IndexState.SingleAsync(); previousOperation = state.CorpusRebuildOperationId;
            var record = await setup.PipelineRecords.SingleAsync(p => p.Id == recordId);
            record.CompletionCriteriaMet = true; record.CurrentStage = (int)PipelineStage.Publish;
            var job = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == recordId); jobId = job.Id;
            var dispatch = await setup.OutboxMessages.SingleAsync(d => d.JobId == jobId);
            setup.CorpusRebuildOperations.Add(new() { Id = operation, TargetEpoch = Guid.NewGuid(), ManifestHash = new string('a', 64), ManifestJson = "{}", CreatedAtUtc = now });
            setup.CorpusRebuildWorkItems.Add(new() { OperationId = operation, PipelineRecordId = recordId, SourceRevision = 1,
                CanonicalArtifactId = Guid.NewGuid(), EmbeddingJobId = jobId, DispatchMessageId = dispatch.Id, State = 1, PreparedAtUtc = now });
            state.CorpusRebuildOperationId = operation;
            await setup.SaveChangesAsync();
        }
        try
        {
            var active = await SnapshotReader().ReadOverviewAsync(default);
            Assert.False(Assert.Single(active.Activity.RunningTasks, t => t.Id == jobId).IsHistorical);
            Assert.Equal("Extracting", Assert.Single(active.Activity.FileStages, s => s.SourceRootId == root).Stage);
            await using (var update = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
            {
                var replacement = Guid.NewGuid();
                update.CorpusRebuildOperations.Add(new() { Id = replacement, TargetEpoch = Guid.NewGuid(), ManifestHash = new string('b', 64),
                    ManifestJson = "{}", SupersedesOperationId = operation, CreatedAtUtc = now });
                update.CorpusRebuildSupersededJobs.Add(new() { JobId = jobId, OperationId = operation,
                    ReplacementOperationId = replacement, SupersededAtUtc = now });
                await update.SaveChangesAsync();
            }
            var superseded = await SnapshotReader().ReadOverviewAsync(default);
            Assert.True(Assert.Single(superseded.Activity.RunningTasks, t => t.Id == jobId).IsHistorical);
        }
        finally
        {
            await using var restore = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            await restore.IndexState.ExecuteUpdateAsync(update => update.SetProperty(s => s.CorpusRebuildOperationId, previousOperation));
        }
    }

    [NativeSqlServerTheory]
    [InlineData("zip")]
    [InlineData("tar")]
    public async Task Completed_legacy_archives_exclude_unretained_skipped_and_deferred_entries_from_known_files(string kind)
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var owner = Revision(Guid.NewGuid(), root, $"legacy.{kind}", now, false);
            setup.SourceRevisions.Add(owner);
            var activity = Activity(owner.Id, SourceActivityState.Completed);
            activity.ActivityKind = (int)SourceActivityKind.ArchiveExpansion;
            activity.ProcessorVersion = kind == "zip" ? ZipArchiveRetainedProcessor.LegacyProcessorVersion : TarArchiveRetainedProcessor.LegacyProcessorVersion;
            activity.DescriptorFingerprint = kind == "zip" ? ZipArchiveRetainedProcessor.LegacyProcessorFingerprint : TarArchiveRetainedProcessor.LegacyProcessorFingerprint;
            setup.SourceActivities.Add(activity);
            var branch = new SourceProcessorBranchEntity { Id = Guid.NewGuid(), SourceActivityId = activity.Id, SourceRevisionId = owner.Id,
                InputSha256 = owner.ContentSha256, ProcessorVersion = activity.ProcessorVersion, ProcessorFingerprint = activity.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now };
            setup.SourceProcessorBranches.Add(branch);
            setup.SourceProcessorBranchMembers.AddRange(
                new SourceProcessorBranchMemberEntity { Id = Guid.NewGuid(), BranchId = branch.Id, MemberFingerprint = new string('c', 64),
                    Disposition = "skipped", ReasonCode = "archive-entry-policy-excluded", CreatedAtUtc = now },
                new SourceProcessorBranchMemberEntity { Id = Guid.NewGuid(), BranchId = branch.Id, MemberFingerprint = new string('d', 64),
                    Disposition = "deferred", ReasonCode = "archive-entry-capability-deferred", CreatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var reader = SnapshotReader();
        var sample = await reader.ReadOverviewAsync(default);
        var source = Assert.Single(sample.Activity.Sources, s => s.Id == root);
        Assert.Equal(1, source.KnownFiles); Assert.Equal(0, source.PendingFiles); Assert.Equal(1, source.CompletedWithoutTextFiles);
        var file = Assert.Single((await reader.ReadPipelineFilesAsync(new(), root, 0, default)).Items);
        Assert.Equal("NoText", file.Category);
        Assert.Equal("archive-expansion-completed-members-counted-separately", file.Reason);
    }

    [NativeSqlServerFact]
    public async Task Current_file_reasons_follow_winning_category_and_clear_predecessor_errors_after_publication()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var owner = Revision(Guid.NewGuid(), root, "published.docx", now, false);
            var input = Revision(Guid.NewGuid(), root, "generated.txt", now, false);
            input.OriginKind = 2; input.ParentSourceRevisionId = owner.Id;
            var mixed = Revision(Guid.NewGuid(), root, "mixed.txt", now, false);
            setup.SourceRevisions.AddRange(owner, input, mixed);
            var old = Activity(owner.Id, SourceActivityState.FailedTerminal); old.Reason = "obsolete-owner-error";
            var current = Activity(owner.Id, SourceActivityState.Completed);
            current.ProcessorVersion = OoxmlStructuralTextProcessor.Capability.ProcessorVersion;
            current.DescriptorFingerprint = OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint;
            var failed = Activity(mixed.Id, SourceActivityState.FailedTerminal); failed.Reason = "current-failure";
            var deferred = Activity(mixed.Id, SourceActivityState.DeferredUnsupported);
            deferred.ProcessorVersion = "other-route"; deferred.Reason = "unrelated-deferred-reason";
            setup.SourceActivities.AddRange(old, current, failed, deferred);
            var branch = new SourceProcessorBranchEntity { Id = Guid.NewGuid(), SourceActivityId = current.Id, SourceRevisionId = owner.Id,
                InputSha256 = owner.ContentSha256, ProcessorVersion = current.ProcessorVersion, ProcessorFingerprint = current.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Completed, CompletedMemberCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
            setup.SourceProcessorBranches.Add(branch);
            var identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "retained document", StableKey = "synthetic-published", CreatedAtUtc = now };
            setup.SourceIdentities.Add(identity);
            var record = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = identity.Id, SourceRevisionId = input.Id,
                Revision = 1, ContentHash = input.ContentSha256, CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true, RegisteredAtUtc = now };
            record.RootLineageRecordId = record.Id;
            setup.PipelineRecords.Add(record);
            var resolvedIdentity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "local file", StableKey = "synthetic-resolved-route", CreatedAtUtc = now };
            setup.SourceIdentities.Add(resolvedIdentity);
            var resolvedRecord = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = resolvedIdentity.Id, SourceRevisionId = mixed.Id,
                Revision = 1, ContentHash = mixed.ContentSha256, CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true, RegisteredAtUtc = now };
            resolvedRecord.RootLineageRecordId = resolvedRecord.Id;
            setup.PipelineRecords.Add(resolvedRecord);
            var resolvedFailure = Activity(mixed.Id, SourceActivityState.FailedTerminal, resolvedRecord.Id);
            resolvedFailure.ProcessorVersion = "resolved-route"; resolvedFailure.Reason = "resolved-failure-must-not-be-current";
            setup.SourceActivities.Add(resolvedFailure);
            setup.DocumentPublications.Add(new() { OwnerSourceRevisionId = owner.Id, DocumentInputSourceRevisionId = input.Id,
                SourceProcessorBranchId = branch.Id, PipelineRecordId = record.Id, PipelineRecordRevision = 1,
                ProcessorFingerprint = branch.ProcessorFingerprint, PublishedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var reader = SnapshotReader();
        var published = Assert.Single((await reader.ReadPipelineFilesAsync(new("Published"), root, 0, default)).Items);
        Assert.DoesNotContain("obsolete", published.Reason, StringComparison.Ordinal);
        var failedFile = Assert.Single((await reader.ReadPipelineFilesAsync(new("Failed"), root, 0, default)).Items);
        Assert.Equal("current-failure", failedFile.Reason);
        var sample = await reader.ReadOverviewAsync(default);
        var cause = Assert.Single(sample.Activity.FileCauses, c => c.SourceRootId == root);
        Assert.Equal("current-failure", cause.Reason); Assert.Equal(1, cause.Count);
    }

    [NativeSqlServerFact]
    public async Task Published_source_text_keeps_its_outcome_while_confirmed_companion_code_analysis_exposes_the_file()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var source = Revision(Guid.NewGuid(), root, "companion.cs", now, false);
        var recordId = await SeedActivityJobAsync(root, now, PublicJobState.Completed, null, source: source);
        Guid branchId;
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var record = await setup.PipelineRecords.SingleAsync(p => p.Id == recordId);
            record.CurrentStage = (int)PipelineStage.Publish; record.CompletionCriteriaMet = true;
            setup.SourceActivities.Add(Activity(source.Id, SourceActivityState.Completed, recordId));
            var code = Activity(source.Id, SourceActivityState.Pending);
            code.ActivityKind = (int)SourceActivityKind.CodeParsing;
            code.ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion;
            code.DescriptorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint;
            setup.SourceActivities.Add(code);
            branchId = Guid.NewGuid();
            setup.SourceProcessorBranches.Add(new() { Id = branchId, SourceActivityId = code.Id, SourceRevisionId = source.Id,
                InputSha256 = source.ContentSha256, ProcessorVersion = code.ProcessorVersion, ProcessorFingerprint = code.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Running, LeaseOwner = "synthetic-code-worker", LeaseGeneration = 1,
                LeaseExpiresAtUtc = now.AddMinutes(5), CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var warm = await WarmOwnershipReadAsync(snapshot: true);
        Assert.Contains(warm.RunningTasks, task => task.Id == branchId && task.SourceRootId == root && task.Stage == "Code analysis");
        var reader = SnapshotReader();
        var current = await reader.ReadOverviewAsync(default);
        var progress = Assert.Single(current.Activity.Sources, s => s.Id == root);
        Assert.Equal(1, progress.KnownFiles); Assert.Equal(1, progress.PublishedFiles); Assert.Equal(0, progress.PendingFiles);
        var task = Assert.Single(current.Activity.RunningTasks, t => t.SourceRootId == root);
        Assert.Equal(branchId, task.Id); Assert.Equal("Code analysis", task.Stage);
        Assert.Equal(source.CanonicalPath, task.FullPath); Assert.False(task.IsHistorical);
        await using (var update = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
            await update.SourceProcessorBranches.Where(b => b.Id == branchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.LeaseExpiresAtUtc, now.AddMinutes(-1)));
        var expired = await reader.ReadOverviewAsync(default);
        Assert.DoesNotContain(expired.Activity.RunningTasks, t => t.Id == branchId);
        Assert.Equal(1, Assert.Single(expired.Activity.Sources, s => s.Id == root).PublishedFiles);
    }
}
