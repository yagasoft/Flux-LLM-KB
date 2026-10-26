namespace FluxKnowledge.Application.Ports;

public enum SourceVerificationReadKind { Source, Retained }

public interface ISourceVerificationReadBudget
{
    ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken);

    /// <summary>Actual completed reads, including partial work from a failed or cancelled hash.</summary>
    void RecordRead(SourceVerificationReadKind kind, int bytes) { }
}
