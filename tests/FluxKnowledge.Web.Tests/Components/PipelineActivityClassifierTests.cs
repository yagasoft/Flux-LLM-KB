using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class PipelineActivityClassifierTests
{
    [Theory]
    [InlineData(PublicJobState.WorkerQueued)]
    [InlineData(PublicJobState.GpuProcessing)]
    [InlineData(PublicJobState.Failed)]
    public void Suppressed_or_superseded_work_remains_historical_without_current_ownership(PublicJobState state)
    {
        var actual = PipelineActivityClassifier.Classify(new(state, IsHistorical: true));
        Assert.Equal(PipelineWorkState.Historical, actual.State);
    }

    [Fact]
    public void Previous_revision_work_with_a_confirmed_owner_still_appears_running()
    {
        var actual = PipelineActivityClassifier.Classify(new(PublicJobState.GpuProcessing,
            IsHistorical: true, HasActiveGpu: true));
        Assert.Equal(PipelineWorkState.Running, actual.State);
    }

    [Fact]
    public void Worker_processing_requires_a_current_lease()
    {
        Assert.Equal(PipelineWorkState.Running, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerProcessing, HasWorkerLease: true)).State);
        Assert.Equal(PipelineWorkState.Attention, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerProcessing)).State);
    }

    [Fact]
    public void Gpu_processing_label_with_no_owned_task_is_attention_not_running()
    {
        Assert.Equal(PipelineWorkState.Attention, PipelineActivityClassifier.Classify(
            new(PublicJobState.GpuProcessing, HasUncertainGpu: true)).State);
    }

    [Fact]
    public void Paused_and_retry_work_waits_instead_of_inflating_the_runnable_queue()
    {
        Assert.Equal(PipelineWorkState.Waiting, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerQueued, SourceAdmitted: false)).State);
        Assert.Equal(PipelineWorkState.Waiting, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerQueued, IsDue: false)).State);
        Assert.Equal(PipelineWorkState.Queued, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerQueued)).State);
    }

    [Fact]
    public void A_compatible_new_ready_task_remains_queued_while_prior_uncertain_outcomes_stay_recorded()
    {
        Assert.Equal(PipelineWorkState.Queued, PipelineActivityClassifier.Classify(
            new(PublicJobState.GpuQueued, HasReadyGpu: true, HasUncertainGpu: true)).State);
    }

    [Fact]
    public void Locally_unavailable_content_waits_without_becoming_a_runnable_queue_item()
    {
        Assert.Equal(PipelineWorkState.Waiting, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerQueued, Reason: "source-content-not-available-locally")).State);
    }

    [Fact]
    public void A_gpu_queue_label_without_a_ready_child_needs_attention()
    {
        Assert.Equal(PipelineWorkState.Attention, PipelineActivityClassifier.Classify(
            new(PublicJobState.GpuQueued)).State);
        Assert.Equal(PipelineWorkState.Queued, PipelineActivityClassifier.Classify(
            new(PublicJobState.GpuQueued, HasReadyGpu: true)).State);
    }

    [Theory]
    [InlineData("repository-source-blocked", PipelineWorkState.Attention)]
    [InlineData("repository-source-deferred", PipelineWorkState.Waiting)]
    public void Explicit_source_reasons_take_precedence_over_due_queue_labels(string reason, PipelineWorkState expected)
    {
        Assert.Equal(expected, PipelineActivityClassifier.Classify(
            new(PublicJobState.WorkerQueued, Reason: reason)).State);
    }
}
