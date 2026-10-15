namespace FluxKnowledge.Application.Sources;

/// <summary>A safe provider failure category; diagnostic detail stays in the inner exception.</summary>
public sealed class SourceDeletionFailureException(string reasonCode, Exception innerException)
    : Exception("Source deletion could not complete its current cleanup unit.", innerException)
{
    public string ReasonCode { get; } = reasonCode;
}
