using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceActivityTests
{
    [Fact]
    public void Execution_classes_are_exactly_the_three_approved_descriptors()
    {
        Assert.Equal(
            new[]
            {
                ExecutionClass.InProcess,
                ExecutionClass.DeferredCapability,
                ExecutionClass.NativeExecutorLater
            },
            Enum.GetValues<ExecutionClass>());
    }

    [Fact]
    public void Activity_key_is_stable_for_duplicate_drafts_and_changes_for_each_identity_component()
    {
        var revision = SourceRevisionId.New();
        var duplicate = SourceActivity.Create(
            revision,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "utf8-v1",
            "sha256:input-a",
            requiredCapability: "text/utf8",
            reason: null);
        var sameDraftWithDifferentExplanation = SourceActivity.Create(
            revision,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "utf8-v1",
            "sha256:input-a",
            requiredCapability: "text/utf8-v2",
            reason: "operator note");
        var changedRevision = SourceActivity.Create(
            SourceRevisionId.New(),
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "utf8-v1",
            "sha256:input-a",
            requiredCapability: "text/utf8",
            reason: null);
        var changedKind = SourceActivity.Create(
            revision,
            SourceActivityKind.MetadataExtraction,
            ExecutionClass.InProcess,
            "utf8-v1",
            "sha256:input-a",
            requiredCapability: "text/utf8",
            reason: null);
        var changedProcessor = SourceActivity.Create(
            revision,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "utf8-v2",
            "sha256:input-a",
            requiredCapability: "text/utf8",
            reason: null);
        var changedInput = SourceActivity.Create(
            revision,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "utf8-v1",
            "sha256:input-b",
            requiredCapability: "text/utf8",
            reason: null);

        Assert.Equal(duplicate.IdempotencyKey, sameDraftWithDifferentExplanation.IdempotencyKey);
        Assert.NotEqual(duplicate.IdempotencyKey, changedRevision.IdempotencyKey);
        Assert.NotEqual(duplicate.IdempotencyKey, changedKind.IdempotencyKey);
        Assert.NotEqual(duplicate.IdempotencyKey, changedProcessor.IdempotencyKey);
        Assert.NotEqual(duplicate.IdempotencyKey, changedInput.IdempotencyKey);
    }

    [Fact]
    public void Activity_key_does_not_collide_when_an_identity_component_contains_a_delimiter()
    {
        var revision = SourceRevisionId.New();
        var processorContainsDelimiter = SourceActivity.Create(
            revision,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "a|b",
            "c",
            requiredCapability: null,
            reason: null);
        var fingerprintContainsDelimiter = SourceActivity.Create(
            revision,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "a",
            "b|c",
            requiredCapability: null,
            reason: null);

        Assert.NotEqual(processorContainsDelimiter.IdempotencyKey, fingerprintContainsDelimiter.IdempotencyKey);
    }

    [Theory]
    [InlineData(SourceActivityState.DeferredUnsupported)]
    [InlineData(SourceActivityState.DeferredPolicy)]
    public void Deferred_activities_do_not_become_pending_when_elapsed_time_is_reconsidered(SourceActivityState deferredState)
    {
        var activity = SourceActivity.Create(
            SourceRevisionId.New(),
            SourceActivityKind.DocumentParsing,
            ExecutionClass.DeferredCapability,
            "document-v1",
            "sha256:input",
            requiredCapability: "document/parser",
            reason: "capability is not registered",
            initialState: deferredState);

        var reconsidered = deferredState == SourceActivityState.DeferredUnsupported
            ? activity.DeferUnsupported("capability is not registered").ReconsiderAfterElapsedTime()
            : activity.DeferPolicy("root policy excludes this document").ReconsiderAfterElapsedTime();

        Assert.Equal(deferredState, reconsidered.State);
        Assert.NotEqual(SourceActivityState.Pending, reconsidered.State);
    }

    [Fact]
    public void Deferred_running_activity_can_only_be_restored_when_a_durable_pipeline_receipt_is_proven()
    {
        var revision = SourceRevisionId.New();

        Assert.Throws<DomainInvariantException>(() => SourceActivity.Restore(
            SourceActivityId.New(), revision, SourceActivityKind.TextExtraction, ExecutionClass.DeferredCapability,
            "phase-3a-v1", new string('d', 64), "text-metadata", SourceActivityState.Running, null));

        var restored = SourceActivity.Restore(
            SourceActivityId.New(), revision, SourceActivityKind.TextExtraction, ExecutionClass.DeferredCapability,
            "phase-3a-v1", new string('d', 64), "text-metadata", SourceActivityState.Running, null,
            hasDurablePipelineReceipt: true);

        Assert.Equal(SourceActivityState.Running, restored.State);
    }

    [Fact]
    public void Native_executor_later_rejects_a_runnable_activity()
    {
        Assert.Throws<DomainInvariantException>(
            () => SourceActivity.Create(
                SourceRevisionId.New(),
                SourceActivityKind.DocumentParsing,
                ExecutionClass.NativeExecutorLater,
                "native-v1",
                "sha256:input",
                requiredCapability: "native/document",
                reason: "native executor is not implemented",
                initialState: SourceActivityState.Pending));
    }

    [Fact]
    public void Native_executor_later_is_represented_as_non_runnable_by_default()
    {
        var activity = SourceActivity.Create(
            SourceRevisionId.New(),
            SourceActivityKind.DocumentParsing,
            ExecutionClass.NativeExecutorLater,
            "native-v1",
            "sha256:input",
            requiredCapability: "native/document",
            reason: "native executor is not implemented");

        Assert.Equal(SourceActivityState.DeferredUnsupported, activity.State);
    }

    [Fact]
    public void Restore_preserves_the_persisted_activity_identity_for_an_idempotent_lookup()
    {
        var id = SourceActivityId.New();
        var revisionId = SourceRevisionId.New();

        var activity = SourceActivity.Restore(
            id,
            revisionId,
            SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess,
            "utf8-v1",
            "sha256:input",
            requiredCapability: "text/utf8",
            state: SourceActivityState.Completed,
            reason: null);

        Assert.Equal(id, activity.Id);
        Assert.Equal(revisionId, activity.SourceRevisionId);
        Assert.Equal(SourceActivityState.Completed, activity.State);
    }
}
