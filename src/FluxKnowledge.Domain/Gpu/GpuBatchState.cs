namespace FluxKnowledge.Domain.Gpu;

public enum GpuBatchState
{
    Active,
    AtSafeBoundary,
    Completed,
    Released,
    CapacityUncertain
}
