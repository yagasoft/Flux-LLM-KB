using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Scenario = FluxKnowledge.Integration.Tests.Sources.DeletedSourceRecoveryIntegrationTests.Scenario;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class DeletedFileJobRetentionTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private static readonly string RemovedPath = Path.Combine("removed", "one.txt");
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Maintenance_uses_committed_removal_time_at_the_exact_seven_day_boundary()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var sourceId = await ReadSourceIdAsync(scenario, RemovedPath);
        var jobId = await scenario.QueueAsync(sourceId);
        var liveJobId = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, "keeper.txt"));
        var removalTime = clock.GetUtcNow();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            await db.Jobs.ExecuteUpdateAsync(update => update.SetProperty(x => x.DueAtUtc, removalTime.AddYears(-1)));
        }

        File.Delete(Path.Combine(scenario.Files, RemovedPath));
        await scenario.PendingAsync(RemovedPath, false);
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        clock.Now = removalTime.AddDays(7).AddTicks(-1);
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        await using (var before = scenario.Factory.CreateDbContext())
        {
            Assert.True(await before.Jobs.AnyAsync(x => x.Id == jobId));
            Assert.Equal(removalTime, await before.SourceRevisions.Where(x => x.Id == sourceId).Select(x => x.SuppressedAtUtc).SingleAsync());
        }

        clock.Now = removalTime.AddDays(7);
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.False(await after.Jobs.AnyAsync(x => x.Id == jobId));
        Assert.True(await after.Jobs.AnyAsync(x => x.Id == liveJobId));
        Assert.True(await after.PipelineRecords.AnyAsync(x => x.SourceRevisionId == sourceId));
        Assert.True(await after.SourceArtifacts.AnyAsync(x => x.SourceRevisionId == sourceId));
        Assert.Equal(removalTime.AddDays(30), await after.SourceRevisions.Where(x => x.Id == sourceId).Select(x => x.RetainUntilUtc).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Existing_typed_deletions_expire_while_other_suppression_reasons_are_preserved()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        const string legacyName = "legacy.txt";
        await File.WriteAllTextAsync(Path.Combine(scenario.Files, legacyName), "legacy input");
        await scenario.ScanFilesystemAsync();
        var deletedAt = DateTimeOffset.UtcNow.AddDays(-8);
        var clock = new TestClock(deletedAt.AddDays(8));
        var sourceId = await ReadSourceIdAsync(scenario, RemovedPath);
        var scopedOutId = await ReadSourceIdAsync(scenario, Path.Combine("removed", "two.txt"));
        var editedId = await ReadSourceIdAsync(scenario, "keeper.txt");
        var legacyId = await ReadSourceIdAsync(scenario, legacyName);
        var expiredJob = await scenario.QueueAsync(sourceId);
        var excludedJob = await scenario.QueueAsync(scopedOutId);
        var editedJob = await scenario.QueueAsync(editedId);
        var legacyJob = await scenario.QueueAsync(legacyId);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var root = await db.SourceRootConfigurations.SingleAsync();
            foreach (var revision in await db.SourceRevisions.ToArrayAsync())
            {
                revision.SuppressedAtUtc = deletedAt;
                revision.RetentionEvidenceJson = revision.Id == editedId ? """{"reason":"superseded-by-verified-physical-capture"}""" :
                    revision.Id == legacyId ? """{"reason":"unseen-during-authoritative-scan"}""" :
                    System.Text.Json.JsonSerializer.Serialize(new SourceAuthoritativeRemovalWitness(1, root.Id, revision.Id,
                        Path.GetRelativePath(scenario.Files, revision.CanonicalPath), root.ConfigurationRevision,
                        1, 1, Guid.NewGuid(), Guid.NewGuid(), 1, revision.Id == sourceId ? "Deleted" : "OutOfScope",
                        deletedAt, null, 0, null, null));
                File.Delete(revision.CanonicalPath);
            }
            await db.SaveChangesAsync();
        }

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        await using var verify = scenario.Factory.CreateDbContext();
        Assert.False(await verify.Jobs.AnyAsync(x => x.Id == expiredJob));
        Assert.True(await verify.Jobs.AnyAsync(x => x.Id == excludedJob));
        Assert.True(await verify.Jobs.AnyAsync(x => x.Id == editedJob));
        Assert.True(await verify.Jobs.AnyAsync(x => x.Id == legacyJob));
    }

    [NativeSqlServerTheory]
    [InlineData("first input")]
    [InlineData("new bytes at the same path")]
    public async Task Restored_or_recreated_paths_keep_their_jobs_without_waiting_for_rediscovery(string contents)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var job = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        await RemoveAndRetireAsync(scenario, clock);
        await File.WriteAllTextAsync(Path.Combine(scenario.Files, RemovedPath), contents);
        clock.Now = clock.Now.AddDays(8);

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        await using var db = scenario.Factory.CreateDbContext();
        Assert.True(await db.Jobs.AnyAsync(x => x.Id == job));
    }

    [NativeSqlServerFact]
    public async Task A_path_that_returns_inside_the_sql_batch_rolls_back_jobs_and_deliveries()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var jobId = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        await RemoveAndRetireAsync(scenario, clock);
        var path = Path.Combine(scenario.Files, RemovedPath);
        var observer = new RecreatingObserver(path);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == jobId);
            db.OutboxMessages.Add(Delivery(job, clock.Now));
            await db.SaveChangesAsync();
        }
        clock.Now = clock.Now.AddDays(8);

        await CreateMaintenance(scenario, clock, observer).AdvanceAsync(default);

        Assert.True(File.Exists(path));
        await using var verify = scenario.Factory.CreateDbContext();
        Assert.True(await verify.Jobs.AnyAsync(x => x.Id == jobId));
        Assert.True(await verify.OutboxMessages.AnyAsync(x => x.JobId == jobId));
    }

    [NativeSqlServerFact]
    public async Task Unavailable_namespace_and_a_deleting_root_keep_job_history()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var job = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        await RemoveAndRetireAsync(scenario, clock);
        clock.Now = clock.Now.AddDays(8);
        Directory.Move(scenario.Files, scenario.Files + "-offline");
        Directory.CreateDirectory(scenario.Files);

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.True(await db.Jobs.AnyAsync(x => x.Id == job));
        Directory.Delete(scenario.Files);
        Directory.Move(scenario.Files + "-offline", scenario.Files);
        await db.SourceRootConfigurations.ExecuteUpdateAsync(set => set.SetProperty(x => x.State, (int)SourceRootState.Deleting));

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        Assert.True(await db.Jobs.AnyAsync(x => x.Id == job));
    }

    [NativeSqlServerTheory]
    [InlineData("worker")]
    [InlineData("delivery")]
    [InlineData("gpu")]
    public async Task Unfinished_worker_attempts_claimed_deliveries_and_unconfirmed_gpu_cleanup_keep_jobs(string blocker)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var protectedJob = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        var clean = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, Path.Combine("removed", "two.txt")));
        Directory.Delete(Path.Combine(scenario.Files, "removed"), recursive: true);
        await scenario.PendingAsync("removed", true);
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            switch (blocker)
            {
                case "worker":
                    db.JobAttempts.Add(new() { JobId = protectedJob, AttemptNumber = 1, LeaseOwner = "worker", StartedAtUtc = clock.Now });
                    break;
                case "delivery":
                    var delivery = Delivery(await db.Jobs.SingleAsync(x => x.Id == protectedJob), clock.Now);
                    delivery.LeaseOwner = "delivery";
                    delivery.LeaseExpiresAtUtc = clock.Now.AddMinutes(1);
                    db.OutboxMessages.Add(delivery);
                    break;
                case "gpu":
                    db.GpuMiniTasks.Add(MiniTask(protectedJob, GpuMiniTaskExecutionState.OutcomeUncertain, clock.Now));
                    break;
            }
            await db.SaveChangesAsync();
        }
        clock.Now = clock.Now.AddDays(8);

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        await using var verify = scenario.Factory.CreateDbContext();
        Assert.False(await verify.Jobs.AnyAsync(x => x.Id == clean));
        Assert.True(await verify.Jobs.AnyAsync(x => x.Id == protectedJob));
    }

    [NativeSqlServerFact]
    public async Task Bounded_batches_resume_and_concurrent_maintenance_cannot_delete_live_owners()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var sourceId = await ReadSourceIdAsync(scenario, RemovedPath);
        var firstJob = await scenario.QueueAsync(sourceId);
        var live = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, "keeper.txt"));
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            var first = await setup.Jobs.SingleAsync(x => x.Id == firstJob);
            setup.Jobs.AddRange(Enumerable.Range(1, 64).Select(_ => new JobEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = first.PipelineRecordId, SourceRevision = first.SourceRevision,
                Stage = first.Stage, Operation = first.Operation, PublicState = first.PublicState, DueAtUtc = first.DueAtUtc
            }));
            await setup.SaveChangesAsync();
        }
        await RemoveAndRetireAsync(scenario, clock);
        clock.Now = clock.Now.AddDays(8);

        var firstOwner = CreateMaintenance(scenario, clock);
        await firstOwner.AdvanceAsync(default);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(34, await db.Jobs.CountAsync());
        await firstOwner.AdvanceAsync(default);
        Assert.Equal(2, await db.Jobs.CountAsync());

        // Neither owner has the previous process's in-memory cursor.
        await Task.WhenAll(CreateMaintenance(scenario, clock).AdvanceAsync(default).AsTask(),
            CreateMaintenance(scenario, clock).AdvanceAsync(default).AsTask());
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        Assert.Equal(live, await db.Jobs.Select(x => x.Id).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Embedded_checkpoints_keep_their_cleanup_anchor_after_job_retention(bool hasIndependentOwner)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var jobId = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        await RemoveAndRetireAsync(scenario, clock);
        var draftId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var pendingDeliveryId = Guid.NewGuid();
        var dispatchedDeliveryId = Guid.NewGuid();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == jobId);
            db.IndexGenerations.Add(new() { Id = draftId, EmbeddingJobId = jobId, Dimensions = 3,
                ModelFingerprint = new string('a', 64), MetadataChecksum = new string('b', 64), CreatedAtUtc = clock.Now });
            db.IndexPublicationCandidates.Add(new() { Id = candidateId, DraftGenerationId = draftId, OwnerJobId = jobId,
                State = 2, CreatedAtUtc = clock.Now, CompletedAtUtc = clock.Now, OwnerStartedAtUtc = clock.Now,
                BeforeChecksum = new string('c', 64), AfterChecksum = new string('c', 64),
                OwnerMachineFingerprint = new string('d', 64) });
            var pending = Delivery(job, clock.Now);
            pending.Id = pendingDeliveryId;
            var dispatched = Delivery(job, clock.Now);
            dispatched.Id = dispatchedDeliveryId;
            dispatched.DispatchedAtUtc = clock.Now;
            db.OutboxMessages.AddRange(pending, dispatched);
            db.JobAttempts.Add(new() { JobId = jobId, AttemptNumber = 1, LeaseOwner = "worker",
                StartedAtUtc = clock.Now, CompletedAtUtc = clock.Now, Outcome = "completed" });
            if (hasIndependentOwner)
            {
                db.Artifacts.Add(new() { Id = Guid.NewGuid(), PipelineRecordId = job.PipelineRecordId,
                    SourceRevision = job.SourceRevision, Stage = (int)PipelineStage.Embed,
                    ContentHash = EmbedDraftDefaults.EmptyArtifactContentHash,
                    ContentType = EmbedDraftDefaults.ArtifactContentType, SearchText = draftId.ToString("D"), CreatedAtUtc = clock.Now });
            }
            await db.SaveChangesAsync();
        }
        clock.Now = clock.Now.AddDays(8);

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        await using var verify = scenario.Factory.CreateDbContext();
        Assert.Equal(!hasIndependentOwner, await verify.Jobs.AnyAsync(x => x.Id == jobId));
        var retainedDraft = await verify.IndexGenerations.SingleAsync(x => x.Id == draftId);
        var candidate = await verify.IndexPublicationCandidates.SingleAsync(x => x.Id == candidateId);
        Assert.Equal(hasIndependentOwner ? null : (Guid?)jobId, retainedDraft.EmbeddingJobId);
        Assert.Equal(hasIndependentOwner ? null : (Guid?)draftId, candidate.DraftGenerationId);
        Assert.Equal(hasIndependentOwner ? draftId : (Guid?)null, candidate.RetiredDraftGenerationId);
        Assert.Equal(hasIndependentOwner ? jobId : (Guid?)null, candidate.RetiredOwnerJobId);
        Assert.Equal(!hasIndependentOwner, await verify.JobAttempts.AnyAsync(x => x.JobId == jobId));
        Assert.Equal(!hasIndependentOwner, await verify.OutboxMessages.AnyAsync(x => x.Id == pendingDeliveryId));
        Assert.Equal(hasIndependentOwner ? null : (Guid?)jobId,
            await verify.OutboxMessages.Where(x => x.Id == dispatchedDeliveryId).Select(x => x.JobId).SingleAsync());
        Assert.Equal(hasIndependentOwner, await verify.Artifacts.AnyAsync(x => x.SearchText == draftId.ToString("D")));
    }

    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Settled_gpu_work_removes_empty_batches_and_preserves_shared_or_native_bound_work(bool shared, bool workerBound)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var removed = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        var live = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, "keeper.txt"));
        await RemoveAndRetireAsync(scenario, clock);
        var taskToPurge = MiniTask(removed, GpuMiniTaskExecutionState.Completed, clock.Now);
        var survivingTask = MiniTask(live, GpuMiniTaskExecutionState.Completed, clock.Now);
        var batch = new GpuBatchEntity { Id = Guid.NewGuid(), CapacitySlotKey = "test-slot", OwnerKey = "owner",
            State = (int)GpuBatchState.Completed, ItemCount = shared ? 2 : 1, ModelRuntimeKey = taskToPurge.ModelRuntimeKey,
            SettingsFingerprint = taskToPurge.SettingsFingerprint, AdmissionGeneration = 1, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now };
        var dispatch = new GpuExecutorDispatchEntity { DispatchId = Guid.NewGuid(), BatchId = batch.Id,
            CapacitySlotKey = batch.CapacitySlotKey, OwnerKey = batch.OwnerKey, ExecutorKey = "executor",
            AdmissionGeneration = 1, State = (int)GpuExecutorDispatchState.Terminal, UpdatedAtUtc = clock.Now };
        await using (var db = scenario.Factory.CreateDbContext())
        {
            db.GpuCapacitySlots.Add(new() { SlotKey = batch.CapacitySlotKey, UpdatedAtUtc = clock.Now });
            db.GpuBatches.Add(batch);
            db.GpuExecutorDispatches.Add(dispatch);
            if (workerBound)
                db.NativeWorkerInstances.Add(new() { InstanceId = Guid.NewGuid(), ActiveDispatchId = dispatch.DispatchId,
                    ExecutorKey = dispatch.ExecutorKey, ExecutableFingerprint = new string('a', 64),
                    ProtocolVersion = "test", LaunchedAtUtc = clock.Now });
            foreach (var task in shared ? new[] { taskToPurge, survivingTask } : [taskToPurge])
            {
                task.BatchId = batch.Id;
                task.AdmissionGeneration = batch.AdmissionGeneration;
                db.GpuMiniTasks.Add(task);
                db.GpuExecutorResultReceipts.Add(new() { OperationId = Guid.NewGuid(), BatchId = batch.Id,
                    DispatchId = dispatch.DispatchId, MiniTaskId = task.Id, ExecutorKey = dispatch.ExecutorKey,
                    AdmissionGeneration = dispatch.AdmissionGeneration, CreatedAtUtc = clock.Now,
                    EvidenceClass = (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed,
                    Disposition = (int)GpuMiniTaskBoundaryDisposition.Completed, RequestFingerprint = new string('a', 64) });
            }
            await db.SaveChangesAsync();
        }
        clock.Now = clock.Now.AddDays(8);

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);

        await using var verify = scenario.Factory.CreateDbContext();
        if (workerBound)
        {
            Assert.True(await verify.Jobs.AnyAsync(x => x.Id == removed));
            Assert.True(await verify.GpuMiniTasks.AnyAsync(x => x.Id == taskToPurge.Id));
            Assert.True(await verify.GpuExecutorResultReceipts.AnyAsync(x => x.MiniTaskId == taskToPurge.Id));
            Assert.Equal(dispatch.DispatchId, await verify.NativeWorkerInstances.Select(x => x.ActiveDispatchId).SingleAsync());
            return;
        }
        Assert.Equal(live, await verify.Jobs.Select(x => x.Id).SingleAsync());
        if (!shared)
        {
            Assert.False(await verify.GpuMiniTasks.AnyAsync());
            Assert.False(await verify.GpuExecutorResultReceipts.AnyAsync());
            Assert.False(await verify.GpuBatches.AnyAsync());
            Assert.False(await verify.GpuExecutorDispatches.AnyAsync());
            return;
        }
        Assert.Equal(survivingTask.Id, await verify.GpuMiniTasks.Select(x => x.Id).SingleAsync());
        Assert.Equal(survivingTask.Id, await verify.GpuExecutorResultReceipts.Select(x => x.MiniTaskId).SingleAsync());
        Assert.Equal(2, await verify.GpuBatches.Select(x => x.ItemCount).SingleAsync());
        Assert.Equal(dispatch.DispatchId, await verify.GpuExecutorDispatches.Select(x => x.DispatchId).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Hosted_source_maintenance_purges_jobs_with_an_exact_derived_owner()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var sourceId = await ReadSourceIdAsync(scenario, RemovedPath);
        var derivedId = Guid.NewGuid();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var physical = await db.SourceRevisions.SingleAsync(x => x.Id == sourceId);
            var archive = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = physical.SourceRootId,
                ParentSourceRevisionId = sourceId, OriginKind = 1, Revision = 1,
                CanonicalPath = physical.CanonicalPath + "!member", StableSourceIdentity = "member:" + Guid.NewGuid(),
                ContentSha256 = new string('a', 64), ByteLength = 10, DiscoveredAtUtc = clock.Now };
            db.SourceRevisions.Add(archive);
            db.SourceRevisions.Add(new() { Id = derivedId, ParentSourceRevisionId = archive.Id, SourceRootId = physical.SourceRootId,
                OriginKind = 2, Revision = 1, CanonicalPath = archive.CanonicalPath + "!document",
                StableSourceIdentity = "document:" + Guid.NewGuid(), ContentSha256 = new string('b', 64),
                ByteLength = 10, DiscoveredAtUtc = clock.Now });
            await db.SaveChangesAsync();
        }

        var jobId = await scenario.QueueAsync(derivedId);
        await RemoveAndRetireAsync(scenario, clock);
        clock.Now = clock.Now.AddDays(7);
        var maintenance = CreateMaintenance(scenario, clock);
        await using var services = new ServiceCollection().AddScoped<ISourceWorkRecovery>(_ => maintenance).BuildServiceProvider();
        using var host = new SourceWorkRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SourceWorkRecoveryService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(timeout.Token);
        try
        {
            await using var db = scenario.Factory.CreateDbContext();
            while (await db.Jobs.AnyAsync(x => x.Id == jobId, timeout.Token))
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);

            Assert.True(await db.SourceRevisions.AnyAsync(x => x.Id == derivedId, timeout.Token));
            Assert.True(await db.PipelineRecords.AnyAsync(x => x.SourceRevisionId == derivedId, timeout.Token));
        }
        finally { await host.StopAsync(default); }
    }

    [NativeSqlServerFact]
    public async Task Jobs_required_by_repository_recovery_outlive_the_seven_day_minimum()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var jobId = await scenario.QueueAsync(await ReadSourceIdAsync(scenario, RemovedPath));
        await using var db = scenario.Factory.CreateDbContext();
        await db.PipelineRecords.ExecuteUpdateAsync(set => set.SetProperty(x => x.RepositoryRecoveryBindingJson, "{}"));
        await RemoveAndRetireAsync(scenario, clock);
        clock.Now = clock.Now.AddDays(7);

        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        Assert.True(await db.Jobs.AnyAsync(x => x.Id == jobId));

        clock.Now = clock.Now.AddDays(23);
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
        Assert.False(await db.Jobs.AnyAsync(x => x.Id == jobId));
    }

    private static async Task RemoveAndRetireAsync(Scenario scenario, TestClock clock)
    {
        File.Delete(Path.Combine(scenario.Files, RemovedPath));
        await scenario.PendingAsync(RemovedPath, false);
        await CreateMaintenance(scenario, clock).AdvanceAsync(default);
    }

    private static OutboxMessageEntity Delivery(JobEntity job, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), JobId = job.Id, PipelineRecordId = job.PipelineRecordId,
        SourceRevision = job.SourceRevision, Stage = job.Stage, Operation = job.Operation,
        IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = now, DueAtUtc = now
    };

    private static GpuMiniTaskEntity MiniTask(Guid jobId, GpuMiniTaskExecutionState state, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ParentJobId = jobId, SourceRevision = 1, ExecutionState = (int)state,
        ModelRuntimeKey = "test", SettingsFingerprint = new string('a', 64), EstimatedBytes = 1,
        IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = now
    };

    private sealed class RecreatingObserver(string path) : ISourceObservationReader
    {
        private readonly WindowsSourceObservationReader _reader = new();
        public ISourceObservationLease Open(SourceRootConfiguration root, string relativePath) => _reader.Open(root, relativePath);
        public ISourceRootObservationLease OpenRoot(SourceRootConfiguration root) => _reader.OpenRoot(root);
        public IReadOnlyList<SourceNamespaceObservation> ReadRootAncestors(SourceRootConfiguration root) => _reader.ReadRootAncestors(root);
        public ISourceAbsenceLease? OpenAbsence(SourceRootConfiguration root, string relativePath) =>
            _reader.OpenAbsence(root, relativePath) is { } lease ? new RecreatingLease(lease, path) : null;

        private sealed class RecreatingLease(ISourceAbsenceLease lease, string path) : ISourceAbsenceLease
        {
            public void ValidateCurrent()
            {
                File.WriteAllText(path, "recreated during purge");
                lease.ValidateCurrent();
            }

            public void Dispose() => lease.Dispose();
        }
    }

    private static SqlSourceWorkRecovery CreateMaintenance(Scenario scenario, TimeProvider clock, ISourceObservationReader? reader = null)
    {
        var observations = reader ?? new WindowsSourceObservationReader();
        return new SqlSourceWorkRecovery(scenario.Factory, scenario.Admission,
            new SqlSourceRemovalReconciler(scenario.Factory, clock, observations), observations, clock,
            new SqlSourceWorkRetirement(scenario.Factory, clock));
    }

    private static async Task<Guid> ReadSourceIdAsync(Scenario scenario, string relativePath)
    {
        await using var db = scenario.Factory.CreateDbContext();
        var path = Path.Combine(scenario.Files, relativePath);
        return await db.SourceRevisions.Where(x => x.CanonicalPath == path).Select(x => x.Id).SingleAsync();
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
