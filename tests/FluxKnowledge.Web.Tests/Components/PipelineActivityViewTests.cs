using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class PipelineActivityViewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
    private static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Idle_current_work_keeps_historical_jobs_and_uncertain_tasks_visible()
    {
        var summary = Snapshot(Source(0), [new(Root, "Workspace", "Embed", PipelineWorkState.Historical, true, "Suppressed", 31)],
            [new(null, "Interactive search", "Outcome uncertain", 178)]);
        var view = PipelineActivityView.From(summary);
        Assert.Equal("Idle", view.State);
        Assert.Equal(0, view.RunningJobs);
        Assert.Equal(31, view.HistoricalJobs);
        Assert.Equal(178, view.UncertainGpuTasks);
    }

    [Fact]
    public void Completing_earlier_work_with_remaining_files_is_catching_up_without_new_discovery()
    {
        var source = Source(4) with { LastDiscoveryAtUtc = Now.AddHours(-1), LastProgressAtUtc = Now.AddMinutes(-1) };
        var first = PipelineActivityView.From(Snapshot(source, [new(Root, "Workspace", "Publish", PipelineWorkState.Running, false, "Owned", 1)]));
        var next = PipelineActivityView.From(Snapshot(source with { PublishedFiles = 8, PendingFiles = 2 },
            [new(Root, "Workspace", "Publish", PipelineWorkState.Running, false, "Owned", 1)]));
        Assert.Equal("Catching up", first.State);
        Assert.Equal("Catching up", next.State);
        Assert.Equal(2, next.PendingFiles);
        Assert.Equal(8, next.PublishedFiles);
    }

    [Fact]
    public void A_current_blocked_file_prevents_an_all_caught_up_state()
    {
        var view = PipelineActivityView.From(Snapshot(Source(0) with { PublishedFiles = 9, BlockedFiles = 1 }));
        Assert.Equal("Blocked", view.State);
        Assert.Equal(1, view.BlockedFiles);
    }

    [Fact]
    public void Provisional_revision_inventory_cannot_be_reported_as_idle_or_complete()
    {
        var view = PipelineActivityView.From(Snapshot(Source(0) with
            { KnownFiles = 1, PublishedFiles = 0, ProvisionalFiles = 1 }));
        Assert.Equal("Blocked", view.State);
        Assert.Equal(1, view.ProvisionalFiles);
        Assert.Equal(0, view.PublishedFiles);
    }

    [Fact]
    public void Progress_in_another_source_does_not_claim_the_pending_source_is_catching_up()
    {
        var other = Guid.NewGuid();
        var summary = Snapshot(Source(3)) with
        {
            Sources = [Source(3), Source(0) with { Id = other, LastProgressAtUtc = Now }],
            Jobs = [new(other, "Other workspace", "Publish", PipelineWorkState.Running, false, "Owned", 1)]
        };
        Assert.Equal("Running", PipelineActivityView.From(summary).State);
    }

    [Fact]
    public void Live_previous_revision_work_does_not_claim_current_backlog_progress()
    {
        var summary = Snapshot(Source(3) with { LastProgressAtUtc = Now },
            [new(Root, "Workspace", "Publish", PipelineWorkState.Running, true, "Previous revision", 1)]);
        Assert.Equal("Running", PipelineActivityView.From(summary).State);
    }

    [Fact]
    public void Interactive_search_does_not_claim_pending_indexing_is_catching_up()
    {
        var summary = Snapshot(Source(3) with { LastProgressAtUtc = Now }, [],
            [new(null, "Interactive search", "Active", 1)]);
        Assert.Equal("Running", PipelineActivityView.From(summary).State);
    }

    [Fact]
    public void A_confirmed_source_operation_is_visible_even_without_pipeline_or_gpu_jobs()
    {
        var view = PipelineActivityView.From(Snapshot(Source(0) with { ActiveSourceOperations = 1, Lifecycle = "Deleting" }));
        Assert.Equal("Running", view.State);
    }

    [Fact]
    public void Retry_and_paused_work_is_waiting()
    {
        var view = PipelineActivityView.From(Snapshot(Source(3),
            [new(Root, "Workspace", "Extract", PipelineWorkState.Waiting, false, "Paused", 2)]));
        Assert.Equal("Waiting", view.State);
        Assert.Equal(2, view.WaitingJobs);
        Assert.Equal(0, view.QueuedJobs);
    }

    [Fact]
    public void Source_selection_does_not_attribute_interactive_gpu_work_to_that_source()
    {
        var summary = Snapshot(Source(0), [], [new(null, "Interactive search", "Active", 1)]);
        Assert.Equal(1, PipelineActivityView.From(summary).ActiveGpuTasks);
        Assert.Equal(0, PipelineActivityView.From(summary, Root).ActiveGpuTasks);
    }

    [Theory]
    [InlineData("Awaiting dispatch")]
    [InlineData("Deferred")]
    [InlineData("Held")]
    public void Interactive_GPU_work_waiting_for_dispatch_retry_or_admission_prevents_idle(string state)
    {
        var view = PipelineActivityView.From(Snapshot(Source(0), [], [new(null, "Interactive search", state, 1)]));
        Assert.Equal("Waiting", view.State);
        Assert.Equal(1, view.WaitingGpuTasks);
    }

    [Fact]
    public void Historical_GPU_bindings_remain_visible_without_a_current_job()
    {
        var view = PipelineActivityView.From(Snapshot(Source(0), [], [new(Root, "Workspace", "Historical unresolved", 3)]));
        Assert.Equal("Idle", view.State);
        Assert.Equal(3, view.HistoricalGpuTasks);
    }

    [Fact]
    public void Missing_and_unconfirmed_status_never_becomes_idle()
    {
        Assert.Equal("Status unavailable", PipelineActivityView.From(PipelineActivitySummary.Unknown).State);
        Assert.Equal("Needs attention", PipelineActivityView.From(Snapshot(Source(0), [],
            [new(null, "Unattributed", "Ownership unconfirmed", 1)])).State);
    }

    private static SourcePipelineProgress Source(int pending) => new(Root, "Workspace", "C:\\workspace", "Enabled",
        10, 10 - pending, pending, 0, 0, 0, 0, 0, Now, Now.AddHours(-1), null, false, null);

    private static PipelineActivitySummary Snapshot(SourcePipelineProgress source, IReadOnlyList<PipelineWorkGroup>? jobs = null,
        IReadOnlyList<GpuActivityGroup>? gpu = null) => new(Now, Now, [source], jobs ?? [], gpu ?? [], 0, 0, 0);
}
