namespace FluxKnowledge.Application.Gpu;

public interface IGpuExecutorAdapter
{
    string ExecutorKey { get; }

    ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken);
}
