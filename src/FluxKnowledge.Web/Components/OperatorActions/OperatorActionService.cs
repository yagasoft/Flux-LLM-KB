using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.AspNetCore.Http;

namespace FluxKnowledge.Web.Components.OperatorActions;

public sealed class OperatorActionService(
    IOperatorActionStore store,
    IStatusEventPublisher statusEvents,
    TimeProvider timeProvider)
{
    public ValueTask<IReadOnlyList<OperatorActionProjection>> ListAsync(
        bool includeIgnored,
        CancellationToken cancellationToken) => store.ListAsync(includeIgnored, 100, cancellationToken);

    public async ValueTask<OperatorActionMutationReceipt> ExecuteAsync(
        OperatorActionMutationCommand command,
        CancellationToken cancellationToken)
    {
        var receipt = await store.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (!receipt.WasReplay)
        {
            await statusEvents.PublishAsync(
                new StatusChanged(null, "operator-actions", timeProvider.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
        }

        return receipt;
    }
}

public interface ILocalOperatorPolicy
{
    ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken);
}

public sealed class LocalOperatorConnectionContext(IHttpContextAccessor accessor)
{
    public bool IsDirectLoopback { get; } = accessor.HttpContext is { } context &&
        LocalOperatorLoopbackGate.IsDirectLoopback(context);
}

public sealed class LocalOperatorPolicy(LocalOperatorConnectionContext connection) : ILocalOperatorPolicy
{
    public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return connection.IsDirectLoopback
            ? ValueTask.CompletedTask
            : ValueTask.FromException(new UnauthorizedAccessException("A direct loopback connection is required for operator actions."));
    }
}

public sealed class OperatorActionPageState(
    OperatorActionService service,
    ILocalOperatorPolicy operatorPolicy)
{
    public IReadOnlyList<OperatorActionProjection> Actions { get; private set; } = [];
    public bool IncludeIgnored { get; private set; }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        Actions = await service.ListAsync(IncludeIgnored, cancellationToken).ConfigureAwait(false);

    public async ValueTask SetIncludeIgnoredAsync(bool includeIgnored, CancellationToken cancellationToken)
    {
        IncludeIgnored = includeIgnored;
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OperatorActionMutationReceipt> ExecuteAsync(
        OperatorActionProjection action,
        string actionKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await service.ExecuteAsync(new OperatorActionMutationCommand(
            action.ActionId,
            Guid.NewGuid(),
            action.RequestFingerprint,
            action.BlockedRowVersionToken,
            actionKind), cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }
}
