using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.OperatorActions;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OperatorActionPageStateTests
{
    private static readonly OperatorActionProjection Action = new(
        new string('a', 64), new string('c', 64), null, "AQIDBAUGBwg=", "document-ooxml-structural-extract", "blocked",
        "operator-action-retryable-test", null, DateTimeOffset.UnixEpoch, null, null, null,
        OverrideAvailable: false, RetryAvailable: true, Ignored: false);

    [Fact]
    public async Task Include_ignored_reloads_the_public_projection_with_the_selected_filter()
    {
        var store = new RecordingStore([Action]);
        var state = new OperatorActionPageState(
            new OperatorActionService(store, new NullPublisher(), TimeProvider.System),
            new AllowPolicy());

        await state.SetIncludeIgnoredAsync(true, CancellationToken.None);

        Assert.True(state.IncludeIgnored);
        Assert.True(store.LastIncludeIgnored);
        Assert.Single(state.Actions);
    }

    [Fact]
    public async Task Retry_uses_only_the_opaque_action_binding_and_reloads_after_commit()
    {
        var store = new RecordingStore([Action]);
        var state = new OperatorActionPageState(
            new OperatorActionService(store, new NullPublisher(), TimeProvider.System),
            new AllowPolicy());

        await state.ExecuteAsync(Action, "retry", CancellationToken.None);

        Assert.NotNull(store.Command);
        Assert.Equal(Action.ActionId, store.Command.ActionId);
        Assert.Equal(Action.BlockedRowVersionToken, store.Command.ExpectedBlockedRowVersion);
        Assert.DoesNotContain("source", System.Text.Json.JsonSerializer.Serialize(store.Command), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task A_non_forceable_action_remains_independently_ignoreable_from_the_page_state()
    {
        var nonForceable = Action with
        {
            ActionState = "descriptor-disabled",
            OverrideAvailable = false,
            RetryAvailable = false
        };
        var store = new RecordingStore([nonForceable]);
        var state = new OperatorActionPageState(
            new OperatorActionService(store, new NullPublisher(), TimeProvider.System),
            new AllowPolicy());

        var receipt = await state.ExecuteAsync(nonForceable, "ignore", CancellationToken.None);

        Assert.Equal("ignore", store.Command!.ActionKind);
        Assert.Equal(nonForceable.ActionId, store.Command.ActionId);
        Assert.True(receipt.Ignored);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task Non_loopback_circuit_cannot_execute_an_operator_action()
    {
        var store = new RecordingStore([Action]);
        var state = new OperatorActionPageState(
            new OperatorActionService(store, new NullPublisher(), TimeProvider.System),
            new DenyPolicy());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await state.ExecuteAsync(Action, "retry", CancellationToken.None));

        Assert.Null(store.Command);
    }

    private sealed class RecordingStore(IReadOnlyList<OperatorActionProjection> actions) : IOperatorActionStore
    {
        public bool LastIncludeIgnored { get; private set; }
        public int ReadCount { get; private set; }
        public OperatorActionMutationCommand? Command { get; private set; }

        public ValueTask<IReadOnlyList<OperatorActionProjection>> ListAsync(bool includeIgnored, int maximumCount, CancellationToken cancellationToken)
        {
            LastIncludeIgnored = includeIgnored;
            ReadCount++;
            return ValueTask.FromResult(actions);
        }

        public ValueTask<OperatorActionMutationReceipt> ExecuteAsync(OperatorActionMutationCommand command, CancellationToken cancellationToken)
        {
            Command = command;
            return ValueTask.FromResult(new OperatorActionMutationReceipt(
                command.ActionId, command.OperationId, null,
                command.ActionKind == "ignore" ? "ignored" : "requested",
                command.ActionKind == "ignore" ? 1 : null,
                command.ActionKind == "ignore" ? true : null,
                false, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class AllowPolicy : ILocalOperatorPolicy
    {
        public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DenyPolicy : ILocalOperatorPolicy
    {
        public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException(new UnauthorizedAccessException());
    }

    private sealed class NullPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
