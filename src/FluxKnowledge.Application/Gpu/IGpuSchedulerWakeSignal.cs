using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

public interface IGpuSchedulerWakeSignal
{
    void Notify(GpuSchedulerWakeReason reason);

    ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken);
}
