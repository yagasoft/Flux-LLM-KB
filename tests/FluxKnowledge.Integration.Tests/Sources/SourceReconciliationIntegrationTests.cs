using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceReconciliationIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Due_enabled_root_creates_and_claims_one_durable_recurring_control()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            var root = Root(rootId, now);
            root.LastScanCompletedAtUtc = now.AddMinutes(-16);
            setup.SourceRootConfigurations.Add(root);
            await setup.SaveChangesAsync();
        }

        var claim = await CreateStore(now)
            .ClaimNextReleasedAsync("worker", now, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(claim);
        await using var verification = CreateContext();
        var request = await verification.SourceScanRequests.SingleAsync(value => value.SourceRootId == rootId);
        var job = await verification.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == request.Id);
        var outbox = await verification.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == request.Id);
        Assert.Equal(1, request.RequestKind);
        Assert.Equal("reconciliation", request.RequestedBy);
        Assert.Equal(now, request.RequestedAtUtc);
        Assert.True(request.IsReleased);
        Assert.Equal(now, request.ReleasedAtUtc);
        Assert.Equal((int)SourceScanRequestState.Running, request.State);
        Assert.Equal((int)SourceScanJobState.Running, job.State);
        Assert.Equal("worker", job.LeaseOwner);
        Assert.Equal(now.AddMinutes(1), job.LeaseExpiresAtUtc);
        Assert.Equal(1, job.LeaseGeneration);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(now, job.DueAtUtc);
        AssertRecurringOutbox(outbox, request.Id, now, now);
    }

    [NativeSqlServerFact]
    public async Task Source_control_preserves_held_work_claims_reclaims_and_rejects_a_stale_completion()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var heldRequestId = Guid.NewGuid();
        var releasedRequestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            setup.SourceScanRequests.AddRange(
                Request(heldRequestId, rootId, now, released: false, SourceScanRequestState.Held),
                Request(releasedRequestId, rootId, now, released: true, SourceScanRequestState.Released));
            setup.SourceScanJobs.AddRange(
                Job(Guid.NewGuid(), heldRequestId, now, DateTimeOffset.MaxValue),
                Job(jobId, releasedRequestId, now, now));
            setup.SourceScanOutbox.AddRange(
                Outbox(heldRequestId, DateTimeOffset.MaxValue, now),
                Outbox(releasedRequestId, now, now));
            await setup.SaveChangesAsync();
        }

        var store = CreateStore(now);
        var first = await store.ClaimNextReleasedAsync("first", now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(releasedRequestId, first.ScanRequest.Id.Value);
        var reclaimed = await store.ClaimNextReleasedAsync("second", now.AddMinutes(2), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(reclaimed);
        Assert.True(reclaimed.LeaseGeneration > first.LeaseGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(
            first, new SourceScanResult(first.SourceRoot.Id, first.ScanRequest.Id, 0, 0, 0, 0), null, CancellationToken.None).AsTask());
        await store.CompleteAsync(reclaimed, new SourceScanResult(reclaimed.SourceRoot.Id, reclaimed.ScanRequest.Id, 1, 0, 1, 0), null, CancellationToken.None);

        await using var verification = CreateContext();
        var heldRequest = await verification.SourceScanRequests.SingleAsync(value => value.Id == heldRequestId);
        var completedRequest = await verification.SourceScanRequests.SingleAsync(value => value.Id == releasedRequestId);
        var heldJob = await verification.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == heldRequestId);
        var completedJob = await verification.SourceScanJobs.SingleAsync(value => value.Id == jobId);
        var heldOutbox = await verification.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == heldRequestId);
        var releasedOutbox = await verification.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == releasedRequestId);
        Assert.Equal((int)SourceScanRequestState.Held, heldRequest.State);
        Assert.Equal(0, heldRequest.RequestKind);
        Assert.Equal("test", heldRequest.RequestedBy);
        Assert.Equal(now, heldRequest.RequestedAtUtc);
        Assert.False(heldRequest.IsReleased);
        Assert.Null(heldRequest.ReleasedAtUtc);
        Assert.Equal(DateTimeOffset.MaxValue, heldJob.DueAtUtc);
        Assert.Equal((int)SourceScanJobState.Pending, heldJob.State);
        Assert.Null(heldJob.LeaseOwner);
        Assert.Null(heldJob.LeaseExpiresAtUtc);
        Assert.Equal(0, heldJob.LeaseGeneration);
        Assert.Equal(0, heldJob.AttemptCount);
        Assert.Equal((int)SourceScanRequestState.Completed, completedRequest.State);
        Assert.Equal(0, completedRequest.RequestKind);
        Assert.Equal("test", completedRequest.RequestedBy);
        Assert.Equal(now, completedRequest.RequestedAtUtc);
        Assert.True(completedRequest.IsReleased);
        Assert.Equal(now, completedRequest.ReleasedAtUtc);
        Assert.Equal((int)SourceScanJobState.Completed, completedJob.State);
        Assert.Null(completedJob.LeaseOwner);
        Assert.Null(completedJob.LeaseExpiresAtUtc);
        Assert.Equal(2, completedJob.LeaseGeneration);
        Assert.Equal(2, completedJob.AttemptCount);
        Assert.Null(completedJob.Reason);
        Assert.Equal(now, completedJob.DueAtUtc);
        AssertRecurringOutbox(heldOutbox, heldRequestId, DateTimeOffset.MaxValue, now);
        AssertRecurringOutbox(releasedOutbox, releasedRequestId, now, now);
    }

    [NativeSqlServerFact]
    public async Task Failed_completion_keeps_exactly_one_retryable_released_control()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            setup.SourceScanRequests.Add(Request(requestId, rootId, now, true, SourceScanRequestState.Released));
            setup.SourceScanJobs.Add(new SourceScanJobEntity { Id = Guid.NewGuid(), SourceScanRequestId = requestId, State = (int)SourceScanJobState.Pending, DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceScanOutbox.Add(Outbox(requestId, now, now));
            await setup.SaveChangesAsync();
        }

        var store = CreateStore(now);
        var claim = await store.ClaimNextReleasedAsync("worker", now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(claim);
        await store.CompleteAsync(claim, new SourceScanResult(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 1), "io", CancellationToken.None);
        var retryAt = now.AddMinutes(1);
        var reclaimed = await store.ClaimNextReleasedAsync("retry-worker", retryAt, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(reclaimed);
        Assert.Equal(claim.ControlJobId, reclaimed.ControlJobId);
        Assert.Equal(claim.ScanRequest.Id, reclaimed.ScanRequest.Id);
        await using var verification = CreateContext();
        var request = await verification.SourceScanRequests.SingleAsync();
        var job = await verification.SourceScanJobs.SingleAsync();
        var outbox = await verification.SourceScanOutbox.SingleAsync();
        Assert.Equal((int)SourceScanRequestState.Running, request.State);
        Assert.Equal(0, request.RequestKind);
        Assert.Equal("test", request.RequestedBy);
        Assert.Equal(now, request.RequestedAtUtc);
        Assert.True(request.IsReleased);
        Assert.Equal(now, request.ReleasedAtUtc);
        Assert.Equal((int)SourceScanJobState.Running, job.State);
        Assert.Equal("retry-worker", job.LeaseOwner);
        Assert.Equal(retryAt.AddMinutes(1), job.LeaseExpiresAtUtc);
        Assert.Equal(2, job.LeaseGeneration);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(now.AddMinutes(1), job.DueAtUtc);
        Assert.Equal("io", job.Reason);
        AssertRecurringOutbox(outbox, requestId, now, now);
    }

    [NativeSqlServerFact]
    public async Task Held_save_only_control_blocks_due_reconciliation_without_creating_a_second_control()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var request = new SourceRootCreateRequest(
            $"C:\\source-reconciliation-tests\\held-{rootId:N}",
            "Held source",
            true,
            ["*.txt"],
            ["bin/**"],
            false,
            1024,
            ["text/plain"],
            TimeSpan.FromMinutes(15),
            "test");
        var receipt = await new SqlSourceRootStore(
                new ContextFactory(_fixture.ConnectionString),
                new FixedTimeProvider(now))
            .CreateAsync(request, ScanStartIntent.SaveOnly, CancellationToken.None);

        var claim = await CreateStore(now).ClaimNextReleasedAsync("worker", now, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Null(claim);
        await using var verification = CreateContext();
        var persistedRequest = await verification.SourceScanRequests.SingleAsync(value => value.Id == receipt.SourceScanRequestId.Value);
        var job = await verification.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == receipt.SourceScanRequestId.Value);
        var outbox = await verification.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == receipt.SourceScanRequestId.Value);
        Assert.Equal(receipt.SourceRootId.Value, persistedRequest.SourceRootId);
        Assert.Equal(0, persistedRequest.RequestKind);
        Assert.Equal("test", persistedRequest.RequestedBy);
        Assert.Equal(now, persistedRequest.RequestedAtUtc);
        Assert.Equal((int)SourceScanRequestState.Held, persistedRequest.State);
        Assert.False(persistedRequest.IsReleased);
        Assert.Null(persistedRequest.ReleasedAtUtc);
        Assert.Equal((int)SourceScanJobState.Pending, job.State);
        Assert.Equal(DateTimeOffset.MaxValue, job.DueAtUtc);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal(0, job.LeaseGeneration);
        Assert.Equal(0, job.AttemptCount);
        AssertRecurringOutbox(outbox, receipt.SourceScanRequestId.Value, DateTimeOffset.MaxValue, now);
    }

    [NativeSqlServerFact]
    public async Task Completed_recurring_control_creates_exactly_one_new_control_after_its_next_due_time()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            var root = Root(rootId, now);
            root.LastScanCompletedAtUtc = now.AddMinutes(-16);
            setup.SourceRootConfigurations.Add(root);
            await setup.SaveChangesAsync();
        }

        var store = CreateStore(now);
        var first = await store.ClaimNextReleasedAsync("first", now, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(first);
        await store.CompleteAsync(
            first,
            new SourceScanResult(first.SourceRoot.Id, first.ScanRequest.Id, 3, 2, 1, 0),
            null,
            CancellationToken.None);

        var nextDue = now.AddMinutes(15);
        var second = await store.ClaimNextReleasedAsync("second", nextDue, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(second);
        Assert.NotEqual(first.ScanRequest.Id, second.ScanRequest.Id);
        await using var verification = CreateContext();
        var requests = await verification.SourceScanRequests.Where(value => value.SourceRootId == rootId).OrderBy(value => value.RequestedAtUtc).ToListAsync();
        var jobs = await verification.SourceScanJobs.Where(value => value.SourceScanRequest.SourceRootId == rootId).ToListAsync();
        var outbox = await verification.SourceScanOutbox.Where(value => value.SourceScanRequest.SourceRootId == rootId).ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(2, outbox.Count);
        var completedRequest = Assert.Single(requests, value => value.Id == first.ScanRequest.Id.Value);
        var nextRequest = Assert.Single(requests, value => value.Id == second.ScanRequest.Id.Value);
        var completedJob = Assert.Single(jobs, value => value.SourceScanRequestId == completedRequest.Id);
        var nextJob = Assert.Single(jobs, value => value.SourceScanRequestId == nextRequest.Id);
        Assert.Equal((int)SourceScanRequestState.Completed, completedRequest.State);
        Assert.Equal(1, completedRequest.RequestKind);
        Assert.Equal("reconciliation", completedRequest.RequestedBy);
        Assert.Equal(now, completedRequest.RequestedAtUtc);
        Assert.Equal(now, completedRequest.ReleasedAtUtc);
        Assert.Equal((int)SourceScanJobState.Completed, completedJob.State);
        Assert.Null(completedJob.LeaseOwner);
        Assert.Null(completedJob.LeaseExpiresAtUtc);
        Assert.Equal(1, completedJob.LeaseGeneration);
        Assert.Equal(1, completedJob.AttemptCount);
        Assert.Equal((int)SourceScanRequestState.Running, nextRequest.State);
        Assert.Equal(1, nextRequest.RequestKind);
        Assert.Equal("reconciliation", nextRequest.RequestedBy);
        Assert.Equal(nextDue, nextRequest.RequestedAtUtc);
        Assert.True(nextRequest.IsReleased);
        Assert.Equal(nextDue, nextRequest.ReleasedAtUtc);
        Assert.Equal((int)SourceScanJobState.Running, nextJob.State);
        Assert.Equal("second", nextJob.LeaseOwner);
        Assert.Equal(nextDue.AddMinutes(1), nextJob.LeaseExpiresAtUtc);
        Assert.Equal(1, nextJob.LeaseGeneration);
        Assert.Equal(1, nextJob.AttemptCount);
        AssertRecurringOutbox(Assert.Single(outbox, value => value.SourceScanRequestId == completedRequest.Id), completedRequest.Id, now, now);
        AssertRecurringOutbox(Assert.Single(outbox, value => value.SourceScanRequestId == nextRequest.Id), nextRequest.Id, nextDue, nextDue);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_cadence_claims_converge_to_one_running_control_and_one_winner()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            var root = Root(rootId, now);
            root.LastScanCompletedAtUtc = now.AddMinutes(-16);
            setup.SourceRootConfigurations.Add(root);
            await setup.SaveChangesAsync();
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<ClaimedSourceScan?> ClaimAsync(string worker)
        {
            if (Interlocked.Increment(ref ready) == 2)
            {
                gate.TrySetResult();
            }

            await gate.Task.WaitAsync(CancellationToken.None);
            return await CreateStore(now).ClaimNextReleasedAsync(worker, now, TimeSpan.FromMinutes(1), CancellationToken.None);
        }

        var claims = await Task.WhenAll(ClaimAsync("first"), ClaimAsync("second"));

        Assert.Equal(1, claims.Count(value => value is not null));
        await using var verification = CreateContext();
        var request = await verification.SourceScanRequests.SingleAsync(value => value.SourceRootId == rootId);
        var job = await verification.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == request.Id);
        var outbox = await verification.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == request.Id);
        Assert.Equal((int)SourceScanRequestState.Running, request.State);
        Assert.Equal(1, request.RequestKind);
        Assert.Equal("reconciliation", request.RequestedBy);
        Assert.Equal(now, request.RequestedAtUtc);
        Assert.True(request.IsReleased);
        Assert.Equal(now, request.ReleasedAtUtc);
        Assert.Equal((int)SourceScanJobState.Running, job.State);
        Assert.Contains(job.LeaseOwner, new[] { "first", "second" });
        Assert.Equal(now.AddMinutes(1), job.LeaseExpiresAtUtc);
        Assert.Equal(1, job.LeaseGeneration);
        Assert.Equal(1, job.AttemptCount);
        AssertRecurringOutbox(outbox, request.Id, now, now);
    }

    [NativeSqlServerFact]
    public async Task Rescans_reuse_an_unchanged_revision_create_a_changed_revision_and_suppress_unseen_files()
    {
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(
            new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false,
            16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = root.CanonicalPath,
                DisplayName = root.DisplayName,
                State = (int)root.State,
                Recursive = root.Recursive,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = root.FollowLinks,
                MaximumFileBytes = root.MaximumFileBytes,
                AllowedClassificationsJson = "[]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var store = CreateStore();
        var unchanged = File("a.txt", new string('a', 64));
        var changedFile = File("a.txt", new string('b', 64));
        var unchangedReceipt = new SourceArtifactReceipt(SourceArtifactId.New(), unchanged.ContentSha256, "sha256\\aa\\unchanged.bin", unchanged.ByteLength, false);
        var changedReceipt = new SourceArtifactReceipt(SourceArtifactId.New(), changedFile.ContentSha256, "sha256\\bb\\changed.bin", changedFile.ByteLength, false);
        var first = await store.ConvergeRevisionAndArtifactAsync(root, unchanged, unchangedReceipt, CancellationToken.None);
        var repeated = await store.ConvergeRevisionAndArtifactAsync(root, unchanged, unchangedReceipt, CancellationToken.None);
        var changed = await store.ConvergeRevisionAndArtifactAsync(root, changedFile, changedReceipt, CancellationToken.None);
        var restored = await store.ConvergeRevisionAndArtifactAsync(root, unchanged, unchangedReceipt, CancellationToken.None);
        await store.SuppressUnseenAsync(root.Id, new HashSet<SourceRevisionId>(), CancellationToken.None);

        Assert.Equal(first, repeated);
        Assert.Equal(first, restored);
        Assert.NotEqual(first, changed);
        await using var verification = CreateContext();
        Assert.Equal(2, await verification.SourceRevisions.CountAsync());
        Assert.All(await verification.SourceRevisions.ToListAsync(), value => Assert.NotNull(value.SuppressedAtUtc));
    }

    [NativeSqlServerFact]
    public async Task Artifact_convergence_reuses_historic_revision_and_persists_exactly_one_matching_artifact()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(
            new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false,
            16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var store = CreateStore(now);
        var a = File("a.txt", new string('a', 64));
        var b = File("a.txt", new string('b', 64));
        var receiptA = new SourceArtifactReceipt(SourceArtifactId.New(), a.ContentSha256, "sha256\\aa\\a.bin", a.ByteLength, false);
        var receiptB = new SourceArtifactReceipt(SourceArtifactId.New(), b.ContentSha256, "sha256\\bb\\b.bin", b.ByteLength, false);

        var first = await store.ConvergeRevisionAndArtifactAsync(root, a, receiptA, CancellationToken.None);
        var changed = await store.ConvergeRevisionAndArtifactAsync(root, b, receiptB, CancellationToken.None);
        var restored = await store.ConvergeRevisionAndArtifactAsync(root, a, receiptA, CancellationToken.None);

        Assert.Equal(first, restored);
        Assert.NotEqual(first, changed);
        await using var verification = CreateContext();
        Assert.Equal(2, await verification.SourceRevisions.CountAsync());
        var artifacts = await verification.SourceArtifacts.ToListAsync();
        Assert.Equal(2, artifacts.Count);
        Assert.All(artifacts.GroupBy(artifact => artifact.SourceRevisionId), group => Assert.Single(group));
    }

    [NativeSqlServerFact]
    public async Task Concurrent_identical_artifact_convergences_create_one_revision_and_one_artifact()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var file = File("race.txt", new string('c', 64));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task<SourceRevisionId> ConvergeAsync()
        {
            if (Interlocked.Increment(ref ready) == 2)
            {
                gate.TrySetResult();
            }
            await gate.Task.WaitAsync(CancellationToken.None);
            return await CreateStore(now).ConvergeRevisionAndArtifactAsync(
                root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\cc\\race.bin", file.ByteLength, false), CancellationToken.None);
        }

        var revisions = await Task.WhenAll(ConvergeAsync(), ConvergeAsync());

        Assert.Equal(revisions[0], revisions[1]);
        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceRevisions.CountAsync());
        Assert.Equal(1, await verification.SourceArtifacts.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Competing_changed_convergences_create_a_single_lineage_with_two_revisions()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var first = File("changed.txt", new string('1', 64));
        var second = File("changed.txt", new string('2', 64));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task ConvergeAsync(SourceDiscoveredFile file)
        {
            if (Interlocked.Increment(ref ready) == 2)
            {
                gate.TrySetResult();
            }
            await gate.Task.WaitAsync(CancellationToken.None);
            await CreateStore(now).ConvergeRevisionAndArtifactAsync(
                root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, $"sha256\\{file.ContentSha256[..2]}\\{file.ContentSha256}.bin", file.ByteLength, false), CancellationToken.None);
        }

        await Task.WhenAll(ConvergeAsync(first), ConvergeAsync(second));

        await using var verification = CreateContext();
        var revisions = await verification.SourceRevisions.OrderBy(value => value.Revision).ToListAsync();
        Assert.Equal([1L, 2L], revisions.Select(value => value.Revision));
        Assert.Equal(revisions[0].Id, revisions[1].ParentSourceRevisionId);
        Assert.Equal(2, await verification.SourceArtifacts.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Conflicting_artifact_receipt_for_a_converged_revision_is_an_invariant_error()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var file = File("receipt.txt", new string('d', 64));
        var store = CreateStore(now);
        await store.ConvergeRevisionAndArtifactAsync(root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\dd\\one.bin", file.ByteLength, false), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConvergeRevisionAndArtifactAsync(
            root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\dd\\other.bin", file.ByteLength, false), CancellationToken.None).AsTask());
    }

    [NativeSqlServerFact]
    public async Task Blocked_retention_convergence_records_bounded_evidence_without_an_artifact()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var file = File("blocked.txt", new string('e', 64));
        var revisionId = await CreateStore(now).ConvergeBlockedRevisionAsync(root, file, new string('x', 256), CancellationToken.None);

        await using var verification = CreateContext();
        var revision = await verification.SourceRevisions.SingleAsync(value => value.Id == revisionId.SourceRevisionId.Value);
        Assert.Contains(new string('x', 128), revision.RetentionEvidenceJson, StringComparison.Ordinal);
        Assert.Equal(0, await verification.SourceArtifacts.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Blocked_retention_then_success_recovers_evidence_cancels_only_retention_activity_and_allows_one_text_activity()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var file = File("recover.txt", new string('7', 64));
        var store = CreateStore(now);
        var blocked = await store.ConvergeBlockedRevisionAsync(root, file, "artifact-io-failed", CancellationToken.None);
        Assert.True(blocked.IsRetentionBlocked);
        _ = await store.ConvergeBlockedRevisionAsync(root, file, "artifact-io-failed", CancellationToken.None);
        var retentionActivityId = Guid.NewGuid();
        var unrelatedActivityId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceActivities.AddRange(
                new SourceActivityEntity
                {
                    Id = retentionActivityId, SourceRevisionId = blocked.SourceRevisionId.Value,
                    ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = (int)ExecutionClass.DeferredCapability,
                    ProcessorVersion = "phase-3a-v1", InputFingerprint = file.ContentSha256, RequiredCapability = "source-artifact-store",
                    State = (int)SourceActivityState.DeferredPolicy, Reason = "artifact-io-failed", CreatedAtUtc = now, UpdatedAtUtc = now
                },
                new SourceActivityEntity
                {
                    Id = unrelatedActivityId, SourceRevisionId = blocked.SourceRevisionId.Value,
                    ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = (int)ExecutionClass.DeferredCapability,
                    ProcessorVersion = "other-policy-v1", InputFingerprint = file.ContentSha256, RequiredCapability = "other-policy",
                    State = (int)SourceActivityState.DeferredPolicy, Reason = "other", CreatedAtUtc = now, UpdatedAtUtc = now
                });
            await setup.SaveChangesAsync();
        }

        var revisionId = await store.ConvergeRevisionAndArtifactAsync(
            root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\77\\recover.bin", file.ByteLength, false), CancellationToken.None);
        var activityStore = new SqlSourceActivityStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        var text = new SourceActivityDraft(revisionId, SourceActivityKind.TextExtraction, ExecutionClass.InProcess, "phase-3a-v1", file.ContentSha256, null, null);
        _ = await activityStore.FindOrCreateAsync(text, CancellationToken.None);
        _ = await activityStore.FindOrCreateAsync(text, CancellationToken.None);
        _ = await store.ConvergeRevisionAndArtifactAsync(
            root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\77\\recover.bin", file.ByteLength, true), CancellationToken.None);

        await using var verification = CreateContext();
        var revision = await verification.SourceRevisions.SingleAsync(value => value.Id == revisionId.Value);
        var activities = await verification.SourceActivities.Where(value => value.SourceRevisionId == revisionId.Value).ToListAsync();
        Assert.Contains("recovered", revision.RetentionEvidenceJson, StringComparison.Ordinal);
        Assert.Contains("failed", revision.RetentionEvidenceJson, StringComparison.Ordinal);
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, activities.Single(value => value.Id == retentionActivityId).State);
        Assert.Equal((int)SourceActivityState.DeferredPolicy, activities.Single(value => value.Id == unrelatedActivityId).State);
        _ = Assert.Single(activities, value => value.ActivityKind == (int)SourceActivityKind.TextExtraction);
        Assert.Equal(1, await verification.SourceArtifacts.CountAsync());
        Assert.Single(await verification.AuditEvents.Where(value => value.SourceRevisionId == revisionId.Value && value.EventType == "source.retention_blocked").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Success_then_blocked_retention_keeps_verified_artifact_and_text_visible()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var file = File("preserve.txt", new string('8', 64));
        var store = CreateStore(now);
        var revisionId = await store.ConvergeRevisionAndArtifactAsync(
            root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\88\\preserve.bin", file.ByteLength, false), CancellationToken.None);
        var activityStore = new SqlSourceActivityStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        _ = await activityStore.FindOrCreateAsync(
            new SourceActivityDraft(revisionId, SourceActivityKind.TextExtraction, ExecutionClass.InProcess, "phase-3a-v1", file.ContentSha256, null, null), CancellationToken.None);

        var blocked = await store.ConvergeBlockedRevisionAsync(root, file, "artifact-io-failed", CancellationToken.None);

        Assert.Equal(revisionId, blocked.SourceRevisionId);
        Assert.False(blocked.IsRetentionBlocked);
        await using var verification = CreateContext();
        var revision = await verification.SourceRevisions.SingleAsync(value => value.Id == revisionId.Value);
        Assert.Null(revision.RetentionEvidenceJson);
        Assert.Equal(1, await verification.SourceArtifacts.CountAsync());
        Assert.Equal((int)SourceActivityState.Pending, (await verification.SourceActivities.SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Converged_revision_keeps_activity_planning_idempotent()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var file = File("activity.txt", new string('f', 64));
        var revisionId = await CreateStore(now).ConvergeRevisionAndArtifactAsync(
            root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\ff\\activity.bin", file.ByteLength, false), CancellationToken.None);
        var activityStore = new SqlSourceActivityStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        var draft = new SourceActivityDraft(revisionId, SourceActivityKind.TextExtraction, ExecutionClass.InProcess, "phase-3a-v1", file.ContentSha256, null, null);

        var first = await activityStore.FindOrCreateAsync(draft, CancellationToken.None);
        var repeated = await activityStore.FindOrCreateAsync(draft, CancellationToken.None);

        Assert.Equal(first.Id, repeated.Id);
        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceActivities.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Canonical_path_hash_collision_rolls_back_without_creating_a_partial_revision()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        var file = File("collision.txt", new string('9', 64));
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, StableSourceIdentity = "other-stable-identity", Revision = 1,
                ContentSha256 = file.ContentSha256, CanonicalPath = file.CanonicalPath, Classification = "AcceptedUtf8Text",
                Extension = ".txt", ByteLength = file.ByteLength, FileLastWriteAtUtc = file.LastWriteAtUtc, DiscoveredAtUtc = now,
                DiscoveryEvidenceJson = "{}"
            });
            await setup.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore(now).ConvergeRevisionAndArtifactAsync(
            root, file, new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256\\99\\collision.bin", file.ByteLength, false), CancellationToken.None).AsTask());

        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceRevisions.CountAsync());
        Assert.Equal(0, await verification.SourceArtifacts.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Same_identity_and_content_case_only_rename_creates_a_new_immutable_current_revision_and_suppresses_the_old_path()
    {
        var now = DateTimeOffset.Parse("2026-08-09T00:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var firstFile = new SourceDiscoveredFile("C:\\source-reconciliation-tests\\Guide.txt", "Guide.txt", "stable:rename", "text"u8.ToArray(), true, hash, 4, now, new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
        var secondFile = firstFile with { CanonicalPath = "C:\\source-reconciliation-tests\\guide.txt", RelativePath = "guide.txt" };
        var store = CreateStore(now);
        var first = await store.ConvergeRevisionAndArtifactAsync(root, firstFile, new SourceArtifactReceipt(SourceArtifactId.New(), hash, "sha256\\aa\\a.bin", 4, false), CancellationToken.None);
        var second = await store.ConvergeRevisionAndArtifactAsync(root, secondFile, new SourceArtifactReceipt(SourceArtifactId.New(), hash, "sha256\\aa\\a.bin", 4, true), CancellationToken.None);
        await store.SuppressUnseenAsync(root.Id, new HashSet<SourceRevisionId> { second }, CancellationToken.None);

        Assert.NotEqual(first, second);
        await using var verification = CreateContext();
        var revisions = await verification.SourceRevisions.OrderBy(value => value.Revision).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal("C:\\source-reconciliation-tests\\Guide.txt", revisions[0].CanonicalPath);
        Assert.NotNull(revisions[0].SuppressedAtUtc);
        Assert.Equal("C:\\source-reconciliation-tests\\guide.txt", revisions[1].CanonicalPath);
        Assert.Null(revisions[1].SuppressedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Rename_history_reuses_the_matching_immutable_A_revision_when_A_returns_after_B()
    {
        var now = DateTimeOffset.Parse("2026-08-09T00:00:00+00:00");
        var rootId = Guid.NewGuid();
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), "C:\\source-reconciliation-tests", "Test", true, false, 16 * 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }
        const string hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var a = new SourceDiscoveredFile("C:\\source-reconciliation-tests\\A.txt", "A.txt", "stable:history", "text"u8.ToArray(), true, hash, 4, now, new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
        var b = a with { CanonicalPath = "C:\\source-reconciliation-tests\\B.txt", RelativePath = "B.txt" };
        var store = CreateStore(now);
        var first = await store.ConvergeRevisionAndArtifactAsync(root, a, new SourceArtifactReceipt(SourceArtifactId.New(), hash, "sha256\\bb\\b.bin", 4, false), CancellationToken.None);
        var middle = await store.ConvergeRevisionAndArtifactAsync(root, b, new SourceArtifactReceipt(SourceArtifactId.New(), hash, "sha256\\bb\\b.bin", 4, true), CancellationToken.None);
        await store.SuppressUnseenAsync(root.Id, new HashSet<SourceRevisionId> { middle }, CancellationToken.None);
        var returned = await store.ConvergeRevisionAndArtifactAsync(root, a, new SourceArtifactReceipt(SourceArtifactId.New(), hash, "sha256\\bb\\b.bin", 4, true), CancellationToken.None);
        await store.SuppressUnseenAsync(root.Id, new HashSet<SourceRevisionId> { returned }, CancellationToken.None);

        Assert.Equal(first, returned);
        await using var verification = CreateContext();
        Assert.Equal(2, await verification.SourceRevisions.CountAsync());
        var current = await verification.SourceRevisions.SingleAsync(value => value.Id == first.Value);
        Assert.Null(current.SuppressedAtUtc);
        Assert.NotNull((await verification.SourceRevisions.SingleAsync(value => value.Id == middle.Value)).SuppressedAtUtc);
    }

    private FluxKnowledgeDbContext CreateContext() => new(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private SqlSourceScanStore CreateStore(DateTimeOffset? now = null) =>
        new(new ContextFactory(_fixture.ConnectionString), now is null ? TimeProvider.System : new FixedTimeProvider(now.Value));

    private static SourceRootConfigurationEntity Root(Guid id, DateTimeOffset now) => new()
    {
        Id = id, CanonicalPath = $"C:\\source-reconciliation-tests\\{id:N}", DisplayName = "Test",
        State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
        FollowLinks = false, MaximumFileBytes = 16 * 1024 * 1024, AllowedClassificationsJson = "[]", CrawlMode = 0,
        ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
    };

    private static SourceScanRequestEntity Request(Guid id, Guid rootId, DateTimeOffset now, bool released, SourceScanRequestState state) => new()
    {
        Id = id, SourceRootId = rootId, RequestKind = 0, RequestedBy = "test", RequestedAtUtc = now,
        IsReleased = released, ReleasedAtUtc = released ? now : null, State = (int)state
    };

    private static SourceScanJobEntity Job(Guid id, Guid requestId, DateTimeOffset now, DateTimeOffset dueAtUtc) => new()
    {
        Id = id,
        SourceScanRequestId = requestId,
        State = (int)SourceScanJobState.Pending,
        DueAtUtc = dueAtUtc,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static SourceScanOutboxEntity Outbox(Guid requestId, DateTimeOffset dueAtUtc, DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        SourceScanRequestId = requestId,
        Operation = "source.scan",
        IdempotencyKey = $"source-scan:{requestId:N}",
        DueAtUtc = dueAtUtc,
        CreatedAtUtc = createdAtUtc
    };

    private static void AssertRecurringOutbox(
        SourceScanOutboxEntity outbox,
        Guid requestId,
        DateTimeOffset dueAtUtc,
        DateTimeOffset createdAtUtc)
    {
        Assert.NotEqual(Guid.Empty, outbox.Id);
        Assert.Equal(requestId, outbox.SourceScanRequestId);
        Assert.Equal("source.scan", outbox.Operation);
        Assert.Equal($"source-scan:{requestId:N}", outbox.IdempotencyKey);
        Assert.Equal(dueAtUtc, outbox.DueAtUtc);
        Assert.Equal(createdAtUtc, outbox.CreatedAtUtc);
        Assert.Null(outbox.DispatchedAtUtc);
        Assert.Null(outbox.LeaseOwner);
        Assert.Null(outbox.LeaseExpiresAtUtc);
        Assert.Equal(0, outbox.LeaseGeneration);
        Assert.Equal(0, outbox.AttemptCount);
    }

    private static SourceDiscoveredFile File(string relativePath, string hash) => new(
        $"C:\\source-reconciliation-tests\\{relativePath}", relativePath, "test:" + relativePath, "text"u8.ToArray(), true, hash, 4,
        DateTimeOffset.Parse("2026-08-06T12:00:00+00:00"),
        new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure())
                .Options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
