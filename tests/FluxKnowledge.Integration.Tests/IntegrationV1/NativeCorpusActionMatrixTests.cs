using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

/// <summary>Command-bound authority effects for the closed native corpus action set.</summary>
public sealed class NativeCorpusActionMatrixTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T12:00:00+00:00");
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerTheory]
    [InlineData("root_update")]
    [InlineData("root_disable")]
    [InlineData("source_sync")]
    [InlineData("watcher_set")]
    [InlineData("job_retry")]
    public async Task Every_existing_target_action_replays_its_command_bound_receipt(string action)
    {
        var scenario = await CreateScenarioAsync(action);
        var service = CreateService();
        var mutation = Mutation(action, scenario.Payload);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var first = await service.CommitAsync(mutation, preview.ConfirmationId, $"replay-{action}", "test", CancellationToken.None);
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, $"replay-{action}", "test", CancellationToken.None);

        Assert.False(first.WasReplay); Assert.True(replay.WasReplay); Assert.Equal(first.OperationId, replay.OperationId);
    }

    [NativeSqlServerTheory]
    [InlineData("root_update")]
    [InlineData("root_disable")]
    [InlineData("source_sync")]
    [InlineData("watcher_set")]
    [InlineData("job_retry")]
    public async Task Every_existing_target_action_honours_cancelled_commit_before_any_authority_mutation(string action)
    {
        var scenario = await CreateScenarioAsync(action);
        var service = CreateService();
        var mutation = Mutation(action, scenario.Payload);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var authorityBefore = await AuthoritySnapshotAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(mutation, preview.ConfirmationId, $"cancel-{action}", "test", cancellation.Token).AsTask());
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await db.NativeOperationReceipts.ToListAsync());
        Assert.Equal(authorityBefore, await AuthoritySnapshotAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("root_update")]
    [InlineData("root_disable")]
    [InlineData("source_sync")]
    [InlineData("watcher_set")]
    [InlineData("job_retry")]
    public async Task Every_existing_target_action_rejects_a_stale_authoritative_target(string action)
    {
        var scenario = await CreateScenarioAsync(action);
        var service = CreateService();
        var mutation = Mutation(action, scenario.Payload);
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await MutateFenceTargetAsync(scenario);

        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(mutation, preview.ConfirmationId, $"stale-{action}", "test", CancellationToken.None).AsTask());
    }

    [NativeSqlServerFact]
    public async Task Root_create_uses_allowed_disposable_admission_evidence_and_replays_after_the_directory_is_removed()
    {
        await ClearAsync();
        var allowed = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeNativeCorpus_{Guid.NewGuid():N}");
        var source = Path.Combine(allowed, "source");
        Directory.CreateDirectory(source);
        try
        {
            var policy = new SourceRootPathPolicy(new LocalIngressOptions([allowed]));
            var service = CreateService(policy);
            var mutation = Mutation("root_create", new { path = source, displayName = "Disposable root" });
            var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
            var first = await service.CommitAsync(mutation, preview.ConfirmationId, "root-create-disposable", "test", CancellationToken.None);
            Directory.Delete(source, recursive: true);
            var replay = await service.CommitAsync(mutation, preview.ConfirmationId, "root-create-disposable", "test", CancellationToken.None);

            Assert.True(replay.WasReplay); Assert.Equal(first.OperationId, replay.OperationId);
            await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
            var root = await db.SourceRootConfigurations.SingleAsync(value => value.DisplayName == "Disposable root");
            Assert.Contains("identityFingerprint", root.HealthEvidenceJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pathFingerprint", root.PermissionEvidenceJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(allowed)) Directory.Delete(allowed, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Root_update_and_sync_commit_durable_distinct_queue_controls()
    {
        var root = await SeedRootAsync();
        var service = CreateService();

        await CommitAsync(service, "root_update", new { rootId = root, displayName = "Updated" }, "update");
        await CompleteActiveAsync(root);
        await CommitAsync(service, "source_sync", new { rootId = root }, "sync");
        await CompleteActiveAsync(root);
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal("Updated", (await db.SourceRootConfigurations.SingleAsync(value => value.Id == root)).DisplayName);
        var controls = await db.SourceScanRequests.Where(value => value.SourceRootId == root).OrderBy(value => value.RequestedAtUtc).ToListAsync();
        Assert.Contains(controls, value => value.RequestKind == 1);
        Assert.Contains(controls, value => value.RequestKind == 0);
    }

    [NativeSqlServerTheory]
    [InlineData("root_create", "secret-content-sentinel")]
    [InlineData("root_create", "password=synthetic-value")]
    [InlineData("root_create", "postgresql://synthetic-user:synthetic-password@127.0.0.1/db")]
    [InlineData("root_create", "-----BEGIN PRIVATE KEY----- synthetic -----END PRIVATE KEY-----")]
    [InlineData("root_create", "eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=")]
    [InlineData("root_update", "secret-content-sentinel")]
    [InlineData("root_update", "password=synthetic-value")]
    [InlineData("root_update", "postgresql://synthetic-user:synthetic-password@127.0.0.1/db")]
    [InlineData("root_update", "-----BEGIN PRIVATE KEY----- synthetic -----END PRIVATE KEY-----")]
    [InlineData("root_update", "eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=")]
    public async Task Protected_corpus_display_name_is_rejected_before_preview_or_root_mutation(
        string action,
        string protectedDisplayName)
    {
        var rootId = Guid.Empty;
        if (action == "root_update") rootId = await SeedRootAsync();
        else await ClearAsync();
        var mutation = action == "root_create"
            ? Mutation(action, new { path = @"C:\native-action-matrix\protected", displayName = protectedDisplayName })
            : Mutation(action, new { rootId, displayName = protectedDisplayName });

        await AssertReasonAsync("secret-content-withheld", () => CreateService()
            .PreviewAsync(mutation, "test", CancellationToken.None)
            .AsTask());

        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await db.NativeOperationIntents.ToListAsync());
        var names = await db.SourceRootConfigurations.Select(value => value.DisplayName).ToArrayAsync();
        Assert.DoesNotContain(protectedDisplayName, names, StringComparer.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Closed_backfill_action_is_rejected_before_creating_an_operation_or_queue_work()
    {
        var root = await SeedRootAsync();
        var service = CreateService();

        await AssertReasonAsync("action-not-allowed", () => service.PreviewAsync(
            Mutation("backfill", new { rootId = root, maximumCount = 7, processor = "retained-text" }),
            "test",
            CancellationToken.None).AsTask());

        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await db.NativeOperationIntents.ToListAsync());
        Assert.Empty(await db.SourceScanRequests.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Root_disable_and_watcher_set_fence_active_watcher_leases_and_prevent_future_claims()
    {
        var root = await SeedRootAsync();
        await using (var setup = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            setup.SourceRootWatchStates.Add(new SourceRootWatchStateEntity { SourceRootId = root, FirstSignalAtUtc = Now, LastSignalAtUtc = Now, SignalCount = 1, DebounceGeneration = 1, DueAtUtc = Now, LeaseOwner = "watch", LeaseGeneration = 1, LeaseExpiresAtUtc = Now.AddMinutes(1) });
            await setup.SaveChangesAsync();
        }
        var service = CreateService();
        await AssertReasonAsync("operation-fenced", () => PreviewAndCommitAsync(service, "watcher_set", new { rootId = root, enabled = false }, "watch-live"));

        await using (var expire = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var state = await expire.SourceRootWatchStates.SingleAsync(value => value.SourceRootId == root);
            state.LeaseExpiresAtUtc = Now.AddMinutes(-1);
            await expire.SaveChangesAsync();
        }
        await CommitAsync(service, "watcher_set", new { rootId = root, enabled = false }, "watch-disable");
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal((int)SourceRootState.Paused, (await db.SourceRootConfigurations.SingleAsync(value => value.Id == root)).State);
        Assert.False(await db.SourceRootWatchStates.AnyAsync(value => value.SourceRootId == root));
        var scanStore = new SqlSourceScanStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        Assert.Null(await scanStore.ClaimNextReleasedAsync("claim", DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Root_disable_removes_an_expired_watcher_generation_and_prevents_a_future_scan_claim()
    {
        var root = await SeedRootAsync();
        await using (var setup = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            setup.SourceRootWatchStates.Add(new SourceRootWatchStateEntity { SourceRootId = root, FirstSignalAtUtc = Now, LastSignalAtUtc = Now, SignalCount = 1, DebounceGeneration = 2, DueAtUtc = Now, LeaseOwner = "expired", LeaseGeneration = 4, LeaseExpiresAtUtc = Now.AddMinutes(-1) });
            await setup.SaveChangesAsync();
        }
        await CommitAsync(CreateService(), "root_disable", new { rootId = root }, "root-disable");
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal((int)SourceRootState.Paused, (await db.SourceRootConfigurations.SingleAsync(value => value.Id == root)).State);
        Assert.False(await db.SourceRootWatchStates.AnyAsync(value => value.SourceRootId == root));
    }

    [NativeSqlServerFact]
    public async Task Watcher_set_rejects_a_later_watcher_generation_and_closed_payload_validation_rejects_wrong_kinds()
    {
        var root = await SeedRootAsync();
        await using (var setup = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            setup.SourceRootWatchStates.Add(new SourceRootWatchStateEntity { SourceRootId = root, FirstSignalAtUtc = Now, LastSignalAtUtc = Now, SignalCount = 1, DebounceGeneration = 1, DueAtUtc = Now });
            await setup.SaveChangesAsync();
        }
        var service = CreateService();
        var mutation = Mutation("watcher_set", new { rootId = root, enabled = false });
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await using (var later = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var state = await later.SourceRootWatchStates.SingleAsync(value => value.SourceRootId == root);
            state.DebounceGeneration++;
            await later.SaveChangesAsync();
        }
        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(mutation, preview.ConfirmationId, "watch-stale", "test", CancellationToken.None).AsTask());
        await AssertReasonAsync("invalid-payload", () => service.PreviewAsync(Mutation("watcher_set", new { rootId = root, enabled = "false" }), "test", CancellationToken.None).AsTask());
    }

    [NativeSqlServerFact]
    public async Task Job_retry_resets_the_existing_fenced_control_only_for_expired_or_failed_jobs()
    {
        var root = await SeedRootAsync();
        var (jobId, requestId, outboxId) = await SeedFailedControlAsync(root);
        var service = CreateService();
        await CommitAsync(service, "job_retry", new { jobId }, "retry");
        await using (var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            Assert.Equal((int)SourceScanJobState.Pending, (await db.SourceScanJobs.SingleAsync(value => value.Id == jobId)).State);
            Assert.Equal((int)SourceScanRequestState.Released, (await db.SourceScanRequests.SingleAsync(value => value.Id == requestId)).State);
            Assert.Null((await db.SourceScanOutbox.SingleAsync(value => value.Id == outboxId)).DispatchedAtUtc);
            Assert.Equal(1, await db.SourceScanOutbox.CountAsync(value => value.SourceScanRequestId == requestId));
        }
        await using (var terminal = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            (await terminal.SourceScanJobs.SingleAsync(value => value.Id == jobId)).State = (int)SourceScanJobState.Completed;
            await terminal.SaveChangesAsync();
        }
        await AssertReasonAsync("operation-fenced", () => PreviewAndCommitAsync(service, "job_retry", new { jobId }, "retry-terminal"));
    }

    [NativeSqlServerFact]
    public async Task Job_retry_rejects_a_live_lease_but_recovers_an_expired_running_control_once()
    {
        var root = await SeedRootAsync();
        var (jobId, requestId, outboxId) = await SeedFailedControlAsync(root);
        await using (var setup = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var job = await setup.SourceScanJobs.SingleAsync(value => value.Id == jobId);
            job.State = (int)SourceScanJobState.Running; job.LeaseOwner = "worker"; job.LeaseExpiresAtUtc = Now.AddMinutes(1);
            job.LeaseGeneration = 7; job.AttemptCount = 5; job.Reason = "original failure"; job.ErrorDetails = "safe failure detail";
            var outbox = await setup.SourceScanOutbox.SingleAsync(value => value.Id == outboxId);
            outbox.LeaseGeneration = 4; outbox.AttemptCount = 3;
            await setup.SaveChangesAsync();
        }
        var service = CreateService();
        await AssertReasonAsync("operation-fenced", () => PreviewAndCommitAsync(service, "job_retry", new { jobId }, "retry-live"));
        await using (var expire = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var job = await expire.SourceScanJobs.SingleAsync(value => value.Id == jobId);
            var request = await expire.SourceScanRequests.SingleAsync(value => value.Id == job.SourceScanRequestId);
            job.LeaseExpiresAtUtc = Now.AddMinutes(-1);
            request.State = (int)SourceScanRequestState.Running;
            await expire.SaveChangesAsync();
        }
        var mutation = Mutation("job_retry", new { jobId }); var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var receipts = await Task.WhenAll(service.CommitAsync(mutation, preview.ConfirmationId, "retry-expired", "test", CancellationToken.None).AsTask(), CreateService().CommitAsync(mutation, preview.ConfirmationId, "retry-expired", "test", CancellationToken.None).AsTask());
        Assert.Single(receipts, receipt => !receipt.WasReplay); Assert.Single(receipts, receipt => receipt.WasReplay);
        await using var afterRetry = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var retriedJob = await afterRetry.SourceScanJobs.SingleAsync(value => value.Id == jobId);
        var retriedRequest = await afterRetry.SourceScanRequests.SingleAsync(value => value.Id == requestId);
        var retriedOutbox = await afterRetry.SourceScanOutbox.SingleAsync(value => value.Id == outboxId);
        Assert.Equal((int)SourceScanJobState.Pending, retriedJob.State);
        Assert.Equal((int)SourceScanRequestState.Released, retriedRequest.State);
        Assert.Equal(7, retriedJob.LeaseGeneration); Assert.Equal(5, retriedJob.AttemptCount);
        Assert.Equal("original failure", retriedJob.Reason); Assert.Equal("safe failure detail", retriedJob.ErrorDetails);
        Assert.Equal(4, retriedOutbox.LeaseGeneration); Assert.Equal(3, retriedOutbox.AttemptCount);
    }

    [NativeSqlServerFact]
    public async Task Root_create_fences_the_previewed_canonical_path_absence_when_another_commit_claims_it()
    {
        await ClearAsync();
        var service = CreateService();
        var mutation = Mutation("root_create", new { path = @"C:\native-action-matrix\same-root", displayName = "Same root" });
        var firstPreview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        var secondPreview = await CreateService().PreviewAsync(mutation, "test", CancellationToken.None);

        var outcomes = await Task.WhenAll(
            CaptureCommitAsync(() => service.CommitAsync(mutation, firstPreview.ConfirmationId, "root-create-first", "test", CancellationToken.None)),
            CaptureCommitAsync(() => CreateService().CommitAsync(mutation, secondPreview.ConfirmationId, "root-create-second", "test", CancellationToken.None)));

        Assert.Single(outcomes, outcome => outcome.Receipt is { WasReplay: false });
        Assert.Single(outcomes, outcome => outcome.Error?.ReasonCode == "operation-fenced");
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await db.SourceRootConfigurations.ToListAsync());
        Assert.Single(await db.SourceScanRequests.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Root_create_cancellation_leaves_no_authority_rows_or_receipt()
    {
        await ClearAsync();
        var mutation = Mutation("root_create", new { path = @"C:\native-action-matrix\cancel-root", displayName = "Cancelled root" });
        var preview = await CreateService().PreviewAsync(mutation, "test", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().CommitAsync(mutation, preview.ConfirmationId, "cancel-root", "test", cancellation.Token).AsTask());

        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await db.SourceRootConfigurations.ToListAsync());
        Assert.Empty(await db.SourceScanRequests.ToListAsync());
        Assert.Empty(await db.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Source_sync_fences_the_previewed_empty_active_control_set_when_a_control_appears()
    {
        var root = await SeedRootAsync();
        var service = CreateService();
        var mutation = Mutation("source_sync", new { rootId = root });
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await SeedControlAsync(root, requestKind: 0, requestState: SourceScanRequestState.Released, jobState: SourceScanJobState.Pending);

        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(mutation, preview.ConfirmationId, "empty-control-race", "test", CancellationToken.None).AsTask());

        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await db.SourceScanRequests.ToListAsync());
        Assert.Empty(await db.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Source_sync_rejects_a_preview_when_its_selected_control_completes()
    {
        var root = await SeedRootAsync();
        var (requestId, jobId, _) = await SeedControlAsync(root, requestKind: 0, requestState: SourceScanRequestState.Released, jobState: SourceScanJobState.Pending);
        var service = CreateService();
        var mutation = Mutation("source_sync", new { rootId = root });
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await using (var complete = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            (await complete.SourceScanRequests.SingleAsync(value => value.Id == requestId)).State = (int)SourceScanRequestState.Completed;
            (await complete.SourceScanJobs.SingleAsync(value => value.Id == jobId)).State = (int)SourceScanJobState.Completed;
            await complete.SaveChangesAsync();
        }

        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(mutation, preview.ConfirmationId, "completed-control", "test", CancellationToken.None).AsTask());
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Single(await db.SourceScanRequests.ToListAsync());
        Assert.Empty(await db.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Root_update_fences_running_and_follow_up_controls_then_creates_its_own_successor()
    {
        var root = await SeedRootAsync();
        var running = await SeedControlAsync(root, requestKind: 0, requestState: SourceScanRequestState.Running, jobState: SourceScanJobState.Running, jobLeaseOwner: "worker", jobLeaseExpiresAtUtc: Now.AddMinutes(1));
        var followUp = await SeedControlAsync(root, requestKind: 2, requestState: SourceScanRequestState.Released, jobState: SourceScanJobState.Pending);
        var service = CreateService();
        var mutation = Mutation("root_update", new { rootId = root, displayName = "Updated" });
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);

        Assert.Equal(2, preview.Targets.Count(target => target.TargetId.StartsWith("request:", StringComparison.Ordinal)));
        await service.CommitAsync(mutation, preview.ConfirmationId, "running-follow-up", "test", CancellationToken.None);

        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal((int)SourceScanRequestState.Running, (await db.SourceScanRequests.SingleAsync(value => value.Id == running.requestId)).State);
        Assert.Equal((int)SourceScanRequestState.Released, (await db.SourceScanRequests.SingleAsync(value => value.Id == followUp.requestId)).State);
        Assert.Single(await db.SourceScanRequests.Where(value => value.SourceRootId == root && value.RequestKind == 1).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Root_update_and_source_sync_do_not_coalesce_into_an_active_control_of_another_kind()
    {
        var root = await SeedRootAsync();
        await SeedControlAsync(root, requestKind: 0, requestState: SourceScanRequestState.Released, jobState: SourceScanJobState.Pending);
        await CommitAsync(CreateService(), "root_update", new { rootId = root, displayName = "Updated" }, "cross-kind-update");
        await using (var first = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            Assert.Equal(2, await first.SourceScanRequests.CountAsync(value => value.SourceRootId == root));
            Assert.Single(await first.SourceScanRequests.Where(value => value.SourceRootId == root && value.RequestKind == 1).ToListAsync());
        }

        var secondRoot = await SeedRootAsync();
        await SeedControlAsync(secondRoot, requestKind: 1, requestState: SourceScanRequestState.Released, jobState: SourceScanJobState.Pending);
        await CommitAsync(CreateService(), "source_sync", new { rootId = secondRoot }, "cross-kind-sync");
        await using var second = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal(2, await second.SourceScanRequests.CountAsync(value => value.SourceRootId == secondRoot));
        Assert.Single(await second.SourceScanRequests.Where(value => value.SourceRootId == secondRoot && value.RequestKind == 0).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Watcher_set_fences_both_the_previewed_presence_and_the_previewed_absence_of_watcher_state()
    {
        var root = await SeedRootAsync();
        var service = CreateService();
        var absentMutation = Mutation("watcher_set", new { rootId = root, enabled = false });
        var absentPreview = await service.PreviewAsync(absentMutation, "test", CancellationToken.None);
        await SeedWatcherAsync(root);

        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(absentMutation, absentPreview.ConfirmationId, "watch-appeared", "test", CancellationToken.None).AsTask());

        var presentMutation = Mutation("watcher_set", new { rootId = root, enabled = false });
        var presentPreview = await service.PreviewAsync(presentMutation, "test", CancellationToken.None);
        await using (var remove = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            remove.SourceRootWatchStates.Remove(await remove.SourceRootWatchStates.SingleAsync(value => value.SourceRootId == root));
            await remove.SaveChangesAsync();
        }

        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(presentMutation, presentPreview.ConfirmationId, "watch-disappeared", "test", CancellationToken.None).AsTask());
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal((int)SourceRootState.Enabled, (await db.SourceRootConfigurations.SingleAsync(value => value.Id == root)).State);
        Assert.Empty(await db.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Root_disable_rejects_a_preview_when_the_selected_watcher_state_disappears()
    {
        var root = await SeedRootAsync();
        await SeedWatcherAsync(root);
        var service = CreateService();
        var mutation = Mutation("root_disable", new { rootId = root });
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        await using (var remove = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            remove.SourceRootWatchStates.Remove(await remove.SourceRootWatchStates.SingleAsync(value => value.SourceRootId == root));
            await remove.SaveChangesAsync();
        }

        await AssertReasonAsync("operation-fenced", () => service.CommitAsync(mutation, preview.ConfirmationId, "disable-watch-disappeared", "test", CancellationToken.None).AsTask());
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Equal((int)SourceRootState.Enabled, (await db.SourceRootConfigurations.SingleAsync(value => value.Id == root)).State);
        Assert.Empty(await db.NativeOperationReceipts.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Job_retry_rejects_live_outbox_pending_and_mismatched_request_controls_without_revoking_ownership()
    {
        var root = await SeedRootAsync();
        var (jobId, requestId, outboxId) = await SeedFailedControlAsync(root);
        var service = CreateService();
        await using (var live = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var outbox = await live.SourceScanOutbox.SingleAsync(value => value.Id == outboxId);
            outbox.LeaseOwner = "dispatcher"; outbox.LeaseExpiresAtUtc = Now.AddMinutes(1);
            await live.SaveChangesAsync();
        }

        await AssertReasonAsync("operation-fenced", () => PreviewAndCommitAsync(service, "job_retry", new { jobId }, "retry-live-outbox"));
        await using (var verifyLive = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var outbox = await verifyLive.SourceScanOutbox.SingleAsync(value => value.Id == outboxId);
            Assert.Equal("dispatcher", outbox.LeaseOwner); Assert.Equal(Now.AddMinutes(1), outbox.LeaseExpiresAtUtc);
            var job = await verifyLive.SourceScanJobs.SingleAsync(value => value.Id == jobId);
            job.State = (int)SourceScanJobState.Pending;
            var request = await verifyLive.SourceScanRequests.SingleAsync(value => value.Id == requestId);
            request.State = (int)SourceScanRequestState.Released;
            outbox.LeaseOwner = null; outbox.LeaseExpiresAtUtc = null;
            await verifyLive.SaveChangesAsync();
        }

        await AssertReasonAsync("operation-fenced", () => PreviewAndCommitAsync(service, "job_retry", new { jobId }, "retry-pending"));
        await using (var mismatch = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            (await mismatch.SourceScanJobs.SingleAsync(value => value.Id == jobId)).State = (int)SourceScanJobState.Failed;
            (await mismatch.SourceScanRequests.SingleAsync(value => value.Id == requestId)).State = (int)SourceScanRequestState.Released;
            await mismatch.SaveChangesAsync();
        }

        await AssertReasonAsync("operation-fenced", () => PreviewAndCommitAsync(service, "job_retry", new { jobId }, "retry-mismatch"));
    }

    [NativeSqlServerFact]
    public async Task Native_root_controls_use_the_canonical_configuration_and_request_audit_evidence()
    {
        await ClearAsync();
        var service = CreateService();
        await CommitAsync(service, "root_create", new { path = @"C:\native-action-matrix\audited-root", displayName = "Audited root" }, "audited-create");
        await using (var created = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            var root = await created.SourceRootConfigurations.SingleAsync();
            var request = await created.SourceScanRequests.SingleAsync(value => value.SourceRootId == root.Id);
            var fingerprint = EvidenceValue(root.HealthEvidenceJson, "configurationFingerprint");
            Assert.Equal(fingerprint, EvidenceValue(request.AuditEvidenceJson, "configurationFingerprint"));
            Assert.Equal(ActorFingerprint("test"), EvidenceValue(request.AuditEvidenceJson, "requestedByFingerprint"));
            Assert.Equal(ActorFingerprint("test"), EvidenceValue(request.AuditEvidenceJson, "releasedByFingerprint"));
        }

        Guid rootId;
        string? oldFingerprint;
        await using (var beforeUpdate = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            rootId = await beforeUpdate.SourceRootConfigurations.Select(value => value.Id).SingleAsync();
            oldFingerprint = EvidenceValue((await beforeUpdate.SourceRootConfigurations.SingleAsync()).HealthEvidenceJson, "configurationFingerprint");
        }
        await CommitAsync(CreateService(), "root_update", new { rootId, displayName = "Updated audited root" }, "audited-update");
        await using var updated = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var rootAfter = await updated.SourceRootConfigurations.SingleAsync(value => value.Id == rootId);
        var newFingerprint = EvidenceValue(rootAfter.HealthEvidenceJson, "configurationFingerprint");
        var updateRequest = await updated.SourceScanRequests.SingleAsync(value => value.SourceRootId == rootId && value.RequestKind == 1);
        Assert.NotEqual(oldFingerprint, newFingerprint);
        Assert.Equal(newFingerprint, EvidenceValue(updateRequest.AuditEvidenceJson, "configurationFingerprint"));
        Assert.Equal(ActorFingerprint("test"), EvidenceValue(updateRequest.AuditEvidenceJson, "requestedByFingerprint"));
        Assert.Equal(ActorFingerprint("test"), EvidenceValue(updateRequest.AuditEvidenceJson, "releasedByFingerprint"));
    }

    private NativeCorpusCommandService CreateService(ISourceRootPathPolicy? pathPolicy = null) => new(
        new NativeOperationService(new SqlNativeOperationStore(SqlTestData.CreateFactory(_fixture), new FixedTimeProvider(Now)), []),
        new SqlNativeCorpusActionStore(
            SqlTestData.CreateFactory(_fixture),
            pathPolicy ?? new TestPathPolicy(),
            new LocalPrivateContentDisclosure()));

    private async Task<Guid> SeedRootAsync()
    {
        await ClearAsync();
        var id = Guid.NewGuid();
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        db.SourceRootConfigurations.Add(Root(id));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid jobId, Guid requestId, Guid outboxId)> SeedFailedControlAsync(Guid root)
    {
        var requestId = Guid.NewGuid(); var jobId = Guid.NewGuid(); var outboxId = Guid.NewGuid();
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        db.SourceScanRequests.Add(new SourceScanRequestEntity { Id = requestId, SourceRootId = root, RequestKind = 0, RequestedBy = "test", RequestedAtUtc = Now, IsReleased = true, ReleasedAtUtc = Now, State = (int)SourceScanRequestState.Failed });
        db.SourceScanJobs.Add(new SourceScanJobEntity { Id = jobId, SourceScanRequestId = requestId, State = (int)SourceScanJobState.Failed, DueAtUtc = Now, CreatedAtUtc = Now, UpdatedAtUtc = Now });
        db.SourceScanOutbox.Add(new SourceScanOutboxEntity { Id = outboxId, SourceScanRequestId = requestId, Operation = "source.scan", IdempotencyKey = $"source-scan:{requestId:N}", DueAtUtc = Now, CreatedAtUtc = Now, DispatchedAtUtc = Now });
        await db.SaveChangesAsync();
        return (jobId, requestId, outboxId);
    }

    private async Task<(Guid requestId, Guid jobId, Guid outboxId)> SeedControlAsync(
        Guid root,
        int requestKind,
        SourceScanRequestState requestState,
        SourceScanJobState jobState,
        string? jobLeaseOwner = null,
        DateTimeOffset? jobLeaseExpiresAtUtc = null,
        string? outboxLeaseOwner = null,
        DateTimeOffset? outboxLeaseExpiresAtUtc = null)
    {
        var requestId = Guid.NewGuid(); var jobId = Guid.NewGuid(); var outboxId = Guid.NewGuid();
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        db.SourceScanRequests.Add(new SourceScanRequestEntity
        {
            Id = requestId, SourceRootId = root, RequestKind = requestKind, RequestedBy = "test", RequestedAtUtc = Now,
            IsReleased = requestState != SourceScanRequestState.Held, ReleasedAtUtc = requestState == SourceScanRequestState.Held ? null : Now,
            State = (int)requestState, AuditEvidenceJson = "{}"
        });
        db.SourceScanJobs.Add(new SourceScanJobEntity
        {
            Id = jobId, SourceScanRequestId = requestId, State = (int)jobState, DueAtUtc = Now, CreatedAtUtc = Now, UpdatedAtUtc = Now,
            LeaseOwner = jobLeaseOwner, LeaseExpiresAtUtc = jobLeaseExpiresAtUtc
        });
        db.SourceScanOutbox.Add(new SourceScanOutboxEntity
        {
            Id = outboxId, SourceScanRequestId = requestId, Operation = "source.scan", IdempotencyKey = $"source-scan:{requestId:N}",
            DueAtUtc = Now, CreatedAtUtc = Now, LeaseOwner = outboxLeaseOwner, LeaseExpiresAtUtc = outboxLeaseExpiresAtUtc
        });
        await db.SaveChangesAsync();
        return (requestId, jobId, outboxId);
    }

    private async Task SeedWatcherAsync(Guid root)
    {
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        db.SourceRootWatchStates.Add(new SourceRootWatchStateEntity
        {
            SourceRootId = root, FirstSignalAtUtc = Now, LastSignalAtUtc = Now, SignalCount = 1,
            DebounceGeneration = 1, DueAtUtc = Now
        });
        await db.SaveChangesAsync();
    }

    private async Task<ActionScenario> CreateScenarioAsync(string action)
    {
        var root = await SeedRootAsync();
        return action switch
        {
            "root_update" => new ActionScenario(root, null, new { rootId = root, displayName = "Updated" }),
            "root_disable" => new ActionScenario(root, null, new { rootId = root }),
            "source_sync" => new ActionScenario(root, null, new { rootId = root }),
            "watcher_set" => new ActionScenario(root, null, new { rootId = root, enabled = true }),
            "job_retry" => new ActionScenario(root, (await SeedFailedControlAsync(root)).jobId, new { jobId = (await FindJobIdAsync(root)) }),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private async Task<Guid> FindJobIdAsync(Guid root)
    {
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        return await db.SourceScanJobs.Where(value => value.SourceScanRequest.SourceRootId == root).Select(value => value.Id).SingleAsync();
    }

    private async Task MutateFenceTargetAsync(ActionScenario scenario)
    {
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        if (scenario.JobId is Guid jobId)
        {
            var requestId = (await db.SourceScanJobs.SingleAsync(job => job.Id == jobId)).SourceScanRequestId;
            (await db.SourceScanOutbox.SingleAsync(value => value.SourceScanRequestId == requestId)).DueAtUtc = Now.AddMinutes(1);
        }
        else
        {
            (await db.SourceRootConfigurations.SingleAsync(value => value.Id == scenario.RootId)).DisplayName = "stale";
        }
        await db.SaveChangesAsync();
    }

    private async Task CompleteActiveAsync(Guid root)
    {
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var request = await db.SourceScanRequests.Where(value => value.SourceRootId == root && value.State != (int)SourceScanRequestState.Completed).OrderByDescending(value => value.RequestedAtUtc).FirstAsync();
        request.State = (int)SourceScanRequestState.Completed;
        (await db.SourceScanJobs.SingleAsync(value => value.SourceScanRequestId == request.Id)).State = (int)SourceScanJobState.Completed;
        await db.SaveChangesAsync();
    }

    private async Task<(string confirmation, NativeActionReceipt receipt)> CommitAsync(NativeCorpusCommandService service, string action, object payload, string key)
    {
        var mutation = Mutation(action, payload); var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        return (preview.ConfirmationId, await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", CancellationToken.None));
    }

    private async Task PreviewAndCommitAsync(NativeCorpusCommandService service, string action, object payload, string key)
    {
        var mutation = Mutation(action, payload); var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        _ = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", CancellationToken.None);
    }

    private static NativeCorpusMutation Mutation(string action, object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new NativeCorpusMutation(action, document.RootElement.Clone());
    }

    private async Task ClearAsync()
    {
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        await db.NativeOperationReceipts.ExecuteDeleteAsync(); await db.NativeOperationIntents.ExecuteDeleteAsync();
        await db.SourceRootWatchStates.ExecuteDeleteAsync(); await db.SourceScanOutbox.ExecuteDeleteAsync(); await db.SourceScanJobs.ExecuteDeleteAsync(); await db.SourceScanRequests.ExecuteDeleteAsync(); await db.SourceRootConfigurations.ExecuteDeleteAsync();
    }

    private async Task<string> AuthoritySnapshotAsync()
    {
        await using var db = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var snapshot = new
        {
            roots = await db.SourceRootConfigurations.AsNoTracking().OrderBy(value => value.Id).Select(value => new
            {
                value.Id, value.CanonicalPath, value.DisplayName, value.State, value.ConfigurationRevision,
                value.HealthEvidenceJson, value.PermissionEvidenceJson, value.RowVersion
            }).ToArrayAsync(),
            watches = await db.SourceRootWatchStates.AsNoTracking().OrderBy(value => value.SourceRootId).Select(value => new
            {
                value.SourceRootId, value.DebounceGeneration, value.DueAtUtc, value.LeaseOwner,
                value.LeaseExpiresAtUtc, value.LeaseGeneration, value.RowVersion
            }).ToArrayAsync(),
            requests = await db.SourceScanRequests.AsNoTracking().OrderBy(value => value.Id).Select(value => new
            {
                value.Id, value.SourceRootId, value.RequestKind, value.IsReleased, value.ReleasedAtUtc,
                value.State, value.AuditEvidenceJson, value.RowVersion
            }).ToArrayAsync(),
            jobs = await db.SourceScanJobs.AsNoTracking().OrderBy(value => value.Id).Select(value => new
            {
                value.Id, value.SourceScanRequestId, value.State, value.DueAtUtc, value.LeaseOwner,
                value.LeaseExpiresAtUtc, value.LeaseGeneration, value.AttemptCount, value.Reason,
                value.ErrorDetails, value.RowVersion
            }).ToArrayAsync(),
            outbox = await db.SourceScanOutbox.AsNoTracking().OrderBy(value => value.Id).Select(value => new
            {
                value.Id, value.SourceScanRequestId, value.DueAtUtc, value.DispatchedAtUtc,
                value.LeaseOwner, value.LeaseExpiresAtUtc, value.LeaseGeneration, value.AttemptCount,
                value.RowVersion
            }).ToArrayAsync()
        };
        return JsonSerializer.Serialize(snapshot);
    }

    private static SourceRootConfigurationEntity Root(Guid id) => new() { Id = id, CanonicalPath = $"C:\\native-action-matrix\\{id:N}", DisplayName = "Root", State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 1024, AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now, HealthEvidenceJson = "{\"physicalIdentity\":{\"identityFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}" };

    private static string? EvidenceValue(string? evidenceJson, string property)
    {
        using var document = JsonDocument.Parse(evidenceJson ?? "{}");
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string ActorFingerprint(string actor) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(actor)));

    private static async Task<CommitOutcome> CaptureCommitAsync(Func<ValueTask<NativeActionReceipt>> commit)
    {
        try { return new CommitOutcome(await commit(), null); }
        catch (NativeOperationException exception) { return new CommitOutcome(null, exception); }
    }

    private static async Task AssertReasonAsync(string reason, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<NativeOperationException>(action);
        Assert.Equal(reason, exception.ReasonCode);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private sealed class TestPathPolicy : ISourceRootPathPolicy { public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => new(request.FullPath, new SourceRootPhysicalIdentity(request.FullPath, "C:\\", true, new string('a', 64)), new SourceRootPermissionEvidence(true, new string('b', 64), "{}")); }
    private sealed record ActionScenario(Guid RootId, Guid? JobId, object Payload);
    private sealed record CommitOutcome(NativeActionReceipt? Receipt, NativeOperationException? Error);
}
