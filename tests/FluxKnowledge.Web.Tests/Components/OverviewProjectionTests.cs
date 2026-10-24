using System.Data.Common;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OverviewProjectionTests
{
    [Fact]
    public async Task Overview_explains_the_global_capacity_owner_and_action_with_the_real_multi_day_age()
    {
        var owner = new GpuOcrOwnerStatus(Guid.NewGuid(), null, "paddleocr-vl-local", "Blocked", 5400, null,
            "document-ocr-legacy-owner-unproven", "Operator verification of native cleanup is required before GPU work can resume.");
        using var factory = new OverviewApplicationFactory(CreateOverview(0, 1100) with
        { GpuSchedulerStatus = GpuSchedulerStatusProjection.Empty with { OcrOwner = owner } });
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("OCR is holding GPU capacity", html, StringComparison.Ordinal);
        Assert.Contains("5,400 minutes", html, StringComparison.Ordinal);
        Assert.Contains(owner.NextAction, html, StringComparison.Ordinal);
        Assert.Contains(owner.ReasonCode, html, StringComparison.Ordinal);
        Assert.Contains(owner.BatchId.ToString(), html, StringComparison.Ordinal);
    }
    [Fact]
    public async Task An_initial_status_failure_does_not_render_zero_scheduler_diagnostics()
    {
        using var factory = new OverviewApplicationFactory(CreateOverview(0, 0), failReads: true);
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("zero activity has not been confirmed", html, StringComparison.Ordinal);
        Assert.Contains("Scheduler diagnostics are unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Scheduler active</dt>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_prioritises_current_source_progress_and_keeps_historical_attention_with_exact_units()
    {
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        var root = Guid.NewGuid();
        var activity = new PipelineActivitySummary(now, now,
            [new(root, "Example workspace", "C:\\workspace", "Enabled", 1100, 1096, 4, 0, 0, 0, 0, 0,
                now.AddMinutes(-2), now.AddHours(-1), now.AddMinutes(-1), false, null)],
            [new(root, "Example workspace", "Publish", PipelineWorkState.Running, false, "Owned", 1),
                new(root, "Example workspace", "Embed", PipelineWorkState.Historical, true, "Suppressed", 31)],
            [new(null, "Interactive search", "Outcome uncertain", 179)], 0, 0, 0);
        using var factory = new OverviewApplicationFactory(CreateOverview(0, 1100) with { Activity = activity });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Catching up", html, StringComparison.Ordinal);
        Assert.Contains("Running pipeline jobs", html, StringComparison.Ordinal);
        Assert.Contains("Active GPU tasks", html, StringComparison.Ordinal);
        Assert.Contains("Example workspace", html, StringComparison.Ordinal);
        Assert.Contains("1,100", html, StringComparison.Ordinal);
        Assert.Contains("31 historical pipeline jobs", html, StringComparison.Ordinal);
        Assert.Contains("GPU tasks with uncertain outcomes", html, StringComparison.Ordinal);
        Assert.Contains("Search-index health", html, StringComparison.Ordinal);
        Assert.Contains("Status as of", html, StringComparison.Ordinal);
        Assert.DoesNotContain("GPU processing</dt>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("999+", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_diagnostic_summary_keeps_opaque_generation_identifier_out_of_the_card_value()
    {
        var summary = OverviewDiagnosticSummary.From(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            new IndexRecoverySummary("RetryScheduled", "ffffffff-1111-2222-3333-444444444444", null, null, "TransientIo", 0));

        Assert.Equal("Recovering", summary.State);
        Assert.Equal("Retry scheduled after a transient I/O failure.", summary.Reason);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", summary.ActiveGenerationDiagnostic);
        Assert.Equal("ffffffff-1111-2222-3333-444444444444", summary.RecoveryGenerationDiagnostic);
    }

    [Fact]
    public async Task An_initial_scan_shows_queued_source_work_without_guessing_file_or_job_totals()
    {
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        var root = Guid.NewGuid();
        var activity = new PipelineActivitySummary(now, now,
            [new(root, "Initial source", "C:\\workspace", "Enabled", 0, 0, 0, 0, 0, 0, 0, 0,
                null, null, null, false, null, QueuedScans: 1)], [], [], 0, 0, 0);
        using var factory = new OverviewApplicationFactory(CreateOverview(0, 0) with { Activity = activity });
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("Queued", html, StringComparison.Ordinal);
        Assert.Contains("1 queued source scans", html, StringComparison.Ordinal);
        Assert.Contains("file discovery has not completed", html, StringComparison.Ordinal);
        Assert.Contains("Changes not yet discovered by a scan are not counted", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No current processing backlog", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_diagnostic_summary_maps_actual_recovering_state_to_recovering()
    {
        var summary = OverviewDiagnosticSummary.From(
            "generation-a",
            new IndexRecoverySummary("Recovering", "generation-a", null, null, "TransientIo", 0));

        Assert.Equal("Recovering", summary.State);
        Assert.Equal("Retry scheduled after a transient I/O failure.", summary.Reason);
    }

    [Fact]
    public async Task Overview_renders_opaque_generation_identifiers_only_inside_copyable_diagnostics()
    {
        using var factory = new OverviewApplicationFactory();
        using var client = factory.CreateClient();

        var markup = await client.GetStringAsync("/");

        Assert.Contains("Search-index health", markup, StringComparison.Ordinal);
        Assert.Contains("<strong>Healthy</strong>", markup, StringComparison.Ordinal);
        Assert.Contains("Copyable index diagnostics", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Active index generation</dt>", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Recovery generation</dt>", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_initialization_buffers_an_index_recovery_event_published_during_the_first_projection_read()
    {
        var initialRecovery = new IndexRecoverySummary(
            "RetryScheduled",
            "generation-a",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T08:00:05Z"),
            "TransientIo",
            3);
        var recovered = new IndexRecoverySummary(
            "Healthy",
            "generation-b",
            DateTimeOffset.Parse("2026-07-27T08:01:00Z"),
            null,
            null,
            4);
        var reader = new FirstReadBlockingProjectionReader(CreateOverview(1, 1, initialRecovery));
        var state = new OverviewProjectionState(reader);
        var feed = new StatusEventFeed();

        var initialize = state.SubscribeAndReloadAsync(feed, CancellationToken.None).AsTask();
        await reader.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await feed.PublishAsync(
            new StatusChanged(null, "index-recovery", DateTimeOffset.UtcNow),
            CancellationToken.None);
        reader.Replace(CreateOverview(1, 2, recovered));
        reader.CompleteFirstRead();

        await using var subscription = await initialize;
        var statusChanged = await subscription.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await state.HandleStatusChangedAsync(statusChanged, CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
        Assert.Equal("Healthy", state.Current.IndexRecovery.State);
        Assert.Equal("generation-b", state.Current.IndexRecovery.ActiveGeneration);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T08:01:00Z"), state.Current.IndexRecovery.LastCompletedAtUtc);
        Assert.Equal(4, state.Current.IndexRecovery.CleanedCandidateCount);
    }

    [Fact]
    public async Task Overview_state_reloads_the_SQL_projection_after_a_status_event()
    {
        var reader = new FakeProjectionReader(
            CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(2, 2));
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "pipeline", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

    [Fact]
    public async Task A_read_that_changed_midway_cannot_replace_a_consistent_snapshot()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 7));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        reader.Replace(CreateOverview(99, 99) with
        {
            Activity = new PipelineActivitySummary(now, now, [], [], [], 0, 0, 0) { IsConsistent = false }
        });
        await state.ReloadAsync(CancellationToken.None);
        Assert.Equal(7, state.Current.IndexedRecordCount);
        Assert.True(state.IsStale);
    }

    [Fact]
    public async Task Failed_refresh_retains_the_last_successful_snapshot_and_marks_it_stale()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 7));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        reader.FailReads = true;

        await state.ReloadAsync(CancellationToken.None);

        Assert.Equal(7, state.Current.IndexedRecordCount);
        Assert.True(state.HasSnapshot);
        Assert.True(state.IsStale);
        Assert.NotNull(state.LastSuccessfulReadAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exhausted_read_budget_marks_status_busy_without_inventing_a_successful_snapshot(bool hasSnapshot)
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 7));
        var state = new OverviewProjectionState(reader);
        if (hasSnapshot) await state.ReloadAsync(CancellationToken.None);
        var previous = state.Current;
        var previousTimestamp = state.LastSuccessfulReadAtUtc;
        reader.Replace(CreateOverview(99, 99));
        reader.ReadFailure = new OverviewReadBudgetExceededException(new OperationCanceledException());

        await state.ReloadAsync(CancellationToken.None);

        Assert.Same(previous, state.Current);
        Assert.Equal(hasSnapshot, state.HasSnapshot);
        Assert.Equal(previousTimestamp, state.LastSuccessfulReadAtUtc);
        Assert.True(state.IsStale);
        Assert.Equal("Status is busy. Waiting for a consistent sample.", state.RefreshIssue);

        reader.ReadFailure = null;
        await state.ReloadAsync(CancellationToken.None);
        Assert.Equal(99, state.Current.IndexedRecordCount);
        Assert.True(state.HasSnapshot);
        Assert.False(state.IsStale);
        Assert.Null(state.RefreshIssue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Genuine_timeouts_and_database_errors_remain_refresh_failures(bool databaseError)
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 7));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        var previousTimestamp = state.LastSuccessfulReadAtUtc;
        reader.ReadFailure = databaseError ? new ProjectionDbException() : new TimeoutException("SQL deadline");

        await state.ReloadAsync(CancellationToken.None);

        Assert.Equal(7, state.Current.IndexedRecordCount);
        Assert.True(state.HasSnapshot);
        Assert.True(state.IsStale);
        Assert.Equal(previousTimestamp, state.LastSuccessfulReadAtUtc);
        Assert.Equal("Status refresh failed.", state.RefreshIssue);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_presented_as_busy_or_failed_status()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeProjectionReader(CreateOverview(1, 7))
        {
            ReadFailure = new OperationCanceledException(cancellation.Token)
        };
        var state = new OverviewProjectionState(reader);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.ReloadAsync(cancellation.Token).AsTask());
        Assert.False(state.HasSnapshot);
        Assert.Null(state.LastSuccessfulReadAtUtc);
        Assert.Null(state.RefreshIssue);
    }

    [Fact]
    public async Task An_initial_busy_read_renders_explicit_unavailability_without_a_refresh_error()
    {
        using var factory = new OverviewApplicationFactory(CreateOverview(1, 7),
            readFailure: new OverviewReadBudgetExceededException(new OperationCanceledException()));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Status is busy. Waiting for a consistent sample.", html, StringComparison.Ordinal);
        Assert.Contains("Current activity is unavailable; zero activity has not been confirmed.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Status refresh failed.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Status as of", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The last successful read may be out of date.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_busy_read_can_render_only_the_labelled_last_snapshot_and_its_original_timestamp()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 7));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        var timestamp = state.LastSuccessfulReadAtUtc;
        reader.ReadFailure = new OverviewReadBudgetExceededException(new OperationCanceledException());
        using var factory = new OverviewApplicationFactory(CreateOverview(99, 99), projectionState: state);
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Status is busy. Waiting for a consistent sample.", html, StringComparison.Ordinal);
        Assert.Contains("Status as of", html, StringComparison.Ordinal);
        Assert.Contains("The last successful read is shown below and may be out of date.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Status refresh failed.", html, StringComparison.Ordinal);
        Assert.Equal(7, state.Current.IndexedRecordCount);
        Assert.Equal(timestamp, state.LastSuccessfulReadAtUtc);
    }

    [Fact]
    public async Task Initial_read_failure_stays_unknown_and_a_later_refresh_recovers()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 7)) { FailReads = true };
        var state = new OverviewProjectionState(reader);
        await using var subscription = await state.SubscribeAndReloadAsync(new StatusEventFeed(), CancellationToken.None);
        Assert.False(state.HasSnapshot);
        Assert.True(state.IsStale);
        reader.FailReads = false;
        await state.ReloadAsync(CancellationToken.None);
        Assert.True(state.HasSnapshot);
        Assert.False(state.IsStale);
        Assert.Equal(7, state.Current.IndexedRecordCount);
    }

    [Fact]
    public async Task Overview_state_reloads_after_a_source_status_event()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(2, 2));

        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "sources", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

    [Fact]
    public async Task Overview_state_reloads_after_an_index_recovery_status_event()
    {
        var reader = new FakeProjectionReader(
            CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(1, 2));
        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "index-recovery", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.IndexedRecordCount);
    }

    [Fact]
    public async Task Overview_initial_load_includes_the_GPU_scheduler_SQL_projection()
    {
        var expected = new GpuSchedulerStatusProjection(
            3,
            1,
            2,
            1,
            new GpuSchedulerLaneCounts(2, 1, 0, 0, 0),
            true,
            "InteractiveRetrieval",
            2,
            1,
            1,
            DateTimeOffset.Parse("2026-07-30T12:05:00+00:00"),
            new GpuCapacityUncertaintySummary("Uncertain", 45));
        var reader = new FakeProjectionReader(CreateOverview(1, 1, gpuSchedulerStatus: expected));
        var state = new OverviewProjectionState(reader);

        await state.ReloadAsync(CancellationToken.None);

        Assert.Equal(expected, state.Current.GpuSchedulerStatus);
        Assert.Equal(1, reader.ReadOverviewCount);
    }

    [Fact]
    public async Task Overview_state_reloads_the_GPU_scheduler_SQL_projection_after_a_scheduler_event()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(
            1,
            1,
            gpuSchedulerStatus: new GpuSchedulerStatusProjection(
                2,
                1,
                1,
                0,
                new GpuSchedulerLaneCounts(1, 1, 0, 0, 0),
                true,
                "DocumentIndexing",
                1,
                1,
                0,
                null,
                GpuCapacityUncertaintySummary.None)));

        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "gpu-scheduler", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, state.Current.GpuSchedulerStatus.ReadyCount);
        Assert.Equal("DocumentIndexing", state.Current.GpuSchedulerStatus.ActiveBatchLane);
        Assert.Equal(2, reader.ReadOverviewCount);
    }

    [Fact]
    public async Task Overview_reconnect_reloads_the_GPU_scheduler_projection_from_SQL()
    {
        var reader = new FakeProjectionReader(CreateOverview(1, 1));
        var state = new OverviewProjectionState(reader);
        await state.ReloadAsync(CancellationToken.None);
        reader.Replace(CreateOverview(
            1,
            1,
            gpuSchedulerStatus: new GpuSchedulerStatusProjection(
                4,
                0,
                0,
                1,
                new GpuSchedulerLaneCounts(4, 0, 0, 0, 0),
                false,
                null,
                2,
                0,
                1,
                null,
                new GpuCapacityUncertaintySummary("Uncertain", null))));

        await state.HandleStatusChangedAsync(
            new StatusChanged(null, "reconnect", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(4, state.Current.GpuSchedulerStatus.ReadyCount);
        Assert.False(state.Current.GpuSchedulerStatus.HasActiveBatch);
        Assert.Null(state.Current.GpuSchedulerStatus.NextDeferredAtUtc);
        Assert.Equal(2, reader.ReadOverviewCount);
    }

    [Fact]
    public async Task Overview_renders_absent_scheduler_batch_and_retry_without_a_scheduler_control()
    {
        using var factory = new OverviewApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Local GPU scheduler", html, StringComparison.Ordinal);
        Assert.Contains("Scheduler active batch</dt><dd>None</dd>", html, StringComparison.Ordinal);
        Assert.Contains("Scheduler next retry</dt><dd>None</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Scheduler active batch lane", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Scheduler uncertain capacity age", html, StringComparison.Ordinal);
        Assert.Contains("Refresh status", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Reset GPU", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
    }

    private static OverviewProjection CreateOverview(
        int workerQueuedCount,
        int indexedRecordCount,
        IndexRecoverySummary? recovery = null,
        GpuSchedulerStatusProjection? gpuSchedulerStatus = null) =>
        new(
            workerQueuedCount,
            0,
            0,
            0,
            0,
            0,
            indexedRecordCount,
            "generation-a",
            recovery ?? new IndexRecoverySummary("Healthy", "generation-a", null, null, null, 0))
        {
            GpuSchedulerStatus = gpuSchedulerStatus ?? GpuSchedulerStatusProjection.Empty
        };

    private sealed class FakeProjectionReader(OverviewProjection current) : IProjectionReader
    {
        public OverviewProjection Current { get; private set; } = current;

        public int ReadOverviewCount { get; private set; }

        public bool FailReads { get; set; }

        public Exception? ReadFailure { get; set; }

        public void Replace(OverviewProjection projection) => Current = projection;

        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
        {
            ReadOverviewCount++;
            if (ReadFailure is { } failure) throw failure;
            if (FailReads) throw new InvalidOperationException("Simulated projection failure");
            return ValueTask.FromResult(Current);
        }

        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Current.GpuSchedulerStatus);

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(null);
    }

    private sealed class FirstReadBlockingProjectionReader(OverviewProjection current) : IProjectionReader
    {
        private readonly TaskCompletionSource _firstReadMayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OverviewProjection Current { get; private set; } = current;

        public void Replace(OverviewProjection projection) => Current = projection;

        public void CompleteFirstRead() => _firstReadMayComplete.SetResult();

        public async ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
        {
            var snapshot = Current;
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstReadStarted.SetResult();
                await _firstReadMayComplete.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }

        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Current.GpuSchedulerStatus);

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(null);
    }

    private sealed class ProjectionDbException() : DbException("SQL read failed");

    private sealed class OverviewApplicationFactory(OverviewProjection? projection = null, bool failReads = false,
        Exception? readFailure = null, OverviewProjectionState? projectionState = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(
                "ConnectionStrings:FluxKnowledge",
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                "Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting(
                "Usearch:RootPath",
                Path.Combine(Path.GetTempPath(), "FluxKnowledgeOverviewProjectionTests"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProjectionReader>();
                if (projection is null) services.AddScoped<IProjectionReader, NoActiveSchedulerProjectionReader>();
                else services.AddScoped<IProjectionReader>(_ => new FakeProjectionReader(projection)
                    { FailReads = failReads, ReadFailure = readFailure });
                if (projectionState is not null)
                {
                    services.RemoveAll<OverviewProjectionState>();
                    services.AddScoped(_ => projectionState);
                }
            });
        }
    }

    private sealed class NoActiveSchedulerProjectionReader : IProjectionReader
    {
        private static readonly OverviewProjection Overview = CreateOverview(
            0,
            0,
            gpuSchedulerStatus: GpuSchedulerStatusProjection.Empty);

        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Overview);

        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GpuSchedulerStatusProjection.Empty);

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(null);
    }
}
