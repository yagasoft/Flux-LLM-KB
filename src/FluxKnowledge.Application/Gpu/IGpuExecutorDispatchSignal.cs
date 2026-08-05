namespace FluxKnowledge.Application.Gpu;

/// <summary>
/// Local, payload-free prompt to reread durable pending executor dispatches.
/// </summary>
public interface IGpuExecutorDispatchSignal
{
    void Notify();
}
