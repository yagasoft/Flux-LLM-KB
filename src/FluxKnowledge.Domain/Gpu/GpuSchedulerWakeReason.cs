namespace FluxKnowledge.Domain.Gpu;

[Flags]
public enum GpuSchedulerWakeReason
{
    WorkReady = 1 << 0,
    SafeBoundary = 1 << 1,
    CapacityReleased = 1 << 2,
    DeferredRetry = 1 << 3,
    StartupRecovery = 1 << 4,
    Reconciliation = 1 << 5
}
