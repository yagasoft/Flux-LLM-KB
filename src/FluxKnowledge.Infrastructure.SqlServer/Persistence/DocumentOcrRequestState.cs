namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Private lifecycle state for one source-bound local OCR request.</summary>
public enum DocumentOcrRequestState
{
    Pending = 0,
    ResultStored = 1,
    Requeued = 2
}
