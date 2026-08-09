namespace FluxKnowledge.Domain.Sources;

public enum SourceScanRequestState
{
    Held,
    Released,
    Running,
    Completed,
    Failed
}
