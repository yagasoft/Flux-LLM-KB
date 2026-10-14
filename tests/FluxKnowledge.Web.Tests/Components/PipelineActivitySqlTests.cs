using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Web.Components.Status;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class PipelineActivitySqlTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Conflicting_file_bindings_remain_provisional_until_authoritative_suppression()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var previous = Revision(Guid.NewGuid(), root, "current.txt", now.AddDays(-1), false);
        var current = Revision(Guid.NewGuid(), root, "current.txt", now, false);
        previous.StableSourceIdentity = current.StableSourceIdentity;
        previous.ContentSha256 = new string('b', 64);
        current.Revision = 2;
        current.ParentSourceRevisionId = previous.Id;
        await using (var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            context.SourceRevisions.Add(previous);
            await context.SaveChangesAsync();
        }
        await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null, source: current);
        var child = Revision(Guid.NewGuid(), root, "retained-child.txt", now, false);
        child.OriginKind = 2;
        child.ParentSourceRevisionId = previous.Id;
        await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null, source: child);
        var before = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        var provisional = Assert.Single(before.Activity.Sources, s => s.Id == root);
        Assert.Equal(1, provisional.KnownFiles);
        Assert.Equal(1, provisional.ProvisionalFiles);
        Assert.Equal(0, provisional.PendingFiles);
        Assert.Equal(2, before.Activity.Jobs.Where(j => j.SourceRootId == root && !j.IsHistorical).Sum(j => j.Count));
        Assert.Equal(0, before.Activity.Jobs.Where(j => j.SourceRootId == root && j.IsHistorical).Sum(j => j.Count));
        await using (var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            (await context.SourceRevisions.SingleAsync(r => r.Id == previous.Id)).SuppressedAtUtc = now;
            await context.SaveChangesAsync();
        }
        var after = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        Assert.Equal(1, Assert.Single(after.Activity.Sources, s => s.Id == root).PendingFiles);
        Assert.Equal(0, Assert.Single(after.Activity.Sources, s => s.Id == root).ProvisionalFiles);
        Assert.Equal(1, after.Activity.Jobs.Where(j => j.SourceRootId == root && !j.IsHistorical).Sum(j => j.Count));
        Assert.Equal(1, after.Activity.Jobs.Where(j => j.SourceRootId == root && j.IsHistorical).Sum(j => j.Count));
    }

    [NativeSqlServerFact]
    public async Task Atomic_replacement_and_content_reversion_do_not_invent_a_current_revision_winner()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var earlier = Revision(Guid.NewGuid(), root, "atomic-save.txt", now.AddDays(-1), false);
        var replacement = Revision(Guid.NewGuid(), root, "atomic-save.txt", now, false);
        replacement.ContentSha256 = new string('b', 64);
        await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null, source: earlier);
        await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null, source: replacement);
        var ambiguous = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        var inventory = Assert.Single(ambiguous.Activity.Sources, s => s.Id == root);
        Assert.Equal(1, inventory.KnownFiles);
        Assert.Equal(1, inventory.ProvisionalFiles);
        Assert.Equal(0, inventory.PendingFiles);
        Assert.Equal(0, inventory.PublishedFiles);
        Assert.Null(inventory.LastProgressAtUtc);
        Assert.DoesNotContain(ambiguous.Activity.Jobs, j => j.SourceRootId == root && j.IsHistorical);
        // Authoritative reconciliation can retain the earlier content after a reversion.
        await using (var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            (await context.SourceRevisions.SingleAsync(s => s.Id == replacement.Id)).SuppressedAtUtc = now;
            await context.SaveChangesAsync();
        }
        var resolved = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(resolved.Activity.Sources, s => s.Id == root).ProvisionalFiles);
        Assert.Equal(1, Assert.Single(resolved.Activity.Sources, s => s.Id == root).PendingFiles);
        Assert.Equal(1, resolved.Activity.Jobs.Where(j => j.SourceRootId == root && j.IsHistorical).Sum(j => j.Count));
        Assert.Equal(1, resolved.Activity.Jobs.Where(j => j.SourceRootId == root && !j.IsHistorical).Sum(j => j.Count));
    }

    [NativeSqlServerFact]
    public async Task Progress_uses_only_successful_publish_receipts_for_current_record_revisions()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var completed = await SeedActivityJobAsync(root, now.AddHours(-1), PublicJobState.Completed, null);
        var pending = await SeedActivityJobAsync(root, now.AddHours(-1), PublicJobState.WorkerProcessing, now.AddMinutes(5), 1);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var record = await context.PipelineRecords.SingleAsync(r => r.Id == completed);
        record.CompletionCriteriaMet = true;
        record.CurrentStage = (int)PipelineStage.Publish;
        context.SourceActivities.Add(Activity(record.SourceRevisionId!.Value, SourceActivityState.Completed, record.Id));
        context.Artifacts.Add(new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = record.Id,
            SourceRevision = 1, Stage = (int)PipelineStage.Normalise, ContentHash = new string('b', 64), ContentType = "text/plain",
            SearchText = "Earlier stage", CreatedAtUtc = now });
        context.AuditEvents.Add(new AuditEventEntity { PipelineRecordId = record.Id, EventType = "pipeline stage completed",
            Actor = "test", DetailsJson = "{}", OccurredAtUtc = now });
        await context.SaveChangesAsync();
        var first = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        Assert.Null(Assert.Single(first.Activity.Sources, s => s.Id == root).LastProgressAtUtc);
        context.Artifacts.Add(new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = record.Id,
            SourceRevision = 1, Stage = (int)PipelineStage.Publish, ContentHash = new string('c', 64), ContentType = "text/plain",
            SearchText = "Current published revision", CreatedAtUtc = now.AddMinutes(-1) });
        await context.SaveChangesAsync();
        var second = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        var source = Assert.Single(second.Activity.Sources, s => s.Id == root);
        Assert.Equal(now.AddMinutes(-1), source.LastProgressAtUtc);
        Assert.Equal(1, source.PublishedFiles);
        Assert.Equal(1, source.PendingFiles);
        Assert.Equal("Catching up", PipelineActivityView.From(second.Activity, root).State);
    }

    [NativeSqlServerFact]
    public async Task Overview_activity_separates_live_lease_and_current_queue_from_young_suppressed_jobs()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var queued = await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null);
        var active = await SeedActivityJobAsync(root, now.AddDays(-14), PublicJobState.WorkerProcessing,
            now.AddMinutes(5), 1, 1);
        var historical = await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null, suppressed: true);

        var overview = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        var groups = overview.Activity.Jobs.Where(j => j.SourceRootId == root).ToArray();

        Assert.True(overview.Activity.IsAvailable);
        Assert.Equal(1, groups.Where(j => j.State == FluxKnowledge.Application.Contracts.PipelineWorkState.Running).Sum(j => j.Count));
        Assert.Equal(1, groups.Where(j => j.State == FluxKnowledge.Application.Contracts.PipelineWorkState.Queued).Sum(j => j.Count));
        Assert.Equal(1, groups.Where(j => j.IsHistorical).Sum(j => j.Count));
        var source = Assert.Single(overview.Activity.Sources, s => s.Id == root);
        Assert.Equal(2, source.KnownFiles);
        Assert.Equal(0, source.PublishedFiles);
        Assert.Equal(2, source.PendingFiles);
    }

    [NativeSqlServerFact]
    public async Task GPU_task_ownership_and_waiting_are_counted_independently_of_pipeline_job_labels()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var work = await SeedActivityJobAsync(root, now, PublicJobState.GpuProcessing, null);
        var waiting = await SeedActivityJobAsync(root, now, PublicJobState.GpuQueued, null);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var batchId = Guid.NewGuid();
        var slotKey = $"activity-{Guid.NewGuid():N}";
        await using (var setup = factory.CreateDbContext())
        {
            var job = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == work);
            var waitingJob = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == waiting);
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = slotKey, State = 1, OwnerKey = "PrivateOwner", UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
            setup.GpuBatches.Add(new GpuBatchEntity { Id = batchId, CapacitySlotKey = slotKey, OwnerKey = "PrivateOwner",
                State = 0, AdmissionGeneration = 7, ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic",
                ItemCount = 2, EstimatedBytes = 2, CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
            (await setup.GpuCapacitySlots.SingleAsync(s => s.SlotKey == slotKey)).ActiveBatchId = batchId;
            setup.GpuExecutorDispatches.Add(new GpuExecutorDispatchEntity { DispatchId = Guid.NewGuid(), BatchId = batchId,
                CapacitySlotKey = slotKey, OwnerKey = "PrivateOwner", ExecutorKey = "synthetic-executor", AdmissionGeneration = 7,
                State = 1, AcknowledgedAtUtc = now, UpdatedAtUtc = now });
            setup.GpuMiniTasks.AddRange(Task(job.Id, 1, batchId), Task(job.Id, 1, batchId), Task(job.Id, 3, null),
                Task(waitingJob.Id, 0, null, now.AddHours(1)));
            await setup.SaveChangesAsync();
        }
        var overview = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        var view = PipelineActivityView.From(overview.Activity, root);
        Assert.Equal(1, view.RunningJobs);
        Assert.Equal(2, view.ActiveGpuTasks);
        Assert.Equal(1, view.UncertainGpuTasks);
        Assert.Equal(1, view.WaitingJobs);
        Assert.Equal(0, view.ReadyGpuTasks);
        Assert.DoesNotContain(overview.Activity.Jobs, j => j.Reason.Contains("PrivateOwner", StringComparison.Ordinal));
        await using (var setup = factory.CreateDbContext())
        {
            (await setup.GpuExecutorDispatches.SingleAsync(d => d.BatchId == batchId)).OwnerKey = "PRIVATEOWNER";
            await setup.SaveChangesAsync();
        }
        var mismatched = PipelineActivityView.From((await ActivityReader().ReadOverviewAsync(CancellationToken.None)).Activity, root);
        Assert.Equal(0, mismatched.ActiveGpuTasks);
        Assert.Equal(0, mismatched.RunningJobs);
        Assert.Equal(2, mismatched.UnconfirmedGpuTasks);

        GpuMiniTaskEntity Task(Guid job, int state, Guid? batch, DateTimeOffset? deferred = null) => new()
        {
            Id = Guid.NewGuid(), ParentJobId = job, SourceRevision = 1, ModelRuntimeKey = "synthetic",
            SettingsFingerprint = "synthetic", IdempotencyKey = $"activity:{Guid.NewGuid():N}", EstimatedBytes = 1,
            AdmissionGeneration = 7, ExecutionState = state, BatchId = batch, DeferredUntilUtc = deferred, CreatedAtUtc = now
        };
    }

    [NativeSqlServerFact]
    public async Task Read_fence_rejects_an_already_allocated_transaction_and_a_write_between_queries()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using var reader = factory.CreateDbContext();
        var stable = await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None);
        Assert.True(stable.Accepts(await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None)));
        await using var writer = factory.CreateDbContext();
        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            (await writer.SourceRootConfigurations.SingleAsync(r => r.Id == root)).UpdatedAtUtc = now.AddSeconds(1);
            await writer.SaveChangesAsync();
            var allocated = await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None);
            Assert.False(allocated.IsStable);
            await transaction.CommitAsync();
            var committed = await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None);
            Assert.False(allocated.Accepts(committed));
            Assert.False(stable.Accepts(committed));
        }
        var before = await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None);
        (await writer.SourceRootConfigurations.SingleAsync(r => r.Id == root)).UpdatedAtUtc = now.AddSeconds(2);
        await writer.SaveChangesAsync();
        Assert.False(before.Accepts(await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None)));
    }

    [NativeSqlServerFact]
    public async Task A_pending_transaction_rejects_overview_before_reading_its_locked_rows()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        (await writer.SourceRootConfigurations.SingleAsync(r => r.Id == root)).DisplayName = "Uncommitted name";
        await writer.SaveChangesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var sample = await ActivityReader().ReadOverviewAsync(deadline.Token);
            Assert.False(sample.Activity.IsConsistent);
            Assert.False(sample.Activity.IsAvailable);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ActivityReader().ReadPipelineAttentionAsync("jobs", root, 0, deadline.Token));
        }
        finally { await transaction.RollbackAsync(); }
    }

    [NativeSqlServerFact]
    public async Task Active_heartbeat_writes_still_allow_fresh_overview_samples_for_a_thousand_file_backlog()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRevisions.AddRange(Enumerable.Range(0, 1000)
                .Select(i => Revision(Guid.NewGuid(), root, $"pending-{i}.txt", now, false)));
            await setup.SaveChangesAsync();
        }
        // Warm query compilation before measuring active write/read availability.
        Assert.True((await ActivityReader().ReadOverviewAsync(CancellationToken.None)).Activity.IsConsistent);
        using var stopping = new CancellationTokenSource();
        var writes = 0;
        var writer = HeartbeatAsync();
        var samples = new List<(bool Accepted, double Milliseconds)>();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var sample = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
                samples.Add((sample.Activity.IsConsistent, elapsed.Elapsed.TotalMilliseconds));
                if (sample.Activity.IsConsistent)
                    Assert.Equal(1000, Assert.Single(sample.Activity.Sources, s => s.Id == root).PendingFiles);
                await Task.Delay(100);
            }
        }
        finally { await stopping.CancelAsync(); await writer; }
        output.WriteLine($"Heartbeat writes: {writes}; accepted samples: {samples.Count(s => s.Accepted)}/12; " +
            $"read median: {samples.OrderBy(s => s.Milliseconds).ElementAt(6).Milliseconds:N1} ms; max: {samples.Max(s => s.Milliseconds):N1} ms.");
        Assert.True(writes >= 2, $"Only {writes} heartbeat writes occurred.");
        Assert.True(samples.Count(s => s.Accepted) >= 3,
            $"Accepted {samples.Count(s => s.Accepted)}/12; slowest read {samples.Max(s => s.Milliseconds):N0} ms.");

        async Task HeartbeatAsync()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    await using var context = factory.CreateDbContext();
                    await context.SourceRootConfigurations.Where(r => r.Id == root)
                        .ExecuteUpdateAsync(update => update.SetProperty(r => r.UpdatedAtUtc, DateTimeOffset.UtcNow), stopping.Token);
                    Interlocked.Increment(ref writes);
                    await Task.Delay(500, stopping.Token);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
    }

    [NativeSqlServerFact]
    public async Task Overview_activity_does_not_count_completed_extraction_registration_as_published_content()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var work = await SeedActivityJobAsync(root, now, PublicJobState.WorkerQueued, null);
        await using (var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            var record = await context.PipelineRecords.SingleAsync(r => r.Id == work);
            context.SourceActivities.Add(Activity(record.SourceRevisionId!.Value, SourceActivityState.Completed, record.Id));
            await context.SaveChangesAsync();
        }

        var overview = await ActivityReader().ReadOverviewAsync(CancellationToken.None);
        var source = Assert.Single(overview.Activity.Sources, s => s.Id == root);
        Assert.Equal(1, source.PendingFiles);
        Assert.Equal(0, source.PublishedFiles);
    }

    [NativeSqlServerFact]
    public async Task Historical_attention_paging_retains_exact_total_and_all_records_without_mutation()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        for (var i = 0; i < 30; i++)
        {
            var work = await SeedActivityJobAsync(root, now, PublicJobState.Failed, null, suppressed: true);
        }
        var reader = ActivityReader();
        var first = await reader.ReadPipelineAttentionAsync("jobs", root, 0, CancellationToken.None);
        var second = await reader.ReadPipelineAttentionAsync("jobs", root, 1, CancellationToken.None);
        Assert.Equal(30, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal(30, first.Items.Concat(second.Items).Select(i => i.Id).Distinct().Count());
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal("Historical", item.Applicability));
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        Assert.Equal(30, await (from job in context.Jobs join record in context.PipelineRecords on job.PipelineRecordId equals record.Id
            join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
            where revision.SourceRootId == root && job.PublicState == (int)PublicJobState.Failed select job.Id).CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Attention_fresh_attempt_discards_changed_names_total_and_rows_from_the_first_attempt()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        await SeedActivityJobAsync(root, now, PublicJobState.Failed, null, suppressed: true);
        var interceptor = new FenceMutationInterceptor(async read =>
        {
            if (read != 2) return;
            await SeedActivityJobAsync(root, now, PublicJobState.Failed, null, suppressed: true);
            await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            await writer.SourceRootConfigurations.Where(r => r.Id == root)
                .ExecuteUpdateAsync(update => update.SetProperty(r => r.DisplayName, "Updated source"));
        });
        var reader = ActivityReader(new TestDbContextFactory(_fixture.ConnectionString, interceptor));
        var page = await reader.ReadPipelineAttentionAsync("jobs", root, 0, CancellationToken.None);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal("Updated source", item.SourceName));
        Assert.Equal(4, interceptor.FenceReads);
    }

    [NativeSqlServerFact]
    public async Task Attention_rejects_both_changed_attempts_without_increasing_the_retry_bound()
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var interceptor = new FenceMutationInterceptor(async read =>
        {
            if (read % 2 != 0) return;
            await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            await writer.SourceRootConfigurations.Where(r => r.Id == root)
                .ExecuteUpdateAsync(update => update.SetProperty(r => r.DisplayName, $"Changed at fence {read}"));
        });
        var reader = ActivityReader(new TestDbContextFactory(_fixture.ConnectionString, interceptor));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadPipelineAttentionAsync("jobs", root, 0, CancellationToken.None));
        Assert.Equal(4, interceptor.FenceReads);
    }

    private SqlProjectionReader ActivityReader(IDbContextFactory<FluxKnowledgeDbContext>? contexts = null)
    {
        var factory = contexts ?? new TestDbContextFactory(_fixture.ConnectionString);
        return new(factory, new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(DerivedIndexRecoveryState.Healthy,
            null, null, null, null, 0)), new SqlGpuSchedulerStore(factory));
    }

    private async Task<Guid> AddActivityRootAsync(DateTimeOffset now)
    {
        var root = Guid.NewGuid();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = root, CanonicalPath = $"C:\\overview-source-indexing\\{root:N}", DisplayName = "Activity test",
            State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
            AllowedClassificationsJson = "[\"text/plain\"]", MaximumFileBytes = 10000,
            ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return root;
    }

    private async Task<Guid> SeedActivityJobAsync(Guid root, DateTimeOffset now, PublicJobState state,
        DateTimeOffset? leaseExpiresAtUtc, long leaseGeneration = 0, int attemptCount = 0,
        bool suppressed = false, SourceRevisionEntity? source = null)
    {
        var recordId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var revision = source ?? Revision(Guid.NewGuid(), root, $"{recordId:N}.txt", now, suppressed);
        context.SourceRevisions.Add(revision);
        context.SourceIdentities.Add(new SourceIdentityEntity { Id = identityId, SourceKind = "local file",
            StableKey = $"C:\\activity-ingress\\{recordId:N}.txt", CreatedAtUtc = now });
        context.PipelineRecords.Add(new PipelineRecordEntity { Id = recordId, SourceIdentityId = identityId, SourceRevisionId = revision.Id,
            Revision = 1, ContentHash = revision.ContentSha256, RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = now });
        context.Jobs.Add(new JobEntity { Id = jobId, PipelineRecordId = recordId, SourceRevision = 1,
            Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractUtf8, PublicState = (int)state,
            DueAtUtc = now, AttemptCount = attemptCount, LeaseOwner = state == PublicJobState.WorkerProcessing ? "activity-worker" : null,
            LeaseExpiresAtUtc = leaseExpiresAtUtc, LeaseGeneration = leaseGeneration });
        context.OutboxMessages.Add(new OutboxMessageEntity { Id = Guid.NewGuid(), JobId = jobId,
            PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8, DispatchGeneration = 0,
            IdempotencyKey = $"{recordId:N}:1:Extract:0", DueAtUtc = now, CreatedAtUtc = now });
        await context.SaveChangesAsync();
        return recordId;
    }

    private sealed class FixedRecoveryStatus(DerivedIndexRecoverySnapshot snapshot)
        : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = snapshot;
    }

    private static SourceRevisionEntity Revision(
        Guid id,
        Guid rootId,
        string path,
        DateTimeOffset now,
        bool suppressed) => new()
    {
        Id = id,
        SourceRootId = rootId,
        StableSourceIdentity = $"overview-source:{id:N}",
        Revision = 1,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"C:\\overview-source-indexing\\{rootId:N}\\{path}",
        Classification = "AcceptedUtf8Text",
        Extension = Path.GetExtension(path),
        ByteLength = 1,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = "{}",
        SuppressedAtUtc = suppressed ? now : null
    };

    private static SourceActivityEntity Activity(
        Guid sourceRevisionId,
        SourceActivityState state,
        Guid? resultingPipelineRecordId = null) => new()
    {
        Id = Guid.NewGuid(),
        SourceRevisionId = sourceRevisionId,
        ActivityKind = (int)SourceActivityKind.TextExtraction,
        ExecutionClass = (int)(state == SourceActivityState.DeferredUnsupported
            ? ExecutionClass.DeferredCapability
            : ExecutionClass.InProcess),
        ProcessorVersion = "phase-3a-v1",
        InputFingerprint = new string('a', 64),
        State = (int)state,
        ResultingPipelineRecordId = resultingPipelineRecordId,
        ResultingPipelineRecordRevision = resultingPipelineRecordId is null ? null : 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch
    };

    private sealed class TestDbContextFactory(string connectionString, params IInterceptor[] interceptors)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(interceptors)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class FenceMutationInterceptor(Func<int, Task> mutate) : DbCommandInterceptor
    {
        public int FenceReads { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MIN_ACTIVE_ROWVERSION", StringComparison.Ordinal)) await mutate(++FenceReads);
            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

