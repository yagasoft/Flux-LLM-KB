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

        Assert.Contains("Index status</dt><dd>Healthy</dd>", markup, StringComparison.Ordinal);
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
        Assert.DoesNotContain("<button", html, StringComparison.OrdinalIgnoreCase);
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

        public void Replace(OverviewProjection projection) => Current = projection;

        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
        {
            ReadOverviewCount++;
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

    private sealed class OverviewApplicationFactory : WebApplicationFactory<Program>
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
                services.AddScoped<IProjectionReader, NoActiveSchedulerProjectionReader>();
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
