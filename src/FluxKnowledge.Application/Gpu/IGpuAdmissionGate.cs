namespace FluxKnowledge.Application.Gpu;

public interface IGpuAdmissionGate
{
    ValueTask<GpuAdmissionDecision> DecideAsync(
        GpuBatchCandidate candidate,
        CancellationToken cancellationToken);
}
