namespace FluxKnowledge.Application.Ports;

public sealed record SourceCaptureJournalSeal(long Usn, SourceFileObservation Observation);
public sealed class SourceCaptureSealUnavailableException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>Seals the bytes of a still-owned physical capture without releasing its original guard.</summary>
public interface IJournalSealableSourceCapture : ISourceFileCapture
{
    ValueTask<SourceCaptureJournalSeal> SealAsync(CancellationToken cancellationToken);
}
