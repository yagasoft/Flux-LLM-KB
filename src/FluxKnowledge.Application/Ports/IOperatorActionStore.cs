using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

/// <summary>Public sanitised projection and mutation entry point for direct local operators.</summary>
public interface IOperatorActionStore
{
    ValueTask<IReadOnlyList<OperatorActionProjection>> ListAsync(
        bool includeIgnored,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<OperatorActionMutationReceipt> ExecuteAsync(
        OperatorActionMutationCommand command,
        CancellationToken cancellationToken);
}
