namespace FluxKnowledge.Application.Ports;

public interface ISourceWorkRetirement
{
    ValueTask<int> RetireAsync(CancellationToken cancellationToken);
}
