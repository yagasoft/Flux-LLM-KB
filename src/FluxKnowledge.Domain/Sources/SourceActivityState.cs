namespace FluxKnowledge.Domain.Sources;

public enum SourceActivityState
{
    Pending,
    Running,
    Completed,
    DeferredUnsupported,
    DeferredPolicy,
    FailedRetryable,
    FailedTerminal,
    CancelledSuperseded
}
