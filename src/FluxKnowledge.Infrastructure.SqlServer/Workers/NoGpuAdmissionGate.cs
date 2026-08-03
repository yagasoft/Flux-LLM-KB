using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// The safe default until a trusted capacity provider is explicitly installed.
/// </summary>
public sealed class NoGpuAdmissionGate : IGpuAdmissionGate
{
    public ValueTask<GpuAdmissionDecision> DecideAsync(
        GpuBatchCandidate candidate,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
}
