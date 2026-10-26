namespace FluxKnowledge.Application.Ports;

/// <summary>Physical reader ownership; dispose only after every native request and content handle is terminal.</summary>
public interface ISourceVerificationReaderSlot
{
    IDisposable? TryAcquire();
}
