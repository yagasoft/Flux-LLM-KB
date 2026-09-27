namespace FluxKnowledge.Application.Gpu;

public interface IGpuExecutorAdapter
{
    string ExecutorKey { get; }

    ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken);
}

/// <summary>Optional durable continuation recovery, run by the existing dispatch loop.</summary>
public interface IGpuExecutorRecoveryAdapter
{
    ValueTask RecoverAsync(CancellationToken cancellationToken);
}
