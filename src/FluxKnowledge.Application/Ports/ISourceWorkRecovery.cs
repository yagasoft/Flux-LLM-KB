namespace FluxKnowledge.Application.Ports;

public interface ISourceWorkRecovery
{
    ValueTask AdvanceAsync(CancellationToken cancellationToken);
}
