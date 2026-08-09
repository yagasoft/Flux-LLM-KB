using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class RetainedTextActivityPlannerTests
{
    [Fact]
    public async Task Pending_in_process_text_activity_plans_one_pipeline_registration()
    {
        var store = new RecordingStore();
        var planner = new RetainedTextActivityPlanner(store);
        var activity = SourceActivity.Create(
            SourceRevisionId.New(), SourceActivityKind.TextExtraction, ExecutionClass.InProcess,
            "phase-3a-v1", new string('a', 64), null, null);

        var planned = await planner.PlanAsync(activity, CancellationToken.None);

        Assert.True(planned);
        Assert.Equal(activity.Id, Assert.Single(store.PlannedActivities).Id);
    }

    [Fact]
    public async Task Pending_in_process_metadata_activity_remains_on_the_existing_retained_text_registration_path()
    {
        var store = new RecordingStore();
        var planner = new RetainedTextActivityPlanner(store);
        var activity = SourceActivity.Create(
            SourceRevisionId.New(), SourceActivityKind.MetadataExtraction, ExecutionClass.InProcess,
            "phase-3a-v1", new string('c', 64), null, null);

        Assert.True(await planner.PlanAsync(activity, CancellationToken.None));
        Assert.Equal(activity.Id, Assert.Single(store.PlannedActivities).Id);
    }

    [Fact]
    public async Task Deferred_or_unknown_activity_never_dispatches_to_the_pipeline()
    {
        var store = new RecordingStore();
        var planner = new RetainedTextActivityPlanner(store);
        var deferred = SourceActivity.Create(
            SourceRevisionId.New(), SourceActivityKind.DocumentParsing, ExecutionClass.DeferredCapability,
            "phase-3a-v1", new string('b', 64), "document-parser", "not installed");

        var planned = await planner.PlanAsync(deferred, CancellationToken.None);

        Assert.False(planned);
        Assert.Empty(store.PlannedActivities);
    }

    private sealed class RecordingStore : IRetainedTextRegistrationStore
    {
        public List<SourceActivity> PlannedActivities { get; } = [];

        public ValueTask<bool> RegisterAsync(SourceActivity activity, CancellationToken cancellationToken)
        {
            PlannedActivities.Add(activity);
            return ValueTask.FromResult(true);
        }
    }
}
