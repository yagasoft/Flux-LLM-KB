namespace FluxKnowledge.Application.Sources;

/// <summary>Advisory intake wakeups have their own bounded channel and single consumer.</summary>
public interface ISourceJournalWakeSignal
{
    void Notify();
    ValueTask WaitAsync(CancellationToken cancellationToken);
}

public sealed class ChannelSourceJournalWakeSignal : ISourceJournalWakeSignal
{
    private readonly ChannelSourceScanWakeSignal _signal = new();
    public void Notify() => _signal.Notify();
    public ValueTask WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);
}
