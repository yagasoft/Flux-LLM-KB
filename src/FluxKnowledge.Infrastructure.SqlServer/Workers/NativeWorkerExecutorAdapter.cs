using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// Private scheduler adapter that delegates only durable opaque handles to the supervisor.
/// </summary>
public sealed class NativeWorkerExecutorAdapter(
    NativeWorkerOptions options,
    NativeWorkerSupervisorService supervisor) : IGpuExecutorAdapter
{
    public string ExecutorKey => options.ExecutorKey
        ?? throw new InvalidOperationException("Enabled native worker options require an executor key.");

    public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken) =>
        supervisor.DeliverAsync(handle, cancellationToken);
}
