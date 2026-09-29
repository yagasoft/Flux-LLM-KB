using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

/// <summary>CPU is entered only after GPU admission proves that no request was handed off.</summary>
public sealed class GpuFirstPassageInference : IScheduledPassageInference
{
    private readonly IConditionalGpuPassageInference _gpu;
    private readonly IScheduledPassageInference _cpu;

    public GpuFirstPassageInference(IConditionalGpuPassageInference gpu, IScheduledPassageInference cpu)
    {
        if (gpu.EmbeddingProfile != cpu.EmbeddingProfile || gpu.RerankerFingerprint != cpu.RerankerFingerprint)
            throw new ArgumentException("gpu-cpu-search-profile-mismatch");
        _gpu = gpu;
        _cpu = cpu;
    }

    public EmbeddingProfile EmbeddingProfile => _gpu.EmbeddingProfile;
    public string RerankerFingerprint => _gpu.RerankerFingerprint;

    public async ValueTask<T> ExecuteAsync<T>(
        Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken)
    {
        try { return await _gpu.ExecuteWhenIdleAsync(work, cancellationToken).ConfigureAwait(false); }
        catch (GpuInteractiveBusyWithoutHandoffException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _cpu.ExecuteAsync(work, cancellationToken).ConfigureAwait(false);
        }
    }
}
